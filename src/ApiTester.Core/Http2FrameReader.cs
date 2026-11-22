using System.Text;

namespace ApiTester.Core;

/// <summary>The HTTP/2 frame types this reader names (RFC 9113 §6).</summary>
public enum Http2FrameType : byte
{
    Data = 0, Headers = 1, Priority = 2, RstStream = 3, Settings = 4,
    PushPromise = 5, Ping = 6, GoAway = 7, WindowUpdate = 8, Continuation = 9
}

/// <summary>One HTTP/2 frame as it appeared on the connection.</summary>
/// <param name="At">When the chunk carrying this frame's first byte crossed the wire. Frames are
/// attributed to the chunk they start in, which is what makes a stall visible: a WINDOW_UPDATE
/// arriving 30 seconds after the DATA that filled the window is the answer to "why did it hang".</param>
/// <param name="Detail">The per-type reading — settings values, a GOAWAY error code, a window
/// increment. Empty when the type carries nothing worth spelling out.</param>
public sealed record Http2Frame(
    TimeSpan At, WireDirection Direction, int Length, byte TypeCode, byte Flags, int StreamId, string Detail)
{
    /// <summary>The type's name, or <c>UNKNOWN(n)</c> — extensions and future types are legal on a
    /// live connection, and a reader that hid them would be lying about what arrived.</summary>
    public string TypeName => TypeCode switch
    {
        0 => "DATA",
        1 => "HEADERS",
        2 => "PRIORITY",
        3 => "RST_STREAM",
        4 => "SETTINGS",
        5 => "PUSH_PROMISE",
        6 => "PING",
        7 => "GOAWAY",
        8 => "WINDOW_UPDATE",
        9 => "CONTINUATION",
        _ => $"UNKNOWN({TypeCode})"
    };

    /// <summary>Flag names, which are type-dependent: 0x01 means END_STREAM on DATA and ACK on
    /// SETTINGS, so a single shared table would mislabel half of them.</summary>
    public string FlagNames
    {
        get
        {
            if (Flags == 0) return "";
            var set = new List<string>();
            switch ((Http2FrameType)TypeCode)
            {
                case Http2FrameType.Data:
                    if ((Flags & 0x01) != 0) set.Add("END_STREAM");
                    if ((Flags & 0x08) != 0) set.Add("PADDED");
                    break;
                case Http2FrameType.Headers:
                case Http2FrameType.PushPromise:
                    if ((Flags & 0x01) != 0 && TypeCode == 1) set.Add("END_STREAM");
                    if ((Flags & 0x04) != 0) set.Add("END_HEADERS");
                    if ((Flags & 0x08) != 0) set.Add("PADDED");
                    if ((Flags & 0x20) != 0 && TypeCode == 1) set.Add("PRIORITY");
                    break;
                case Http2FrameType.Continuation:
                    if ((Flags & 0x04) != 0) set.Add("END_HEADERS");
                    break;
                case Http2FrameType.Settings:
                case Http2FrameType.Ping:
                    if ((Flags & 0x01) != 0) set.Add("ACK");
                    break;
            }
            return set.Count > 0 ? string.Join("|", set) : $"0x{Flags:x2}";
        }
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Direction == WireDirection.Sent ? ">> " : "<< ")
          .Append($"[{At.TotalMilliseconds,8:F1} ms] ")
          .Append($"{TypeName,-13} ")
          .Append($"stream={StreamId,-3} len={Length,-6}");
        string flags = FlagNames;
        if (flags.Length > 0) sb.Append(' ').Append(flags);
        if (Detail.Length > 0) sb.Append("  ").Append(Detail);
        return sb.ToString();
    }
}

/// <summary>What one direction of a connection turned out to contain.</summary>
/// <param name="PrefaceSeen">Whether the client's connection preface started this stream. It also
/// answers whether a header block could ever be decoded in full: the HPACK dynamic table is built
/// up from the connection's first byte, so a connection joined late can never be replayed.</param>
/// <param name="TrailingBytes">Bytes left over that are not a whole frame — a capture cut mid-frame
/// rather than a protocol error. Reported instead of silently dropped.</param>
public sealed record Http2Read(bool PrefaceSeen, IReadOnlyList<Http2Frame> Frames, int TrailingBytes);

/// <summary>Decodes the HTTP/2 framing layer from plaintext bytes captured by <see cref="WireLog"/>.
///
/// <para>This is the layer people open a packet analyser for when debugging HTTP/2: a hung request
/// is usually explained by a flow-control window that never reopened, a GOAWAY carrying an error
/// code, or a SETTINGS value smaller than expected — none of which are visible in the request and
/// response bodies. Because the direct send path decrypts by construction, the bytes are simply
/// available; no capture driver and no administrator rights are involved.</para>
///
/// <para><b>Pure by design.</b> <see cref="Read"/> is a function from bytes to frames with no socket
/// and no state, so it is tested against hand-built frames rather than against a live server.</para></summary>
public static class Http2FrameReader
{
    /// <summary>The client connection preface (RFC 9113 §3.4). Its presence identifies an HTTP/2
    /// stream and, more usefully, tells us the capture began at the connection's first byte.</summary>
    public static ReadOnlySpan<byte> Preface => "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8;

    /// <summary>Whether these bytes begin an HTTP/2 connection.</summary>
    public static bool StartsWithPreface(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= Preface.Length && bytes[..Preface.Length].SequenceEqual(Preface);

    /// <summary>Parse one direction of a connection. <paramref name="offsets"/> maps a byte offset
    /// to the moment it crossed the wire, so each frame keeps a real timestamp.</summary>
    public static Http2Read Read(ReadOnlySpan<byte> bytes, WireDirection direction,
                                 IReadOnlyList<(int Offset, TimeSpan At)>? offsets = null,
                                 bool includeSecrets = false)
    {
        var frames = new List<Http2Frame>();
        int at = 0;
        bool preface = StartsWithPreface(bytes);
        if (preface) at = Preface.Length;

        int offsetIdx = 0;
        while (at + 9 <= bytes.Length)
        {
            int length = (bytes[at] << 16) | (bytes[at + 1] << 8) | bytes[at + 2];
            byte type = bytes[at + 3];
            byte flags = bytes[at + 4];
            // The top bit of the stream identifier is reserved and MUST be ignored on receipt.
            int stream = ((bytes[at + 5] & 0x7f) << 24) | (bytes[at + 6] << 16)
                       | (bytes[at + 7] << 8) | bytes[at + 8];

            if (at + 9 + length > bytes.Length) break;      // a frame cut short by the capture limit
            var payload = bytes.Slice(at + 9, length);

            frames.Add(new Http2Frame(TimeAt(offsets, at, ref offsetIdx), direction, length, type, flags, stream,
                                      Describe(type, flags, payload, includeSecrets)));
            at += 9 + length;
        }

        return new Http2Read(preface, frames, bytes.Length - at);
    }

    private static TimeSpan TimeAt(IReadOnlyList<(int Offset, TimeSpan At)>? offsets, int position, ref int idx)
    {
        if (offsets is null || offsets.Count == 0) return TimeSpan.Zero;
        while (idx + 1 < offsets.Count && offsets[idx + 1].Offset <= position) idx++;
        return offsets[idx].At;
    }

    private static string Describe(byte type, byte flags, ReadOnlySpan<byte> payload, bool includeSecrets)
    {
        switch ((Http2FrameType)type)
        {
            case Http2FrameType.Settings:
                if ((flags & 0x01) != 0) return "";                       // ACK carries no payload
                var settings = new List<string>();
                for (int i = 0; i + 6 <= payload.Length; i += 6)
                {
                    int id = (payload[i] << 8) | payload[i + 1];
                    uint value = ReadUInt32(payload[(i + 2)..]);
                    settings.Add($"{SettingName(id)}={value}");
                }
                return string.Join(" ", settings);

            case Http2FrameType.GoAway:
                if (payload.Length < 8) return "(truncated)";
                int lastStream = (int)(ReadUInt32(payload) & 0x7fffffff);
                uint error = ReadUInt32(payload[4..]);
                // The debug data is where a gateway says what it actually objected to, in words.
                string debug = payload.Length > 8 ? Printable(payload[8..]) : "";
                return $"lastStream={lastStream} error={ErrorName(error)}"
                     + (debug.Length > 0 ? $" debug=\"{debug}\"" : "");

            case Http2FrameType.RstStream:
                return payload.Length >= 4 ? $"error={ErrorName(ReadUInt32(payload))}" : "(truncated)";

            case Http2FrameType.WindowUpdate:
                return payload.Length >= 4
                    ? $"increment={ReadUInt32(payload) & 0x7fffffff}" : "(truncated)";

            case Http2FrameType.Ping:
                return (flags & 0x01) != 0 ? "" : "opaque data";

            case Http2FrameType.Headers:
            case Http2FrameType.Continuation:
            case Http2FrameType.PushPromise:
                return Hpack.Describe(payload, type, flags, includeSecrets);

            default:
                return "";
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> b) =>
        b.Length < 4 ? 0u : ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

    private static string SettingName(int id) => id switch
    {
        0x1 => "HEADER_TABLE_SIZE",
        0x2 => "ENABLE_PUSH",
        0x3 => "MAX_CONCURRENT_STREAMS",
        0x4 => "INITIAL_WINDOW_SIZE",
        0x5 => "MAX_FRAME_SIZE",
        0x6 => "MAX_HEADER_LIST_SIZE",
        0x8 => "ENABLE_CONNECT_PROTOCOL",
        _ => $"SETTING(0x{id:x})"
    };

    private static string ErrorName(uint code) => code switch
    {
        0 => "NO_ERROR", 1 => "PROTOCOL_ERROR", 2 => "INTERNAL_ERROR", 3 => "FLOW_CONTROL_ERROR",
        4 => "SETTINGS_TIMEOUT", 5 => "STREAM_CLOSED", 6 => "FRAME_SIZE_ERROR", 7 => "REFUSED_STREAM",
        8 => "CANCEL", 9 => "COMPRESSION_ERROR", 10 => "CONNECT_ERROR", 11 => "ENHANCE_YOUR_CALM",
        12 => "INADEQUATE_SECURITY", 13 => "HTTP_1_1_REQUIRED",
        _ => $"UNKNOWN({code})"
    };

    private static string Printable(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes) sb.Append(b is >= 0x20 and < 0x7f ? (char)b : '.');
        return sb.ToString();
    }
}

/// <summary>Reads the *structure* of an HPACK header block, and the field values that can be known
/// without the connection's history.
///
/// <para><b>What this deliberately does not do, and why.</b> HPACK compresses headers against a
/// dynamic table that both peers build up from the connection's first byte. A capture that joined
/// an established connection — which is the normal case here, since connections are pooled and
/// reused — cannot reconstruct that table, so any "decoded" value for a dynamic reference would be
/// a guess. Two things are knowable regardless, and only those are reported as values: references
/// into the fixed 61-entry static table, and literals sent uncompressed. A Huffman-coded literal is
/// named as such and its length given, rather than decoded, because decoding it correctly is
/// possible but reporting it would imply the neighbouring dynamic references are equally trustworthy
/// — and they are not.</para>
///
/// <para>The block is still walked in full: every representation's length is computable even when
/// its value is not readable, so the field count is exact and the reader never loses its place.</para></summary>
internal static class Hpack
{
    internal static string Describe(ReadOnlySpan<byte> payload, byte type, byte flags, bool includeSecrets)
    {
        var block = payload;

        // Strip the parts of the frame that precede the header block itself.
        if ((flags & 0x08) != 0 && block.Length > 0)         // PADDED: one length byte, then trailing pad
        {
            int pad = block[0];
            block = block[1..];
            if (pad <= block.Length) block = block[..^pad];
        }
        if (type == 1 && (flags & 0x20) != 0 && block.Length >= 5) block = block[5..];   // HEADERS PRIORITY

        // PUSH_PROMISE's promised stream identifier is mandatory and precedes the block. Naming it
        // is most of the point of the frame — "the server is about to push stream 4" — and a frame
        // too short to contain it is malformed, so it is reported as truncated rather than having
        // its identifier bytes parsed as though they were headers.
        string promised = "";
        if (type == 5)
        {
            if (block.Length < 4) return "(truncated)";
            promised = $"promised={((block[0] & 0x7f) << 24) | (block[1] << 16) | (block[2] << 8) | block[3]}  ";
            block = block[4..];
        }

        var fields = new List<string>();
        int count = 0, undecodable = 0, at = 0;

        while (at < block.Length)
        {
            byte first = block[at];
            string? name = null, value = null;

            if ((first & 0x80) != 0)                          // indexed header field
            {
                int index = ReadInteger(block, ref at, 7);
                if (index <= 0) break;                        // index 0 is illegal; stop rather than guess
                if (StaticTable.TryGetValue(index, out var entry)) { name = entry.Name; value = entry.Value; }
                else undecodable++;
            }
            else if ((first & 0x40) != 0)                     // literal, incremental indexing
            {
                int index = ReadInteger(block, ref at, 6);
                if (!ReadField(block, ref at, index, out name, out value)) break;
            }
            else if ((first & 0x20) != 0)                     // dynamic table size update
            {
                ReadInteger(block, ref at, 5);
                continue;                                     // not a field
            }
            else                                              // literal, without / never indexed
            {
                int index = ReadInteger(block, ref at, 4);
                if (!ReadField(block, ref at, index, out name, out value)) break;
            }

            count++;
            if (name is null) { undecodable++; continue; }
            if (!includeSecrets && IsSecret(name)) value = "(redacted)";
            fields.Add(value is null ? $"{name}: (opaque)" : $"{name}: {value}");
        }

        var sb = new StringBuilder(promised);
        sb.Append($"{count} field(s), {block.Length} bytes");
        if (fields.Count > 0) sb.Append("  ").Append(string.Join("; ", fields));
        if (undecodable > 0) sb.Append($"  (+{undecodable} needing the connection's HPACK history)");
        return sb.ToString();
    }

    /// <summary>Read one literal field's name and value. Returns false if the block is malformed or
    /// cut short, which stops the walk rather than emitting invented fields.</summary>
    private static bool ReadField(ReadOnlySpan<byte> block, ref int at, int nameIndex,
                                  out string? name, out string? value)
    {
        name = null; value = null;
        if (nameIndex > 0)
        {
            if (StaticTable.TryGetValue(nameIndex, out var entry)) name = entry.Name;
            // else: a dynamic-table name, unknowable here — left null, the field still counts.
        }
        else if (!ReadString(block, ref at, out name)) return false;

        return ReadString(block, ref at, out value);
    }

    /// <summary>Read a length-prefixed string, decoding it only when it was sent uncompressed. A
    /// Huffman-coded string advances the cursor correctly but yields no text — see the class note.</summary>
    private static bool ReadString(ReadOnlySpan<byte> block, ref int at, out string? text)
    {
        text = null;
        if (at >= block.Length) return false;
        bool huffman = (block[at] & 0x80) != 0;
        int length = ReadInteger(block, ref at, 7);
        if (length < 0 || at + length > block.Length) return false;

        if (huffman) text = $"(huffman, {length}B)";
        else text = Encoding.UTF8.GetString(block.Slice(at, length));
        at += length;
        return true;
    }

    /// <summary>HPACK's prefixed integer encoding (RFC 7541 §5.1): a value that fits in the prefix
    /// is the prefix; otherwise continuation octets carry seven bits each.</summary>
    private static int ReadInteger(ReadOnlySpan<byte> block, ref int at, int prefixBits)
    {
        if (at >= block.Length) return -1;
        int max = (1 << prefixBits) - 1;
        int value = block[at++] & max;
        if (value < max) return value;

        int shift = 0;
        while (at < block.Length)
        {
            byte b = block[at++];
            value += (b & 0x7f) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
            if (shift > 21) return -1;      // absurd for a header; refuse rather than overflow
        }
        return -1;
    }

    private static bool IsSecret(string name) =>
        name is "authorization" or "proxy-authorization" or "cookie" or "set-cookie";

    /// <summary>RFC 7541 Appendix A. Fixed for all connections, which is exactly why references
    /// into it are the part of a header block that can be read without any history.</summary>
    private static readonly Dictionary<int, (string Name, string? Value)> StaticTable = new()
    {
        [1] = (":authority", null),
        [2] = (":method", "GET"),
        [3] = (":method", "POST"),
        [4] = (":path", "/"),
        [5] = (":path", "/index.html"),
        [6] = (":scheme", "http"),
        [7] = (":scheme", "https"),
        [8] = (":status", "200"),
        [9] = (":status", "204"),
        [10] = (":status", "206"),
        [11] = (":status", "304"),
        [12] = (":status", "400"),
        [13] = (":status", "404"),
        [14] = (":status", "500"),
        [15] = ("accept-charset", null),
        [16] = ("accept-encoding", "gzip, deflate"),
        [17] = ("accept-language", null),
        [18] = ("accept-ranges", null),
        [19] = ("accept", null),
        [20] = ("access-control-allow-origin", null),
        [21] = ("age", null),
        [22] = ("allow", null),
        [23] = ("authorization", null),
        [24] = ("cache-control", null),
        [25] = ("content-disposition", null),
        [26] = ("content-encoding", null),
        [27] = ("content-language", null),
        [28] = ("content-length", null),
        [29] = ("content-location", null),
        [30] = ("content-range", null),
        [31] = ("content-type", null),
        [32] = ("cookie", null),
        [33] = ("date", null),
        [34] = ("etag", null),
        [35] = ("expect", null),
        [36] = ("expires", null),
        [37] = ("from", null),
        [38] = ("host", null),
        [39] = ("if-match", null),
        [40] = ("if-modified-since", null),
        [41] = ("if-none-match", null),
        [42] = ("if-range", null),
        [43] = ("if-unmodified-since", null),
        [44] = ("last-modified", null),
        [45] = ("link", null),
        [46] = ("location", null),
        [47] = ("max-forwards", null),
        [48] = ("proxy-authenticate", null),
        [49] = ("proxy-authorization", null),
        [50] = ("range", null),
        [51] = ("referer", null),
        [52] = ("refresh", null),
        [53] = ("retry-after", null),
        [54] = ("server", null),
        [55] = ("set-cookie", null),
        [56] = ("strict-transport-security", null),
        [57] = ("transfer-encoding", null),
        [58] = ("user-agent", null),
        [59] = ("vary", null),
        [60] = ("via", null),
        [61] = ("www-authenticate", null),
    };
}

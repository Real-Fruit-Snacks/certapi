using System.Text;

namespace ApiTester.Core;

/// <summary>Which side of the conversation a chunk of bytes belongs to.</summary>
public enum WireDirection { Sent, Received }

/// <summary>One chunk of plaintext as it crossed the wire, with when it happened.</summary>
public sealed record WireChunk(TimeSpan At, WireDirection Direction, byte[] Bytes);

/// <summary>Collects the plaintext bytes of a connection — what was really sent and really
/// received, after TLS, before any parsing.
/// <para>This is the one thing a packet capture cannot give you for an encrypted connection
/// without its keys, and it costs no driver and no administrator rights: this product's direct
/// send path hand-drives its own <see cref="System.Net.Security.SslStream"/>, so a tee inserted
/// above it sees exactly what the HTTP layer wrote and read.</para>
/// <para>Thread-safe: a connection's reads and writes can overlap.</para></summary>
public sealed class WireLog
{
    private readonly object _gate = new();
    private readonly List<WireChunk> _chunks = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly long _limit;
    private long _captured;

    /// <param name="limitBytes">Stop collecting after this much, so a large download does not
    /// become a memory problem. The report says plainly when it stopped rather than pretending
    /// the conversation ended.</param>
    public WireLog(long limitBytes = 1024 * 1024) => _limit = limitBytes;

    public bool Truncated { get; private set; }

    public IReadOnlyList<WireChunk> Chunks { get { lock (_gate) return _chunks.ToArray(); } }

    public void Record(WireDirection direction, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return;
        lock (_gate)
        {
            if (_captured >= _limit) { Truncated = true; return; }
            int take = (int)Math.Min(bytes.Length, _limit - _captured);
            if (take < bytes.Length) Truncated = true;
            _chunks.Add(new WireChunk(_clock.Elapsed, direction, bytes[..take].ToArray()));
            _captured += take;
        }
    }

    /// <summary>The conversation as text, one direction-marked block per chunk. A block that is
    /// not valid text is rendered as hex and ASCII side by side, the way any byte viewer does —
    /// a body is often a mixture, and guessing wrong in either direction loses information.</summary>
    public string Render(bool includeSecrets = false)
    {
        var sb = new StringBuilder();
        var chunks = includeSecrets ? Chunks : MaskSecretHeaderValues(Chunks);
        foreach (var chunk in chunks)
        {
            string arrow = chunk.Direction == WireDirection.Sent ? ">>" : "<<";
            sb.Append(arrow).Append(' ')
              .Append(chunk.Direction == WireDirection.Sent ? "sent" : "received").Append(' ')
              .Append(chunk.Bytes.Length).Append(" bytes at ")
              .Append(chunk.At.TotalMilliseconds.ToString("F1")).AppendLine(" ms");

            if (LooksTextual(chunk.Bytes))
            {
                string text = Encoding.UTF8.GetString(chunk.Bytes);
                if (!includeSecrets) text = RedactHeaders(text);
                foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                    sb.Append("   ").AppendLine(line);
            }
            else
            {
                sb.Append(Hex(chunk.Bytes));
            }
            sb.AppendLine();
        }
        if (Truncated) sb.AppendLine("… truncated: the capture limit was reached (--wire-file keeps more).");
        return sb.ToString();
    }

    /// <summary>The same conversation read as HTTP/2 frames rather than as bytes — the layer an
    /// analyser shows, and the one that explains a stalled or refused h2 request: a flow-control
    /// window that never reopened, a GOAWAY carrying an error code, a SETTINGS value smaller than
    /// expected.
    /// <para>Each direction is a separate stream of frames (a frame can span several chunks, and
    /// the two directions interleave arbitrarily), so both are parsed independently and then merged
    /// back into one timeline by when their first byte crossed the wire.</para></summary>
    public string RenderFrames(bool includeSecrets = false)
    {
        var sent = Concatenate(WireDirection.Sent);
        var received = Concatenate(WireDirection.Received);

        var read = new[]
        {
            Http2FrameReader.Read(sent.Bytes, WireDirection.Sent, sent.Offsets, includeSecrets),
            Http2FrameReader.Read(received.Bytes, WireDirection.Received, received.Offsets, includeSecrets),
        };

        var frames = read.SelectMany(r => r.Frames).OrderBy(f => f.At).ToList();
        if (frames.Count == 0)
        {
            // Saying "no frames" would read as a failure; the usual cause is simply that this was
            // an HTTP/1.1 connection, where the byte view is the readable one.
            return "No HTTP/2 frames in this exchange — it is not an HTTP/2 connection. "
                 + "Use the byte view (--wire) instead.\n";
        }

        var sb = new StringBuilder();
        foreach (var frame in frames) sb.AppendLine(frame.ToString());

        if (!read[0].PrefaceSeen)
            sb.AppendLine()
              .AppendLine("Note: this capture began after the connection was established, so the "
                        + "HPACK dynamic table cannot be replayed and compressed header values are "
                        + "reported rather than decoded.");
        int trailing = read.Sum(r => r.TrailingBytes);
        if (trailing > 0) sb.AppendLine($"({trailing} byte(s) left over — a frame the capture cut short.)");
        if (Truncated) sb.AppendLine("… truncated: the capture limit was reached.");
        return sb.ToString();
    }

    /// <summary>One direction's bytes end to end, with a map from byte offset to the moment that
    /// byte crossed the wire, so each frame can keep a real timestamp.</summary>
    private (byte[] Bytes, List<(int Offset, TimeSpan At)> Offsets) Concatenate(WireDirection direction)
    {
        var buffer = new List<byte>();
        var offsets = new List<(int, TimeSpan)>();
        foreach (var chunk in Chunks)
        {
            if (chunk.Direction != direction) continue;
            offsets.Add((buffer.Count, chunk.At));
            buffer.AddRange(chunk.Bytes);
        }
        return (buffer.ToArray(), offsets);
    }

    /// <summary>Copies of the chunks with every credential header's value overwritten by
    /// <c>*</c>, found by reading each direction end to end: a header line split across two reads
    /// (a big cookie, a long token) is masked in both halves, and a chunk that is shown as hex
    /// — headers and a binary body arriving together — shows no secret in its ASCII column.
    /// Length-preserving, so every chunk keeps its size and place.</summary>
    internal static IReadOnlyList<WireChunk> MaskSecretHeaderValues(IReadOnlyList<WireChunk> chunks)
    {
        var copies = chunks.Select(c => c with { Bytes = (byte[])c.Bytes.Clone() }).ToList();
        foreach (var direction in new[] { WireDirection.Sent, WireDirection.Received })
        {
            var mine = copies.Where(c => c.Direction == direction).ToList();
            int total = mine.Sum(c => c.Bytes.Length);
            if (total == 0) continue;
            var all = new byte[total];
            var starts = new int[mine.Count];
            for (int i = 0, at = 0; i < mine.Count; at += mine[i].Bytes.Length, i++)
            {
                starts[i] = at;
                mine[i].Bytes.CopyTo(all, at);
            }

            for (int lineStart = 0; lineStart < total;)
            {
                int lineEnd = Array.IndexOf(all, (byte)'\n', lineStart);
                if (lineEnd < 0) lineEnd = total;
                int colon = -1;
                for (int i = lineStart; i < lineEnd && i - lineStart < 128; i++)
                {
                    byte b = all[i];
                    if (b == ':') { colon = i; break; }
                    if (!(b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_')) break;
                }
                if (colon > lineStart && SecretHeaders.IsSecret(Encoding.ASCII.GetString(all, lineStart, colon - lineStart)))
                {
                    int v = colon + 1;
                    while (v < lineEnd && all[v] is (byte)' ' or (byte)'\t') v++;
                    int end = lineEnd > v && all[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
                    for (int i = v; i < end; i++) all[i] = (byte)'*';
                }
                lineStart = lineEnd + 1;
            }

            for (int i = 0; i < mine.Count; i++)
                Array.Copy(all, starts[i], mine[i].Bytes, 0, mine[i].Bytes.Length);
        }
        return copies;
    }

    /// <summary>Whether a chunk is worth showing as text: mostly printable, no NUL bytes. A
    /// heuristic, deliberately — HTTP/1.1 headers are text, an HTTP/2 frame is not, and a body may
    /// be either.</summary>
    internal static bool LooksTextual(ReadOnlySpan<byte> bytes)
    {
        int printable = 0;
        foreach (byte b in bytes)
        {
            if (b == 0) return false;
            if (b is >= 0x20 and < 0x7f or (byte)'\r' or (byte)'\n' or (byte)'\t') printable++;
        }
        return printable >= bytes.Length * 0.85;
    }

    /// <summary>Hex and ASCII side by side, sixteen bytes to a line.</summary>
    internal static string Hex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder();
        for (int offset = 0; offset < bytes.Length; offset += 16)
        {
            int count = Math.Min(16, bytes.Length - offset);
            sb.Append("   ").Append(offset.ToString("x8")).Append("  ");
            for (int i = 0; i < 16; i++)
            {
                sb.Append(i < count ? bytes[offset + i].ToString("x2") : "  ").Append(' ');
                if (i == 7) sb.Append(' ');
            }
            sb.Append(" |");
            for (int i = 0; i < count; i++)
            {
                byte b = bytes[offset + i];
                sb.Append(b is >= 0x20 and < 0x7f ? (char)b : '.');
            }
            sb.AppendLine("|");
        }
        return sb.ToString();
    }

    /// <summary>Blank the value of a credential-bearing header while keeping the header's name, so
    /// the transcript still shows that it was sent. A wire log is pasted into tickets; this
    /// feature must not be how a token escapes.</summary>
    internal static string RedactHeaders(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var line in text.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && IsSecretHeader(line[..colon].Trim()))
            {
                sb.Append(line[..(colon + 1)]).Append(" (redacted)");
                if (line.EndsWith('\r')) sb.Append('\r');
            }
            else sb.Append(line);
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static bool IsSecretHeader(string name) => SecretHeaders.IsSecret(name);
}

/// <summary>An <see cref="System.Net.Security.SslStream"/> that also copies the plaintext crossing
/// it into a <see cref="WireLog"/> — what the HTTP layer wrote, and what it read back, after
/// encryption and decryption.
/// <para><b>Why a subclass rather than a wrapper.</b> The obvious design — a pass-through stream
/// wrapped around the TLS stream — silently breaks the request. <c>SocketsHttpHandler</c> skips
/// its own TLS handshake only when the stream its <c>ConnectCallback</c> returns actually IS an
/// <see cref="System.Net.Security.SslStream"/>; hand it anything else and it negotiates a second
/// TLS session inside the first, which the server cannot parse. Subclassing keeps that type
/// identity, so the handler still recognises the connection as already secured while every byte
/// still passes through here.</para></summary>
public sealed class TappedSslStream : System.Net.Security.SslStream
{
    private readonly WireLog _log;

    public TappedSslStream(Stream innerStream, bool leaveInnerStreamOpen,
                           System.Net.Security.RemoteCertificateValidationCallback? validationCallback,
                           WireLog log)
        : base(innerStream, leaveInnerStreamOpen, validationCallback) => _log = log;

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = base.Read(buffer, offset, count);
        if (read > 0) _log.Record(WireDirection.Received, buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int read = await base.ReadAsync(buffer, ct);
        if (read > 0) _log.Record(WireDirection.Received, buffer.Span[..read]);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int read = await base.ReadAsync(buffer, offset, count, ct);
        if (read > 0) _log.Record(WireDirection.Received, buffer.AsSpan(offset, read));
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _log.Record(WireDirection.Sent, buffer.AsSpan(offset, count));
        base.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        _log.Record(WireDirection.Sent, buffer.Span);
        return base.WriteAsync(buffer, ct);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        _log.Record(WireDirection.Sent, buffer.AsSpan(offset, count));
        return base.WriteAsync(buffer, offset, count, ct);
    }
}

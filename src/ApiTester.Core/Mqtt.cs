using System.Buffers.Binary;
using System.Text;

namespace ApiTester.Core;

/// <summary>A received MQTT control packet: the type (high nibble of the first byte), its flags
/// (low nibble) and the rest of the packet.</summary>
public sealed record MqttPacket(int Type, int Flags, byte[] Body);

/// <summary>An application message delivered by the broker.</summary>
public sealed record MqttMessage(string Topic, byte[] Payload, int Qos, bool Retain);

/// <summary>MQTT 3.1.1 and 5.0 packet encoding and decoding: just what a test client needs —
/// CONNECT, PUBLISH (QoS 0 and 1), SUBSCRIBE, PINGREQ, DISCONNECT and their answers.</summary>
public static class MqttCodec
{
    public const int Connect = 1, ConnAck = 2, Publish = 3, PubAck = 4, PubRec = 5, PubRel = 6, PubComp = 7,
        Subscribe = 8, SubAck = 9, PingReq = 12, PingResp = 13, Disconnect = 14;

    public static byte[] EncodeConnect(int version, string clientId, string? username, string? password,
        int keepAliveSeconds, bool cleanStart)
    {
        var v = new List<byte>();
        WriteString(v, "MQTT");
        v.Add((byte)(version == 5 ? 5 : 4));
        byte flags = 0;
        if (cleanStart) flags |= 0x02;
        if (username is not null) flags |= 0x80;
        if (password is not null) flags |= 0x40;
        v.Add(flags);
        v.Add((byte)(keepAliveSeconds >> 8));
        v.Add((byte)keepAliveSeconds);
        if (version == 5) v.Add(0);   // no properties
        WriteString(v, clientId);
        if (username is not null) WriteString(v, username);
        if (password is not null) WriteBinary(v, Encoding.UTF8.GetBytes(password));
        return Frame(Connect << 4, v);
    }

    public static byte[] EncodePublish(int version, string topic, byte[] payload, int qos, bool retain, ushort packetId)
    {
        var v = new List<byte>();
        WriteString(v, topic);
        if (qos > 0) { v.Add((byte)(packetId >> 8)); v.Add((byte)packetId); }
        if (version == 5) v.Add(0);
        v.AddRange(payload);
        return Frame((Publish << 4) | (qos << 1) | (retain ? 1 : 0), v);
    }

    public static byte[] EncodeSubscribe(int version, ushort packetId, IEnumerable<(string Filter, int Qos)> filters)
    {
        var v = new List<byte> { (byte)(packetId >> 8), (byte)packetId };
        if (version == 5) v.Add(0);
        foreach (var (filter, qos) in filters)
        {
            WriteString(v, filter);
            v.Add((byte)qos);
        }
        return Frame((Subscribe << 4) | 0x02, v);
    }

    public static byte[] EncodePubAck(ushort packetId) => new byte[] { PubAck << 4, 2, (byte)(packetId >> 8), (byte)packetId };

    public static byte[] PingRequest => new byte[] { PingReq << 4, 0 };

    public static byte[] DisconnectPacket => new byte[] { Disconnect << 4, 0 };

    /// <summary>Read one whole packet, or null at the end of the stream.</summary>
    public static async Task<MqttPacket?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var one = new byte[1];
        if (await stream.ReadAsync(one, ct) == 0) return null;
        int first = one[0];
        int length = 0, shift = 0;
        while (true)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new EndOfStreamException("the broker closed mid-packet");
            length |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0) break;
            shift += 7;
            if (shift > 21) throw new InvalidDataException("malformed MQTT remaining length");
        }
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return new MqttPacket(first >> 4, first & 0x0F, body);
    }

    /// <summary>The CONNACK return/reason code (0 = accepted).</summary>
    public static int ConnAckCode(MqttPacket p) => p.Body.Length >= 2 ? p.Body[1] : 0xFF;

    public static string ConnAckReason(int version, int code) => (version, code) switch
    {
        (_, 0) => "accepted",
        (4, 1) => "unacceptable protocol version",
        (4, 2) => "client identifier rejected",
        (4, 3) => "server unavailable",
        (4, 4) => "bad user name or password",
        (4, 5) => "not authorized",
        (5, 0x84) => "unsupported protocol version",
        (5, 0x85) => "client identifier not valid",
        (5, 0x86) => "bad user name or password",
        (5, 0x87) => "not authorized",
        (5, 0x88) => "server unavailable",
        (5, 0x8A) => "banned",
        (5, 0x8C) => "bad authentication method",
        _ => $"refused (code 0x{code:X2})"
    };

    public static MqttMessage DecodePublish(int version, MqttPacket p, out ushort packetId)
    {
        int qos = (p.Flags >> 1) & 3;
        int at = 0;
        string topic = ReadString(p.Body, ref at);
        packetId = 0;
        if (qos > 0) { packetId = BinaryPrimitives.ReadUInt16BigEndian(p.Body.AsSpan(at)); at += 2; }
        if (version == 5) { int properties = ReadVarInt(p.Body, ref at); at += properties; }
        return new MqttMessage(topic, p.Body[at..], qos, (p.Flags & 1) != 0);
    }

    /// <summary>The per-filter results of a SUBACK: granted QoS 0–2, or a failure code ≥ 0x80.</summary>
    public static IReadOnlyList<int> SubAckCodes(int version, MqttPacket p)
    {
        int at = 2;
        if (version == 5) { int properties = ReadVarInt(p.Body, ref at); at += properties; }
        return p.Body[at..].Select(b => (int)b).ToList();
    }

    public static ushort PacketIdOf(MqttPacket p) =>
        p.Body.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(p.Body) : (ushort)0;

    private static byte[] Frame(int first, List<byte> body)
    {
        var o = new List<byte>(body.Count + 5) { (byte)first };
        int n = body.Count;
        do
        {
            byte b = (byte)(n % 128);
            n /= 128;
            if (n > 0) b |= 0x80;
            o.Add(b);
        } while (n > 0);
        o.AddRange(body);
        return o.ToArray();
    }

    private static void WriteString(List<byte> o, string s) => WriteBinary(o, Encoding.UTF8.GetBytes(s));

    private static void WriteBinary(List<byte> o, byte[] b)
    {
        if (b.Length > 0xFFFF) throw new ArgumentException("an MQTT string is limited to 65535 bytes");
        o.Add((byte)(b.Length >> 8));
        o.Add((byte)b.Length);
        o.AddRange(b);
    }

    private static string ReadString(byte[] b, ref int at)
    {
        int len = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));
        at += 2;
        string s = Encoding.UTF8.GetString(b, at, len);
        at += len;
        return s;
    }

    /// <summary>Read a variable byte integer at <paramref name="at"/>, advancing past it, and return its value.</summary>
    private static int ReadVarInt(byte[] b, ref int at)
    {
        int value = 0, shift = 0;
        while (true)
        {
            byte x = b[at++];
            value |= (x & 0x7F) << shift;
            if ((x & 0x80) == 0) return value;
            shift += 7;
        }
    }
}

/// <summary>A <see cref="Stream"/> over a WebSocket carrying MQTT: each binary message is read
/// through as bytes (a message may hold part of a packet or several), each write is one message.</summary>
public sealed class WebSocketByteStream : Stream
{
    private readonly WebSocketSession _session;
    private readonly IAsyncEnumerator<WebSocketMessage> _messages;
    private byte[] _buffer = Array.Empty<byte>();
    private int _at;

    public WebSocketByteStream(WebSocketSession session, CancellationToken ct)
    {
        _session = session;
        _messages = session.ReceiveAllAsync(ct).GetAsyncEnumerator(ct);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
    {
        while (_at >= _buffer.Length)
        {
            if (!await _messages.MoveNextAsync() || _messages.Current.IsClose) return 0;
            _buffer = _messages.Current.Bytes;
            _at = 0;
        }
        int n = Math.Min(destination.Length, _buffer.Length - _at);
        _buffer.AsMemory(_at, n).CopyTo(destination);
        _at += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken ct = default) =>
        await _session.SendBinaryAsync(source, ct);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override async ValueTask DisposeAsync()
    {
        await _messages.DisposeAsync();
        await base.DisposeAsync();
    }
}

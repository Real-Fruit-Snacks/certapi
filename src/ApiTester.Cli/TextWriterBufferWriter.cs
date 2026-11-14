using System.Buffers;
using System.Text;

namespace ApiTester.Cli;

/// <summary>A UTF-8 <see cref="IBufferWriter{T}"/> that passes what is written on to a
/// <see cref="TextWriter"/> as it arrives, a chunk at a time: a formatter can write a large
/// document to the console's text stream without the whole of it ever being held, as bytes or as
/// a string. Characters split across two chunks are carried over by the decoder.</summary>
internal sealed class TextWriterBufferWriter : IBufferWriter<byte>, IDisposable
{
    private const int ChunkSize = 64 * 1024;
    private readonly TextWriter _output;
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
    private char[] _chars = ArrayPool<char>.Shared.Rent(ChunkSize + 4);

    public TextWriterBufferWriter(TextWriter output) => _output = output;

    public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return _buffer; }

    public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return _buffer; }

    public void Advance(int count)
    {
        if (count <= 0) return;
        int written = _decoder.GetChars(_buffer, 0, count, _chars, 0, flush: false);
        _output.Write(_chars, 0, written);
    }

    private void Ensure(int sizeHint)
    {
        if (sizeHint <= _buffer.Length) return;
        ArrayPool<byte>.Shared.Return(_buffer);
        ArrayPool<char>.Shared.Return(_chars);
        _buffer = ArrayPool<byte>.Shared.Rent(sizeHint);
        _chars = ArrayPool<char>.Shared.Rent(sizeHint + 4);
    }

    public void Dispose()
    {
        int tail = _decoder.GetChars(Array.Empty<byte>(), 0, 0, _chars, 0, flush: true);
        if (tail > 0) _output.Write(_chars, 0, tail);
        ArrayPool<byte>.Shared.Return(_buffer);
        ArrayPool<char>.Shared.Return(_chars);
    }
}

namespace ApiTester.Core;

/// <summary>A response body as JSON reads it: without the UTF-8 byte-order mark some servers (IIS,
/// a static .json file saved by Notepad) put in front, which <see cref="System.Text.Json.JsonDocument"/>
/// refuses outright — so assertions, captures, diffs and the pretty view all saw "not JSON".</summary>
internal static class Utf8Json
{
    public static ReadOnlyMemory<byte> Body(byte[] body) =>
        body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF ? body.AsMemory(3) : body;
}

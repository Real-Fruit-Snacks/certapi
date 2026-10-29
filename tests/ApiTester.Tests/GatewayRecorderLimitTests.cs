using System.IO;
using System.Text;
using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>`serve --record` holds the session in memory until shutdown, so what it keeps of the
/// bodies is bounded; an exchange past the limits is still recorded, with a note instead of its body.</summary>
public class GatewayRecorderLimitTests
{
    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoHeaders = Array.Empty<KeyValuePair<string, string>>();
    private static readonly IReadOnlyList<KeyValuePair<string, string>> TextHeaders =
        new[] { new KeyValuePair<string, string>("Content-Type", "text/plain") };

    private static JsonElement SavedEntries(GatewayRecorder recorder)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".har");
        try
        {
            recorder.Save(path);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.GetProperty("log").GetProperty("entries").Clone();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Bodies_past_the_budget_are_left_out_and_said_so()
    {
        // The budget is memory: a kept text body is a string, two bytes a character, so 8 bytes cost 16.
        var recorder = new GatewayRecorder(bodyBudget: 20);
        recorder.Record("GET", "https://api.example/a", NoHeaders, Array.Empty<byte>(), null, 200, "OK", TextHeaders, Encoding.UTF8.GetBytes("12345678"), 1);
        recorder.Record("GET", "https://api.example/b", NoHeaders, Array.Empty<byte>(), null, 200, "OK", TextHeaders, Encoding.UTF8.GetBytes("abcdefgh"), 1);

        var entries = SavedEntries(recorder);
        Assert.Equal(2, entries.GetArrayLength());
        var first = entries[0].GetProperty("response").GetProperty("content");
        var second = entries[1].GetProperty("response").GetProperty("content");
        Assert.Equal("12345678", first.GetProperty("text").GetString());
        Assert.False(first.TryGetProperty("comment", out _));
        Assert.Equal("", second.GetProperty("text").GetString());
        Assert.Equal(8, second.GetProperty("size").GetInt64());
        Assert.Contains("limit for bodies", second.GetProperty("comment").GetString());
        Assert.Equal(1, recorder.OmittedBodies);

        // What is left of the budget still takes a body that fits; once it is spent, capture stops.
        Assert.True(recorder.KeepsBodies);
        recorder.Record("GET", "https://api.example/c", NoHeaders, Array.Empty<byte>(), null, 200, "OK", TextHeaders, Encoding.UTF8.GetBytes("xy"), 1);
        Assert.Equal("xy", SavedEntries(recorder)[2].GetProperty("response").GetProperty("content").GetProperty("text").GetString());
        Assert.False(recorder.KeepsBodies);
        Assert.Equal(0, recorder.CaptureLimit);
    }

    [Fact]
    public void A_body_larger_than_the_capture_limit_is_recorded_by_size()
    {
        var recorder = new GatewayRecorder();
        long size = GatewayRecorder.MaxBodyBytes + 1L;
        recorder.Record("POST", "https://api.example/upload", NoHeaders, Array.Empty<byte>(), "application/octet-stream", 201, "Created",
            TextHeaders, Array.Empty<byte>(), 1, requestBodySize: size, responseBodySize: null);

        var entry = SavedEntries(recorder)[0];
        var post = entry.GetProperty("request").GetProperty("postData");
        Assert.Contains("per body", post.GetProperty("comment").GetString());
        Assert.Equal(1, recorder.OmittedBodies);
        Assert.True(recorder.KeepsBodies);   // nothing was kept, so the budget is untouched
    }

    [Fact]
    public void A_left_out_compressed_body_replays_without_its_content_encoding()
    {
        var recorder = new GatewayRecorder();
        var gzip = new[] { new KeyValuePair<string, string>("Content-Type", "application/json"),
                           new KeyValuePair<string, string>("Content-Encoding", "gzip") };
        recorder.Record("GET", "https://api.example/export", NoHeaders, Array.Empty<byte>(), null, 200, "OK",
            gzip, Array.Empty<byte>(), 1, responseBodySize: GatewayRecorder.MaxBodyBytes + 1L);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".har");
        try
        {
            recorder.Save(path);
            var served = new HarReplaySource(HarReader.Parse(File.ReadAllText(path))).Match("GET", "/export");
            Assert.NotNull(served);
            Assert.Empty(served!.Body);
            Assert.DoesNotContain(served.Headers, h => h.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(served.Headers, h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Captures_in_flight_share_the_budget()
    {
        var recorder = new GatewayRecorder(bodyBudget: 100);
        var first = recorder.Capture(new MemoryStream());
        first.Write(new byte[40], 0, 40);          // reserves 80
        using var second = recorder.Capture(new MemoryStream());
        second.Write(new byte[20], 0, 20);         // 80 + 40 would pass 100
        Assert.False(first.Overflowed);
        Assert.True(second.Overflowed);
        Assert.Equal(20, second.Total);

        first.Dispose();                            // a capture that ends hands its share back
        using var third = recorder.Capture(new MemoryStream());
        third.Write(new byte[20], 0, 20);
        Assert.False(third.Overflowed);
        Assert.Equal(20, third.TakeCaptured().Length);
        Assert.True(recorder.KeepsBodies);
    }

    [Fact]
    public async Task A_capturing_stream_relays_everything_and_keeps_only_what_fits()
    {
        var data = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();

        // Write-through (a response going to the client).
        var client = new MemoryStream();
        using (var tee = new CapturingStream(client, limit: 400))
        {
            await tee.WriteAsync(data.AsMemory(0, 300));
            Assert.False(tee.Overflowed);
            Assert.Equal(data[..300], tee.Captured);
            await tee.WriteAsync(data.AsMemory(300));
            Assert.True(tee.Overflowed);
            Assert.Empty(tee.Captured);
            Assert.Equal(1000, tee.Total);
        }
        Assert.Equal(data, client.ToArray());   // every byte reached the client

        // Read-through (a request body going upstream).
        using var source = new CapturingStream(new MemoryStream(data), limit: 2000);
        var upstream = new MemoryStream();
        await source.CopyToAsync(upstream);
        Assert.Equal(data, upstream.ToArray());
        Assert.Equal(data, source.Captured);
        Assert.False(source.Overflowed);
    }

    [Fact]
    public void A_capture_limit_of_zero_keeps_nothing_but_counts_everything()
    {
        var sink = new MemoryStream();
        using var tee = new CapturingStream(sink, limit: 0);
        tee.Write(new byte[] { 1, 2, 3 }, 0, 3);
        Assert.True(tee.Overflowed);
        Assert.Equal(3, tee.Total);
        Assert.Equal(3, sink.Length);
    }
}

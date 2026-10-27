using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>A body file that is not text goes out as its exact bytes.</summary>
public class BinaryBodyTests
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0xFE, 0x80, 0x01 };

    /// <summary>A one-request server that keeps what it received, byte for byte.</summary>
    private sealed class Capture : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serve;
        public byte[] Body = Array.Empty<byte>();
        public string? ContentType;
        public string Url { get; }

        public Capture()
        {
            int port = ServeFixture.FreePort();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _serve = Task.Run(async () =>
            {
                var ctx = await _listener.GetContextAsync();
                using var ms = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(ms);
                Body = ms.ToArray();
                ContentType = ctx.Request.ContentType;
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
            });
        }

        public void Wait() => _serve.Wait(TimeSpan.FromSeconds(20));
        public void Dispose() { try { _listener.Stop(); } catch { } }
    }

    private static (int Code, string Err) Send(params string[] args)
    {
        var se = new StringWriter();
        int code = CliApp.Run(new[] { "send" }.Concat(args).Append("--no-config").ToArray(), new StringWriter(), se, new MemoryStream(),
            new CliServices { UserConfigPath = null, LiveStatePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json") });
        return (code, se.ToString());
    }

    private static string TempFile(byte[] bytes)
    {
        var f = Path.GetTempFileName();
        File.WriteAllBytes(f, bytes);
        return f;
    }

    [Theory]
    [InlineData("--data-file")]
    [InlineData("-d")]
    public void A_binary_file_arrives_byte_for_byte_as_octet_stream(string option)
    {
        var file = TempFile(Png);
        try
        {
            using var server = new Capture();
            var r = Send(server.Url, "-X", "PUT", option, option == "-d" ? "@" + file : file);
            server.Wait();
            Assert.Equal(0, r.Code);
            Assert.Equal(Png, server.Body);
            Assert.Equal("application/octet-stream", server.ContentType);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_typed_content_type_is_kept_for_a_binary_body()
    {
        var file = TempFile(Png);
        try
        {
            using var server = new Capture();
            Assert.Equal(0, Send(server.Url, "-X", "PUT", "--data-file", file, "--content-type", "image/png").Code);
            server.Wait();
            Assert.Equal("image/png", server.ContentType);
            Assert.Equal(Png, server.Body);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_text_file_still_resolves_variables_and_defaults_to_json()
    {
        var file = TempFile(Encoding.UTF8.GetBytes("{\"who\":\"{{name}}\"}"));
        try
        {
            using var server = new Capture();
            Assert.Equal(0, Send(server.Url, "-X", "POST", "--data-file", file, "--var", "name=matt").Code);
            server.Wait();
            Assert.Equal("{\"who\":\"matt\"}", Encoding.UTF8.GetString(server.Body));
            Assert.StartsWith("application/json", server.ContentType);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_binary_body_is_archived_as_base64()
    {
        var file = TempFile(Png);
        var har = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".har");
        try
        {
            using var server = new Capture();
            Assert.Equal(0, Send(server.Url, "-X", "PUT", "--data-file", file, "--har", har).Code);
            server.Wait();
            using var doc = JsonDocument.Parse(File.ReadAllText(har));
            var post = doc.RootElement.GetProperty("log").GetProperty("entries")[0].GetProperty("request").GetProperty("postData");
            Assert.Equal("base64", post.GetProperty("encoding").GetString());
            Assert.Equal(Png, Convert.FromBase64String(post.GetProperty("text").GetString()!));
        }
        finally { File.Delete(file); File.Delete(har); }
    }

    [Fact]
    public void Body_files_are_text_only_when_they_are_utf8_without_nul()
    {
        Assert.Equal("héllo", BodyFile.FromBytes(Encoding.UTF8.GetBytes("héllo")).Text);
        Assert.Equal("x", BodyFile.FromBytes(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'x' }).Text);   // BOM dropped, as before
        Assert.True(BodyFile.FromBytes(new byte[] { (byte)'a', 0, (byte)'b' }).IsBinary);
        Assert.True(BodyFile.FromBytes(new byte[] { 0xC3, 0x28 }).IsBinary);
        Assert.Equal("plain body", BodyFile.FromDataOption("plain body")!.Text);
        Assert.Null(BodyFile.FromDataOption(null));
    }

    [Fact]
    public void A_utf16_file_with_a_bom_is_text_as_windows_powershell_writes_it()
    {
        // Windows PowerShell 5.1: '{"a":1}' > body.json writes UTF-16LE with a BOM.
        var le = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("{\"a\":1}")).ToArray();
        Assert.Equal("{\"a\":1}", BodyFile.FromBytes(le).Text);
        var be = new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes("x")).ToArray();
        Assert.Equal("x", BodyFile.FromBytes(be).Text);
    }

    [Fact]
    public void A_content_type_typed_with_dash_H_sets_the_body_type()
    {
        var file = TempFile(Png);
        try
        {
            using var server = new Capture();
            Assert.Equal(0, Send(server.Url, "-X", "PUT", "--data-file", file, "-H", "Content-Type: image/png").Code);
            server.Wait();
            Assert.Equal("image/png", server.ContentType);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Recorded_and_imported_bodies_keep_text_and_never_mangle_binary()
    {
        var text = HarPostData.From(Encoding.UTF8.GetBytes("{\"a\":1}"), "application/json");
        Assert.Null(text.Encoding);
        Assert.Equal("{\"a\":1}", text.TextOrNull());

        var bin = HarPostData.From(Png, "image/png");
        Assert.Equal("base64", bin.Encoding);
        Assert.Equal(Png, Convert.FromBase64String(bin.Text));
        Assert.Null(bin.TextOrNull());   // no faithful text form for a saved request
    }

    [Fact]
    public void A_grpc_message_file_must_be_text()
    {
        var file = TempFile(Png);
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(new[] { "grpc", "call", "http://127.0.0.1:9", "Svc/Method", "-d", "@" + file, "--no-config" },
                TextReader.Null, new StringWriter(), se,
                services: new CliServices { UserConfigPath = null, LiveStatePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json") });
            Assert.Equal(3, code);
            Assert.Contains("not UTF-8 text", se.ToString());
        }
        finally { File.Delete(file); }
    }
}

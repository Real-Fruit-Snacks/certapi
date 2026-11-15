using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ApiTester.Core;

/// <summary>Opens the byte stream a direct request runs over: a named pipe, a Unix socket, or a
/// TCP connection — re-pointed by <c>--connect-to</c> or <c>--resolve</c>, made from
/// <c>--interface</c>'s address, restricted by <c>-4</c>/<c>-6</c>, or tunnelled through an explicit
/// proxy by hand when <c>--proxy-header</c> must go into the CONNECT. TLS goes on top of whatever
/// this returns, still naming the URL's host.</summary>
public static class ConnectionDialer
{
    public static async Task<Stream> DialAsync(TransportOptions transport, DnsEndPoint target, CancellationToken ct)
    {
        if (transport.NamedPipe is { } pipe)
        {
            var (server, name) = PipeName(pipe);
            var stream = new NamedPipeClientStream(server, name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await stream.ConnectAsync(ct); }
            catch { await stream.DisposeAsync(); throw; }
            return stream;
        }
        if (transport.UnixSocket is { } path)
        {
            var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try { await unix.ConnectAsync(new UnixDomainSocketEndPoint(path), ct); }
            catch { unix.Dispose(); throw; }
            return new NetworkStream(unix, ownsSocket: true);
        }

        if (TunnelProxy(transport, target) is { } proxy)
            return await TunnelAsync(transport, proxy, target, ct);

        var (host, port) = Destination(transport, target.Host, target.Port);
        return new NetworkStream(await ConnectTcpAsync(transport, host, port, ct), ownsSocket: true);
    }

    /// <summary>Where a TCP connection for <paramref name="host"/>:<paramref name="port"/> really
    /// goes: a <c>--connect-to</c> rule first, then a <c>--resolve</c> pin.</summary>
    public static (string Host, int Port) Destination(TransportOptions transport, string host, int port)
    {
        if (transport.ConnectTo.FirstOrDefault(c => c.Matches(host, port)) is { } rule)
        {
            host = rule.ToHost.Length == 0 ? host : rule.ToHost;
            port = rule.ToPort ?? port;
        }
        if (transport.Resolve.FirstOrDefault(r => r.Port == port && string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase)) is { } pin)
            host = pin.Address;
        return (host, port);
    }

    private static async Task<Socket> ConnectTcpAsync(TransportOptions transport, string host, int port, CancellationToken ct)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await AddressSelection.ResolveAsync(host, transport.AddressFamily, ct);
        IPAddress? local = transport.LocalAddress is null ? null : IPAddress.Parse(transport.LocalAddress);
        if (local is not null)
        {
            // A source address can only reach destinations of its own family.
            addresses = addresses.Where(a => a.AddressFamily == local.AddressFamily).ToArray();
            if (addresses.Length == 0)
                throw new SocketException((int)SocketError.AddressFamilyNotSupported);
        }
        Exception? last = null;
        foreach (var address in addresses)
        {
            Socket Create()
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { if (local is not null) socket.Bind(new IPEndPoint(local, 0)); }
                catch { socket.Dispose(); throw; }
                return socket;
            }
            try
            {
                return await LoopbackConnect.ConnectAsync(Create, new IPEndPoint(address, port), ct);
            }
            catch (Exception ex) when (ex is SocketException && !ct.IsCancellationRequested)
            {
                last = ex;
            }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>The explicit http:// proxy an https request is tunnelled through by hand: only when
    /// <c>--proxy-header</c> asks for headers the HTTP stack cannot put into a CONNECT.</summary>
    internal static Uri? TunnelProxy(TransportOptions transport, DnsEndPoint target)
    {
        if (transport.ProxyHeaders.Count == 0 || transport.Proxy != ProxyMode.Explicit) return null;
        if (!Uri.TryCreate(transport.ProxyUrl, UriKind.Absolute, out var proxy) || proxy.Scheme != Uri.UriSchemeHttp) return null;
        var asUri = new UriBuilder(Uri.UriSchemeHttps, target.Host, target.Port).Uri;
        // Only the user's own bypass list: WebProxy.IsBypassed would also skip every loopback
        // address, which the handler's proxied path does not do.
        if (ProxyBypass.IsBypassed(transport.NoProxy, asUri)) return null;
        return proxy;
    }

    private static async Task<Stream> TunnelAsync(TransportOptions transport, Uri proxy, DnsEndPoint target, CancellationToken ct)
    {
        var socket = await ConnectTcpAsync(transport, proxy.Host, proxy.Port, ct);
        var stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            string authority = target.Host.Contains(':') ? $"[{target.Host}]:{target.Port}" : $"{target.Host}:{target.Port}";
            var head = new StringBuilder();
            head.Append("CONNECT ").Append(authority).Append(" HTTP/1.1\r\nHost: ").Append(authority).Append("\r\n");
            if (!string.IsNullOrEmpty(transport.ProxyUser))
                head.Append("Proxy-Authorization: Basic ")
                    .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{transport.ProxyUser}:{transport.ProxyPassword}"))).Append("\r\n");
            foreach (var h in transport.ProxyHeaders) head.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
            head.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);

            // The proxy's answer, byte by byte up to the blank line, so nothing of the tunnel's
            // first TLS bytes is swallowed.
            var answer = new StringBuilder();
            var one = new byte[1];
            while (!answer.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one, ct) == 0) throw new IOException("The proxy closed the connection before answering CONNECT.");
                answer.Append((char)one[0]);
                if (answer.Length > 16384) throw new IOException("The proxy's answer to CONNECT is too long.");
            }
            string status = answer.ToString().Split("\r\n")[0];
            var parts = status.Split(' ', 3);
            if (parts.Length < 2 || parts[1] != "200")
                throw new HttpRequestException(HttpRequestError.ProxyTunnelError, $"The proxy refused the tunnel: {status}");
            return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    /// <summary>"docker_engine", "\\.\pipe\docker_engine" or "\\server\pipe\name".</summary>
    internal static (string Server, string Name) PipeName(string spec)
    {
        string s = spec.Replace('/', '\\');
        if (s.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = s[2..].Split('\\', 3);
            if (parts.Length == 3 && parts[1].Equals("pipe", StringComparison.OrdinalIgnoreCase))
                return (parts[0], parts[2]);
        }
        return (".", s.TrimStart('\\'));
    }
}

using System.Net;
using System.Net.Sockets;

namespace ApiTester.Core;

/// <summary>A refused connection to this machine, answered at once rather than after two seconds.
///
/// <para>Windows treats a refused connect (a RST in answer to the SYN) like a lost SYN and tries
/// twice more before giving up, so connecting to a port nothing listens on costs about 2 s even
/// on loopback. That is paid by every send to a local server that is down, and by every new
/// connection to http://localhost:&lt;port&gt; when the server listens on 127.0.0.1 only —
/// "localhost" is tried as ::1 first, refused there, and only then 127.0.0.1.</para>
///
/// <para>SIO_TCP_INITIAL_RTO with TCP_INITIAL_RTO_NO_SYN_RETRANSMISSIONS turns the retries off
/// for one socket. Only a loopback destination gets it: loopback loses no packets, so a retry
/// there can only ever repeat a refusal, while on a real network a SYN can be lost and the retry
/// is what recovers it.</para>
///
/// <para>A refusal is also how Windows answers when something does listen but its queue of
/// connections waiting to be accepted is full — a busy server, or one with a small queue (Python's
/// http.server keeps 5) under a burst, `bench` at a local server — and there the retries are what
/// let the connection in once the queue drains. So a refused connection is looked at once more:
/// when the system's table of listening ports has something on that port, the connect is made
/// again as Windows makes it by itself, retries and all. Only a port nothing listens on is
/// answered at once.</para></summary>
public static class LoopbackConnect
{
    // _WSAIOW(IOC_VENDOR, 17)
    private const int SioTcpInitialRto = unchecked((int)0x98000011);
    // TCP_INITIAL_RTO_PARAMETERS { USHORT Rtt = TCP_INITIAL_RTO_UNSPECIFIED_RTT;
    //                              UCHAR MaxSynRetransmissions = TCP_INITIAL_RTO_NO_SYN_RETRANSMISSIONS; }
    private static readonly byte[] NoSynRetransmissions = [0xFF, 0xFF, 0xFE, 0x00];

    /// <summary>A socket from <paramref name="create"/>, connected to <paramref name="target"/>: at once
    /// refused when nothing listens on a loopback port, and otherwise as Windows connects — see the
    /// class. The socket is the caller's on success and disposed on failure.</summary>
    public static async ValueTask<Socket> ConnectAsync(Func<Socket> create, EndPoint target, CancellationToken ct)
    {
        var socket = create();
        bool fast = TurnOffRetries(socket, target);
        try
        {
            await socket.ConnectAsync(target, ct);
            return socket;
        }
        catch (SocketException ex) when (fast && ex.SocketErrorCode == SocketError.ConnectionRefused &&
                                         !ct.IsCancellationRequested && SomethingListens(target))
        {
            socket.Dispose();
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        // Something listens there and refused anyway: its queue was full. Again, the way Windows
        // connects by itself, whose retries give the queue time to drain.
        var again = create();
        try
        {
            await again.ConnectAsync(target, ct);
            return again;
        }
        catch
        {
            again.Dispose();
            throw;
        }
    }

    /// <summary>For a handler that connects by itself (no ConnectCallback of ours): connects the
    /// way the handler would — one dual-mode socket, Nagle off, every address of the name in turn —
    /// through <see cref="ConnectAsync"/>. A handler that already has a callback is left alone; on
    /// other systems nothing is changed.</summary>
    public static void Install(System.Net.Http.SocketsHttpHandler handler)
    {
        if (!OperatingSystem.IsWindows() || handler.ConnectCallback is not null) return;
        handler.ConnectCallback = async (context, ct) =>
        {
            var socket = await ConnectAsync(() => new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true },
                context.DnsEndPoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        };
    }

    /// <summary>An IP address as an <see cref="IPEndPoint"/>, a name as a <see cref="DnsEndPoint"/>.</summary>
    public static EndPoint EndPointFor(string host, int port) =>
        IPAddress.TryParse(host.Trim('[', ']'), out var ip) ? new IPEndPoint(ip, port) : new DnsEndPoint(host, port);

    internal static bool IsLoopbackName(string host)
    {
        string h = host.Trim('[', ']');
        if (IPAddress.TryParse(h, out var ip)) return IPAddress.IsLoopback(ip);
        h = h.TrimEnd('.');
        return h.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoopback(EndPoint target) => target switch
    {
        IPEndPoint ip => IPAddress.IsLoopback(ip.Address),
        DnsEndPoint dns => IsLoopbackName(dns.Host),
        _ => false,
    };

    /// <summary>Whether the retries were turned off: Windows, a loopback destination, and a system
    /// that has the option.</summary>
    private static bool TurnOffRetries(Socket socket, EndPoint target)
    {
        if (!OperatingSystem.IsWindows() || !IsLoopback(target)) return false;   // the delay is Windows' alone
        try
        {
            socket.IOControl(SioTcpInitialRto, NoSynRetransmissions, null);
            return true;
        }
        catch (SocketException) { return false; /* an older Windows: the connect keeps its retries */ }
    }

    /// <summary>Whether anything listens on the target's port where a connection to it could land.
    /// For an IPv6 target only IPv6 listeners count, so a server on 127.0.0.1 alone still leaves
    /// a refused ::1 answered at once (and "localhost" goes on to 127.0.0.1); for an IPv4 target an
    /// IPv6 listener on [::] counts too, since a dual-mode one takes IPv4. When the table cannot be
    /// read, the answer is yes: the connect then behaves as it always did.</summary>
    internal static bool SomethingListens(EndPoint target)
    {
        int port;
        AddressFamily? family = null;
        switch (target)
        {
            case IPEndPoint ip: port = ip.Port; family = ip.AddressFamily; break;
            case DnsEndPoint dns: port = dns.Port; break;
            default: return true;
        }
        try
        {
            foreach (var listener in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (listener.Port != port) continue;
                var a = listener.Address;
                bool reachable = IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any);
                if (!reachable) continue;
                if (family is null || a.AddressFamily == family) return true;
                if (family == AddressFamily.InterNetwork && a.Equals(IPAddress.IPv6Any)) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or PlatformNotSupportedException)
        {
            return true;
        }
    }
}

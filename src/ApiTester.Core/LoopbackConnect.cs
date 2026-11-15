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
/// is what recovers it.</para></summary>
public static class LoopbackConnect
{
    // _WSAIOW(IOC_VENDOR, 17)
    private const int SioTcpInitialRto = unchecked((int)0x98000011);
    // TCP_INITIAL_RTO_PARAMETERS { USHORT Rtt = TCP_INITIAL_RTO_UNSPECIFIED_RTT;
    //                              UCHAR MaxSynRetransmissions = TCP_INITIAL_RTO_NO_SYN_RETRANSMISSIONS; }
    private static readonly byte[] NoSynRetransmissions = [0xFF, 0xFF, 0xFE, 0x00];

    /// <summary>For a socket about to connect to <paramref name="destination"/>.</summary>
    public static void Prepare(Socket socket, IPAddress destination)
    {
        if (IPAddress.IsLoopback(destination)) TurnOffRetries(socket);
    }

    /// <summary>For a socket about to connect to <paramref name="host"/> by name: a loopback
    /// literal, localhost, or a *.localhost name (which resolve to loopback by definition).</summary>
    public static void Prepare(Socket socket, string host)
    {
        if (IsLoopbackName(host)) TurnOffRetries(socket);
    }

    /// <summary>For a handler that connects by itself (no ConnectCallback of ours): connects the
    /// way the handler would — one dual-mode socket, Nagle off, every address of the name in turn —
    /// with the retries turned off when the name is a loopback one. A handler that already has a
    /// callback is left alone; on other systems nothing is changed.</summary>
    public static void Install(System.Net.Http.SocketsHttpHandler handler)
    {
        if (!OperatingSystem.IsWindows() || handler.ConnectCallback is not null) return;
        handler.ConnectCallback = async (context, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                Prepare(socket, context.DnsEndPoint.Host);
                await socket.ConnectAsync(context.DnsEndPoint, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
    }

    internal static bool IsLoopbackName(string host)
    {
        string h = host.Trim('[', ']');
        if (IPAddress.TryParse(h, out var ip)) return IPAddress.IsLoopback(ip);
        h = h.TrimEnd('.');
        return h.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static void TurnOffRetries(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;   // the delay is Windows' alone
        try { socket.IOControl(SioTcpInitialRto, NoSynRetransmissions, null); }
        catch (SocketException) { /* an older Windows without it: the connect just keeps its retries */ }
    }
}

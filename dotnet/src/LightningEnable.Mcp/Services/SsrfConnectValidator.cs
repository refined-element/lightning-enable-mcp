using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Connect-time SSRF guard for the L402 HTTP client
/// (<c>AddHttpClient&lt;IL402HttpClient, L402HttpClient&gt;</c> in
/// <c>Program.cs</c>). The URL passed to <c>access_l402_resource</c> is
/// attacker-influenceable, and <c>AccessL402ResourceTool.ValidateUrl</c> only
/// resolves-and-validates the INITIAL URL — then the socket layer resolves the
/// host AGAIN to connect, a TOCTOU window a DNS-rebind attacker can use to swap
/// in <c>169.254.169.254</c> (cloud metadata) or a private range between the two
/// lookups.
/// <para/>
/// Wired into the client's <see cref="SocketsHttpHandler.ConnectCallback"/>, this
/// validates the address the socket is ABOUT TO CONNECT TO and connects to that
/// EXACT validated set (no third resolution) — closing the window. Combined with
/// <c>AllowAutoRedirect = false</c> on the same handler, a redirect pivot to a
/// private/metadata host cannot be auto-followed either. Belt-and-suspenders with
/// the initial-URL validator.
/// </summary>
internal static class SsrfConnectValidator
{
    /// <summary>
    /// Resolves <paramref name="host"/> (or parses it when it is already an IP
    /// literal), rejects the whole set if ANY address is private/reserved (fail
    /// closed), and returns the validated addresses to connect to. Throws
    /// <see cref="HttpRequestException"/> on a disallowed or unresolvable host.
    /// The message names no internal host or IP, so nothing about the internal
    /// network is echoed back through the tool result.
    /// </summary>
    public static async Task<IPAddress[]> ResolveValidatedAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, ct);

        if (addresses.Length == 0)
        {
            throw new HttpRequestException("SSRF guard: target did not resolve to any address.");
        }

        foreach (var address in addresses)
        {
            if (PrivateIpAddressDetector.IsPrivate(address))
            {
                throw new HttpRequestException(
                    "SSRF guard: target resolved to a private/reserved IP address at connect time.");
            }
        }

        return addresses;
    }

    /// <summary>
    /// <see cref="SocketsHttpHandler.ConnectCallback"/> body: validate the
    /// connect-time IPs and open the TCP connection to exactly that validated set.
    /// The handler layers TLS on top of the returned <see cref="NetworkStream"/>
    /// for https targets, using the request's SNI/Host — independent of the IP we
    /// connected to.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await ResolveValidatedAsync(context.DnsEndPoint.Host, ct);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

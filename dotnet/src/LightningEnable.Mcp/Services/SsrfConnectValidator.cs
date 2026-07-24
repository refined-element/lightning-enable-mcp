using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Connect-time SSRF guard for the HTTP clients that fetch attacker-influenceable
/// URLs — the L402 client and the discover_api manifest client (both wired in
/// <c>Program.cs</c>). The URL passed to those tools is agent-supplied, and the
/// cheap <see cref="SsrfUrlGuard"/> pre-check only classifies IP literals / blocked
/// hostnames — the socket layer still resolves the host to connect, a TOCTOU window
/// a DNS-rebind attacker can use to swap in <c>169.254.169.254</c> (cloud metadata)
/// or a private range between the two lookups.
/// <para/>
/// Wired into each client's <see cref="SocketsHttpHandler.ConnectCallback"/>, this
/// validates the address the socket is ABOUT TO CONNECT TO and connects to that
/// EXACT validated set (no third resolution) — closing the window. Because the
/// callback fires on EVERY connection the handler opens, including each
/// auto-redirect hop, <c>AllowAutoRedirect = true</c> is safe: a redirect pivot to a
/// private/metadata host is rejected on the redirected hop.
/// </summary>
internal static class SsrfConnectValidator
{
    /// <summary>
    /// Resolves <paramref name="host"/> (or parses it when it is already an IP
    /// literal) and returns the addresses to connect to. Always fails closed on an
    /// empty resolution. When <paramref name="enforcePrivateIpGuard"/> is
    /// <c>true</c> (the default — a direct-to-target connection), the whole set is
    /// rejected if ANY address is private/reserved. Throws
    /// <see cref="HttpRequestException"/> on a disallowed or unresolvable host. The
    /// message names no internal host or IP, so nothing about the internal network is
    /// echoed back through the tool result.
    /// </summary>
    public static async Task<IPAddress[]> ResolveValidatedAsync(
        string host, CancellationToken ct, bool enforcePrivateIpGuard = true)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, ct);

        if (addresses.Length == 0)
        {
            throw new HttpRequestException("SSRF guard: target did not resolve to any address.");
        }

        if (enforcePrivateIpGuard)
        {
            foreach (var address in addresses)
            {
                if (PrivateIpAddressDetector.IsPrivate(address))
                {
                    throw new HttpRequestException(
                        "SSRF guard: target resolved to a private/reserved IP address at connect time.");
                }
            }
        }

        return addresses;
    }

    /// <summary>
    /// <see cref="SocketsHttpConnectionContext.DnsEndPoint"/> is the endpoint the
    /// handler is about to connect to. When an HTTP(S) proxy is configured
    /// (HTTP_PROXY / HTTPS_PROXY, surfaced via <see cref="HttpClient.DefaultProxy"/>),
    /// that endpoint is the PROXY, not the target — so a corporate proxy on a private
    /// IP (e.g. 10.x) would otherwise be SSRF-rejected and break every fetch. This
    /// returns <c>true</c> when the connect target is the operator-configured proxy
    /// for <paramref name="requestUri"/>, in which case the private-IP guard is
    /// skipped (the operator chose the proxy — it is trusted). Direct-connection
    /// targets are never matched here, so they stay fully validated. Fails closed:
    /// any error / no-proxy / mismatch returns <c>false</c> (keep enforcing the guard).
    /// </summary>
    internal static bool ShouldTrustAsConfiguredProxy(Uri? requestUri, DnsEndPoint connectTarget, IWebProxy? proxy)
    {
        if (requestUri == null || proxy == null)
        {
            return false;
        }

        try
        {
            if (proxy.IsBypassed(requestUri))
            {
                return false;
            }

            var proxyUri = proxy.GetProxy(requestUri);
            if (proxyUri == null)
            {
                return false;
            }

            // The connect target is the proxy iff host+port match the resolved proxy.
            return string.Equals(proxyUri.Host, connectTarget.Host, StringComparison.OrdinalIgnoreCase)
                && proxyUri.Port == connectTarget.Port;
        }
        catch
        {
            // Never skip validation because proxy detection failed.
            return false;
        }
    }

    /// <summary>
    /// <see cref="SocketsHttpHandler.ConnectCallback"/> body: validate the
    /// connect-time IPs and open the TCP connection to exactly that validated set.
    /// The handler layers TLS on top of the returned <see cref="NetworkStream"/>
    /// for https targets, using the request's SNI/Host — independent of the IP we
    /// connected to.
    /// <para/>
    /// If the connection is to the operator-configured proxy (not the direct
    /// target), the private-IP guard is skipped for it — see
    /// <see cref="ShouldTrustAsConfiguredProxy"/>. Residual: when a proxy is used the
    /// TARGET's real IP is resolved by the proxy, not here, so the connect-time IP
    /// guard cannot see it — a private-IP-proxy deployment relies on the cheap
    /// hostname pre-check (SsrfUrlGuard) for the target. The no-proxy path is
    /// unchanged and fully validates the target IP.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var trustAsProxy = ShouldTrustAsConfiguredProxy(
            context.InitialRequestMessage.RequestUri,
            context.DnsEndPoint,
            HttpClient.DefaultProxy);

        var addresses = await ResolveValidatedAsync(
            context.DnsEndPoint.Host, ct, enforcePrivateIpGuard: !trustAsProxy);

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

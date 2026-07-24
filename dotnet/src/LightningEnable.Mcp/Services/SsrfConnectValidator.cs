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
/// EXACT validated set (no third resolution) — closing the window.
/// <para/>
/// The clients run with <c>AllowAutoRedirect = false</c> (see <c>Program.cs</c>).
/// The callback firing per-hop does NOT make auto-redirect safe — it validates the
/// IP of each hop but cannot stop .NET's redirect handler from re-sending
/// agent-supplied custom headers (X-Api-Key, Cookie, ...) to a cross-origin redirect
/// target, nor stop the L402 flow from paying a provider that host-redirects before
/// its 402 and then loses its Authorization header on the host change. With redirects
/// unfollowed the initial (fully validated) URL is the only fetch; a 3xx is surfaced
/// to the agent as an actionable "call again with the target" result, never followed.
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
    /// <para/>
    /// <paramref name="resolver"/> is a test seam (defaults to real DNS) so the guard's
    /// resolve-and-reject logic is unit-testable without a network.
    /// </summary>
    public static async Task<IPAddress[]> ResolveValidatedAsync(
        string host, CancellationToken ct, bool enforcePrivateIpGuard = true,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await (resolver ?? DefaultResolveAsync)(host, ct);

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

    private static Task<IPAddress[]> DefaultResolveAsync(string host, CancellationToken ct) =>
        Dns.GetHostAddressesAsync(host, ct);

    /// <summary>
    /// Proxy-case TARGET validation. When an operator-configured proxy is used the
    /// ConnectCallback connects to the PROXY, and the PROXY (not us) resolves and
    /// connects to the target — so our connect-time IP guard never sees the target's
    /// real IP. That is the ONE case where connect-time target validation is
    /// unavailable, so we DNS-pre-resolve the target host here and reject a
    /// private/metadata result before the fetch. Only invoked in the proxy case; the
    /// no-proxy path keeps the connect-time guard and does not double-resolve.
    /// <para/>
    /// <b>Fails closed</b>: an unresolvable/failed lookup refuses the request (this
    /// pre-resolution is the only target protection available here). <b>Residual
    /// TOCTOU</b>: the proxy re-resolves the target when it fetches, so a DNS rebind
    /// between this lookup and the proxy's own lookup can still slip a private IP past
    /// — narrow (needs attacker-controlled authoritative DNS with a sub-lookup TTL) and
    /// documented, not silently ignored. <paramref name="resolver"/> is a test seam.
    /// </summary>
    internal static async Task ValidateProxiedTargetHostAsync(
        Uri? targetUri, CancellationToken ct,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null)
    {
        if (targetUri == null)
        {
            throw new HttpRequestException(
                "SSRF guard: proxied request has no target URI to validate.");
        }

        try
        {
            // enforcePrivateIpGuard: true — reject the target if any resolved address is
            // private/reserved. We discard the addresses (the proxy does the real connect).
            await ResolveValidatedAsync(targetUri.Host, ct, enforcePrivateIpGuard: true, resolver: resolver);
        }
        catch (HttpRequestException)
        {
            // Already the generic, non-echoing SSRF-guard shape (private IP / empty set).
            throw;
        }
        catch (Exception)
        {
            // DNS failure or any other resolution error → fail closed. In the proxy case
            // this pre-resolution is the only target validation we have, so an
            // unvalidatable target must be refused, not allowed through the proxy.
            throw new HttpRequestException(
                "SSRF guard: proxied target host could not be validated; refusing to fetch through the proxy.");
        }
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
    /// target), the private-IP guard is skipped for the PROXY endpoint — see
    /// <see cref="ShouldTrustAsConfiguredProxy"/>. But because the proxy (not us) then
    /// resolves and connects to the TARGET, the connect-time IP guard never sees the
    /// target's real IP. The cheap <see cref="SsrfUrlGuard"/> hostname pre-check does
    /// NOT protect the target either — it cannot catch a hostname that RESOLVES to a
    /// private IP (it does no DNS). So in the proxy case we DNS-pre-resolve and validate
    /// the target host here (<see cref="ValidateProxiedTargetHostAsync"/>, fail-closed)
    /// before connecting to the proxy. Residual: a rebind between our pre-resolution and
    /// the proxy's own lookup is a documented TOCTOU. The no-proxy path is unchanged and
    /// fully validates the target IP at connect time (no double DNS).
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var trustAsProxy = ShouldTrustAsConfiguredProxy(
            context.InitialRequestMessage.RequestUri,
            context.DnsEndPoint,
            HttpClient.DefaultProxy);

        // Proxy case only: validate the TARGET host by DNS pre-resolution, since the
        // proxy — not this callback — opens the target socket. Fails closed.
        if (trustAsProxy)
        {
            await ValidateProxiedTargetHostAsync(context.InitialRequestMessage.RequestUri, ct);
        }

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

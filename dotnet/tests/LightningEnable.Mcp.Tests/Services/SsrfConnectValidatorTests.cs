using System.Net;
using System.Net.Http;
using LightningEnable.Mcp.Services;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// SSRF TOCTOU / DNS-rebind guard (F-10d): <see cref="SsrfConnectValidator"/> is
/// the connect-time check wired into the L402 client's ConnectCallback. These lock
/// its fail-closed behavior deterministically — IP literals and loopback resolve
/// without any network, so the tests never hit DNS.
/// </summary>
public class SsrfConnectValidatorTests
{
    [Fact]
    public async Task ResolveValidated_PrivateIpLiteral_ThrowsFailingClosed()
    {
        var act = async () =>
            await SsrfConnectValidator.ResolveValidatedAsync("10.0.0.5", CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("private/reserved");
    }

    [Fact]
    public async Task ResolveValidated_CloudMetadataIpLiteral_Throws()
    {
        // 169.254.169.254 (link-local cloud metadata) is the canonical SSRF target.
        var act = async () =>
            await SsrfConnectValidator.ResolveValidatedAsync("169.254.169.254", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ResolveValidated_Loopback_Throws()
    {
        // "localhost" resolves offline (loopback / hosts file) to 127.0.0.1 / ::1,
        // both private → rejected. Deterministic without a network.
        var act = async () =>
            await SsrfConnectValidator.ResolveValidatedAsync("localhost", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ResolveValidated_PublicIpLiteral_ReturnsValidatedAddress()
    {
        var addresses =
            await SsrfConnectValidator.ResolveValidatedAsync("93.184.216.34", CancellationToken.None);

        addresses.Should().ContainSingle().Which.Should().Be(IPAddress.Parse("93.184.216.34"));
    }

    [Fact]
    public async Task ResolveValidated_MessageDoesNotEchoTheHost()
    {
        // The rejection reason must not disclose the internal target back to the
        // caller — nothing about the internal network is echoed.
        var act = async () =>
            await SsrfConnectValidator.ResolveValidatedAsync("192.168.13.37", CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().NotContain("192.168.13.37");
    }

    // ---- The ConnectCallback blocks a private/metadata target at connect time,
    // closing the DNS-rebind window on the ONLY fetch. Production runs the clients with
    // AllowAutoRedirect = false (see Program.cs): the callback firing per-hop does NOT
    // make auto-redirect safe (a redirect would re-send agent custom headers cross-origin
    // and can lose the L402 header mid-payment), so redirects are surfaced as actionable,
    // never followed. This pins the connect-time guard with the production redirect flag. ----

    [Fact]
    public async Task ConnectCallback_BlocksPrivateTargetAtConnect()
    {
        // A handler configured exactly like the L402 / discover_api clients
        // (AllowAutoRedirect = false + this ConnectCallback). UseProxy = false pins the
        // connect target to the direct address so the test is deterministic regardless
        // of any ambient HTTP_PROXY. The callback rejects the private target at connect time.
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = SsrfConnectValidator.ConnectAsync,
        };
        using var client = new HttpClient(handler);

        var act = async () => await client.GetAsync("http://10.0.0.5/");

        var ex = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        var chain = ex.Message + " " + (ex.InnerException?.Message ?? string.Empty);
        chain.Should().Contain("private/reserved");
    }

    // ---- FIX 5: an operator-configured proxy on a private IP must not break fetches.
    // The connection to the proxy itself is trusted; direct-connection targets stay validated. ----

    [Fact]
    public void ShouldTrust_ConnectToConfiguredProxy_ReturnsTrue()
    {
        // Corporate proxy on a private IP (10.x). The connect endpoint IS the proxy.
        var proxy = new WebProxy("10.0.0.9", 3128);
        var requestUri = new Uri("http://public-api.example.com/data");
        var connectTarget = new DnsEndPoint("10.0.0.9", 3128);

        SsrfConnectValidator.ShouldTrustAsConfiguredProxy(requestUri, connectTarget, proxy)
            .Should().BeTrue("connecting to the operator-configured proxy is trusted");
    }

    [Fact]
    public void ShouldTrust_DirectTargetWithProxyConfigured_ReturnsFalse()
    {
        // A connect target that is NOT the proxy host is a direct target → still validated.
        var proxy = new WebProxy("10.0.0.9", 3128);
        var requestUri = new Uri("http://public-api.example.com/data");
        var connectTarget = new DnsEndPoint("93.184.216.34", 80);

        SsrfConnectValidator.ShouldTrustAsConfiguredProxy(requestUri, connectTarget, proxy)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldTrust_NoProxyConfigured_ReturnsFalse()
    {
        var requestUri = new Uri("http://public-api.example.com/");
        var connectTarget = new DnsEndPoint("93.184.216.34", 80);

        SsrfConnectValidator.ShouldTrustAsConfiguredProxy(requestUri, connectTarget, proxy: null)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldTrust_ProxyBypassedForTarget_ReturnsFalse()
    {
        // If the proxy bypasses this target the connection is direct → not the proxy → validated.
        var proxy = new WebProxy("10.0.0.9", 3128)
        {
            BypassProxyOnLocal = false,
            BypassList = new[] { @"public-api\.example\.com" },
        };
        var requestUri = new Uri("http://public-api.example.com/");
        var connectTarget = new DnsEndPoint("93.184.216.34", 80);

        SsrfConnectValidator.ShouldTrustAsConfiguredProxy(requestUri, connectTarget, proxy)
            .Should().BeFalse();
    }

    [Fact]
    public async Task ResolveValidated_ProxyGuardDisabled_AllowsPrivateProxyAddress()
    {
        // When the connection is to the trusted proxy the private-IP guard is skipped,
        // so a proxy on a private IP resolves instead of being rejected.
        var addresses = await SsrfConnectValidator.ResolveValidatedAsync(
            "10.0.0.9", CancellationToken.None, enforcePrivateIpGuard: false);

        addresses.Should().ContainSingle().Which.Should().Be(IPAddress.Parse("10.0.0.9"));
    }

    // ---- FIX B: proxy-trust re-opened target SSRF. Trusting the proxy ENDPOINT must NOT
    // leave the TARGET unvalidated. When a proxy is configured the proxy (not our
    // ConnectCallback) resolves+connects to the target, so we DNS-pre-resolve the target
    // host and reject a private/metadata result before the fetch. The cheap hostname
    // pre-check cannot catch a hostname that RESOLVES to a private IP — these prove the
    // pre-resolution does, fails closed on resolution error, and still allows a public
    // target. A test resolver keeps them deterministic without touching the network. ----

    private static Func<string, CancellationToken, Task<IPAddress[]>> ResolverReturning(params string[] ips) =>
        (_, _) => Task.FromResult(ips.Select(IPAddress.Parse).ToArray());

    [Fact]
    public async Task ValidateProxiedTargetHost_TargetResolvesToMetadataIp_Refused()
    {
        // The canonical SSRF pivot: a benign-looking hostname whose DNS answer is the
        // cloud-metadata IP. Even with a (trusted) proxy configured, this must be refused
        // BEFORE the proxy is asked to fetch it.
        var act = async () => await SsrfConnectValidator.ValidateProxiedTargetHostAsync(
            new Uri("http://totally-legit.example.com/"), CancellationToken.None,
            ResolverReturning("169.254.169.254"));

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ValidateProxiedTargetHost_TargetResolvesToPrivateIp_Refused()
    {
        var act = async () => await SsrfConnectValidator.ValidateProxiedTargetHostAsync(
            new Uri("http://internal-thing.example.com/"), CancellationToken.None,
            ResolverReturning("10.1.2.3"));

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().NotContain("10.1.2.3", "the internal target must not be echoed back");
    }

    [Fact]
    public async Task ValidateProxiedTargetHost_MixedPublicAndPrivate_FailsClosedOnAny()
    {
        // Fail closed: one private answer in the set blocks the whole target, even if a
        // public answer is also present (a rebinder can round-robin).
        var act = async () => await SsrfConnectValidator.ValidateProxiedTargetHostAsync(
            new Uri("http://mixed.example.com/"), CancellationToken.None,
            ResolverReturning("93.184.216.34", "127.0.0.1"));

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ValidateProxiedTargetHost_ResolutionError_FailsClosed()
    {
        // If the target host cannot be resolved at all, refuse — this pre-resolution is
        // the only target protection available in the proxy case, so it must not allow.
        Func<string, CancellationToken, Task<IPAddress[]>> throwingResolver =
            (_, _) => throw new System.Net.Sockets.SocketException();

        var act = async () => await SsrfConnectValidator.ValidateProxiedTargetHostAsync(
            new Uri("http://does-not-resolve.example.com/"), CancellationToken.None, throwingResolver);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("refusing to fetch through the proxy");
    }

    [Fact]
    public async Task ValidateProxiedTargetHost_EmptyResolution_FailsClosed()
    {
        var act = async () => await SsrfConnectValidator.ValidateProxiedTargetHostAsync(
            new Uri("http://empty.example.com/"), CancellationToken.None,
            ResolverReturning());

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ValidateProxiedTargetHost_PublicTarget_Allowed()
    {
        // A public target resolves cleanly → no throw. This is the corporate-proxy-to-a
        // -public-API happy path that FIX B must keep working.
        var act = async () => await SsrfConnectValidator.ValidateProxiedTargetHostAsync(
            new Uri("http://public-api.example.com/"), CancellationToken.None,
            ResolverReturning("93.184.216.34"));

        await act.Should().NotThrowAsync();
    }
}

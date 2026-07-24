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

    // ---- FIX 2: AllowAutoRedirect = true is safe because the ConnectCallback fires
    // on every connection the handler opens (initial request AND each redirect hop). ----

    [Fact]
    public async Task ConnectCallback_OnHandlerWithAutoRedirect_BlocksPrivateTargetAtConnect()
    {
        // A handler configured exactly like the L402 / discover_api clients
        // (AllowAutoRedirect = true + this ConnectCallback). UseProxy = false pins the
        // connect target to the direct address so the test is deterministic regardless
        // of any ambient HTTP_PROXY. The callback rejects the private target at connect
        // time — and because a redirected hop opens a NEW connection through the SAME
        // callback, a 3xx pivot to a private/metadata IP is rejected identically, while
        // a hop to a public host resolves to validated public addresses
        // (ResolveValidated_PublicIpLiteral_ReturnsValidatedAddress).
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
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
}

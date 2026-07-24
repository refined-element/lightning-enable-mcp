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
}

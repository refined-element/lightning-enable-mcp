using System.Text;
using System.Text.Json;
using FluentAssertions;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;

namespace LightningEnable.Mcp.Tests.Tools;

/// <summary>
/// MPP draft-00 support in the pay_l402_challenge tool: passing the raw
/// WWW-Authenticate value of a modern Payment challenge (challengeHeader) pays the
/// invoice inside it and returns a single-use modern Payment credential.
/// </summary>
public class PayL402ChallengeModernTests
{
    private const string FixtureInvoice = "lnbc100n1pjtest"; // 10 sats
    private const string FixturePreimage = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FutureExpiry = "2099-01-01T00:00:00Z";

    private readonly Mock<IL402HttpClient> _l402ClientMock = new();

    private static string B64UrlNoPad(string s) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string RequestJson(string amount = "10") =>
        $"{{\"amount\":\"{amount}\",\"currency\":\"sat\",\"methodDetails\":{{\"invoice\":\"{FixtureInvoice}\"}}}}";

    private static string ModernHeader(string requestEncoded, string expires = FutureExpiry) =>
        $"Payment id=\"chal-1\", realm=\"api.example.com\", method=\"lightning\", intent=\"charge\", request=\"{requestEncoded}\", expires=\"{expires}\"";

    private static JsonElement DecodeCredential(string credential)
    {
        var padded = credential.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
    }

    [Fact]
    public async Task ModernChallengeHeader_PaysAndReturnsModernCredential()
    {
        var encoded = B64UrlNoPad(RequestJson());
        _l402ClientMock.Setup(c => c.PayChallengeAsync(
                null, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FixturePreimage);

        var result = await PayL402ChallengeTool.PayL402Challenge(
            challengeHeader: ModernHeader(encoded),
            l402Client: _l402ClientMock.Object);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("singleUse").GetBoolean().Should().BeTrue();

        var headerValue = json.GetProperty("usage").GetProperty("headerValue").GetString()!;
        headerValue.Should().StartWith("Payment ");
        headerValue.Should().NotContain("preimage=");

        var decoded = DecodeCredential(headerValue["Payment ".Length..]);
        decoded.GetProperty("challenge").GetProperty("request").GetString().Should().Be(encoded);
        decoded.GetProperty("payload").GetProperty("preimage").GetString().Should().Be(FixturePreimage);

        // The invoice was taken from inside the challenge (no separate invoice arg needed).
        _l402ClientMock.Verify(c => c.PayChallengeAsync(
            null, FixtureInvoice, It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ModernChallengeHeader_UppercasePreimage_LowercasedInCredential()
    {
        var encoded = B64UrlNoPad(RequestJson());
        _l402ClientMock.Setup(c => c.PayChallengeAsync(
                null, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FixturePreimage.ToUpperInvariant());

        var result = await PayL402ChallengeTool.PayL402Challenge(
            challengeHeader: ModernHeader(encoded),
            l402Client: _l402ClientMock.Object);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        var headerValue = json.GetProperty("usage").GetProperty("headerValue").GetString()!;
        var decoded = DecodeCredential(headerValue["Payment ".Length..]);
        decoded.GetProperty("payload").GetProperty("preimage").GetString().Should().Be(FixturePreimage);
    }

    [Fact]
    public async Task ExpiredModernChallenge_RefusedBeforePaying()
    {
        var encoded = B64UrlNoPad(RequestJson());

        var result = await PayL402ChallengeTool.PayL402Challenge(
            challengeHeader: ModernHeader(encoded, expires: "2001-01-01T00:00:00Z"),
            l402Client: _l402ClientMock.Object);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("expired");
        _l402ClientMock.Verify(c => c.PayChallengeAsync(
            It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvoiceMismatchingChallenge_Refused()
    {
        var encoded = B64UrlNoPad(RequestJson());

        var result = await PayL402ChallengeTool.PayL402Challenge(
            invoice: "lnbc200n1pjother",
            challengeHeader: ModernHeader(encoded),
            l402Client: _l402ClientMock.Object);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("does not match");
        _l402ClientMock.Verify(c => c.PayChallengeAsync(
            It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MacaroonWithModernChallenge_Refused()
    {
        var encoded = B64UrlNoPad(RequestJson());

        var result = await PayL402ChallengeTool.PayL402Challenge(
            macaroon: "YWJjZGVm",
            challengeHeader: ModernHeader(encoded),
            l402Client: _l402ClientMock.Object);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("macaroon");
    }

    [Fact]
    public async Task UnparseableChallengeHeader_ReturnsError()
    {
        var result = await PayL402ChallengeTool.PayL402Challenge(
            challengeHeader: "Bearer not-a-payment-challenge",
            l402Client: _l402ClientMock.Object);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task NoInvoiceAndNoChallengeHeader_ReturnsInputError()
    {
        var result = await PayL402ChallengeTool.PayL402Challenge(
            l402Client: _l402ClientMock.Object);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("Invoice");
    }

    [Fact]
    public async Task LegacyPaymentChallengeHeader_PaysLegacyMpp()
    {
        var legacyHeader = $"Payment realm=\"api.example.com\", method=\"lightning\", invoice=\"{FixtureInvoice}\", amount=\"10\", currency=\"sat\"";
        _l402ClientMock.Setup(c => c.PayChallengeAsync(
                null, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FixturePreimage);

        var result = await PayL402ChallengeTool.PayL402Challenge(
            challengeHeader: legacyHeader,
            l402Client: _l402ClientMock.Object);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("usage").GetProperty("headerValue").GetString()
            .Should().Be($"Payment method=\"lightning\", preimage=\"{FixturePreimage}\"",
                "a legacy Payment challenge keeps the legacy credential format");
    }
}

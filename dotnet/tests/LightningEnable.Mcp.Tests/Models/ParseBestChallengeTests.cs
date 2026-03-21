using FluentAssertions;
using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Tests.Models;

public class ParseBestChallengeTests
{
    [Fact]
    public void ParseBest_BothHeaders_PrefersL402()
    {
        var headers = new[]
        {
            "L402 macaroon=\"abc123\", invoice=\"lnbc100n1pjl402\"",
            "Payment realm=\"test\", method=\"lightning\", invoice=\"lnbc100n1pjmpp\", amount=\"100\", currency=\"sat\""
        };

        var result = PaymentChallengeParser.ParseBest(headers);

        result.HasChallenge.Should().BeTrue();
        result.IsMpp.Should().BeFalse();
        result.L402.Should().NotBeNull();
        result.L402!.Invoice.Should().Be("lnbc100n1pjl402");
        result.Invoice.Should().Be("lnbc100n1pjl402");
    }

    [Fact]
    public void ParseBest_BothHeaders_StillParsesMpp()
    {
        var headers = new[]
        {
            "L402 macaroon=\"abc123\", invoice=\"lnbc100n1pjl402\"",
            "Payment realm=\"test\", method=\"lightning\", invoice=\"lnbc100n1pjmpp\", amount=\"100\", currency=\"sat\""
        };

        var result = PaymentChallengeParser.ParseBest(headers);

        // Even though L402 is preferred, MPP should still be parsed
        result.Mpp.Should().NotBeNull();
        result.Mpp!.Invoice.Should().Be("lnbc100n1pjmpp");
    }

    [Fact]
    public void ParseBest_OnlyMpp_SelectsMpp()
    {
        var headers = new[]
        {
            "Payment realm=\"test\", method=\"lightning\", invoice=\"lnbc100n1pjmpp\", amount=\"100\", currency=\"sat\""
        };

        var result = PaymentChallengeParser.ParseBest(headers);

        result.HasChallenge.Should().BeTrue();
        result.IsMpp.Should().BeTrue();
        result.Mpp.Should().NotBeNull();
        result.Invoice.Should().Be("lnbc100n1pjmpp");
    }

    [Fact]
    public void ParseBest_OnlyL402_SelectsL402()
    {
        var headers = new[]
        {
            "L402 macaroon=\"abc123\", invoice=\"lnbc100n1pjl402\""
        };

        var result = PaymentChallengeParser.ParseBest(headers);

        result.HasChallenge.Should().BeTrue();
        result.IsMpp.Should().BeFalse();
        result.L402.Should().NotBeNull();
    }

    [Fact]
    public void ParseBest_NoValidHeaders_ReturnsEmpty()
    {
        var headers = new[] { "Bearer token123", "Basic abc" };

        var result = PaymentChallengeParser.ParseBest(headers);

        result.HasChallenge.Should().BeFalse();
        result.IsMpp.Should().BeFalse();
    }

    [Fact]
    public void ParseBest_EmptyCollection_ReturnsEmpty()
    {
        var result = PaymentChallengeParser.ParseBest(Array.Empty<string>());
        result.HasChallenge.Should().BeFalse();
    }

    [Fact]
    public void ParseBest_InvoiceProperty_PrefersL402OverMpp()
    {
        var headers = new[]
        {
            "Payment realm=\"test\", method=\"lightning\", invoice=\"lnbc100n1pjmpp\"",
            "L402 macaroon=\"abc123\", invoice=\"lnbc100n1pjl402\""
        };

        var result = PaymentChallengeParser.ParseBest(headers);

        // Invoice should come from L402 since it takes precedence
        result.Invoice.Should().Be("lnbc100n1pjl402");
        result.IsMpp.Should().BeFalse();
    }

    [Fact]
    public void ParseBest_CommaSeparatedChallenges_PrefersL402()
    {
        // Single header value with Payment first, L402 second (comma-separated)
        var headers = new[]
        {
            "Payment realm=\"test\", method=\"lightning\", invoice=\"lnbc100n1pjmpp\", amount=\"100\", L402 macaroon=\"abc123\", invoice=\"lnbc100n1pjl402\""
        };

        var result = PaymentChallengeParser.ParseBest(headers);

        result.HasChallenge.Should().BeTrue();
        result.IsMpp.Should().BeFalse();
        result.L402.Should().NotBeNull();
        result.L402!.Invoice.Should().Be("lnbc100n1pjl402");
        result.Mpp.Should().NotBeNull();
        result.Mpp!.Invoice.Should().Be("lnbc100n1pjmpp");
    }

    [Fact]
    public void ParseBest_CommaSeparatedL402First_Works()
    {
        // L402 first, then Payment in same header value
        var headers = new[]
        {
            "L402 macaroon=\"abc123\", invoice=\"lnbc100n1pjl402\", Payment realm=\"test\", method=\"lightning\", invoice=\"lnbc100n1pjmpp\""
        };

        var result = PaymentChallengeParser.ParseBest(headers);

        result.HasChallenge.Should().BeTrue();
        result.IsMpp.Should().BeFalse();
        result.L402.Should().NotBeNull();
        result.L402!.Invoice.Should().Be("lnbc100n1pjl402");
    }

    [Fact]
    public void ParseBest_TabWhitespaceInHeaders_Works()
    {
        var headers = new[]
        {
            "Payment\tmethod=\"lightning\", invoice=\"lnbc100n1pjmpp\""
        };

        var result = PaymentChallengeParser.ParseBest(headers);

        result.HasChallenge.Should().BeTrue();
        result.IsMpp.Should().BeTrue();
        result.Mpp!.Invoice.Should().Be("lnbc100n1pjmpp");
    }
}

using FluentAssertions;
using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Tests.Models;

public class MppClientChallengeTests
{
    [Fact]
    public void Parse_ValidPaymentHeader_ReturnsMppChallenge()
    {
        var header = "Payment realm=\"api.example.com\", method=\"lightning\", invoice=\"lnbc100n1pjtest\", amount=\"100\", currency=\"sat\"";
        var result = MppClientChallenge.Parse(header);

        result.Should().NotBeNull();
        result!.Invoice.Should().Be("lnbc100n1pjtest");
        result.Amount.Should().Be("100");
        result.Realm.Should().Be("api.example.com");
    }

    [Fact]
    public void Parse_NonLightningMethod_ReturnsNull()
    {
        var header = "Payment realm=\"test\", method=\"stripe\", invoice=\"lnbc100n1pjtest\"";
        var result = MppClientChallenge.Parse(header);
        result.Should().BeNull();
    }

    [Fact]
    public void Parse_MissingInvoice_ReturnsNull()
    {
        var header = "Payment realm=\"test\", method=\"lightning\", amount=\"100\"";
        var result = MppClientChallenge.Parse(header);
        result.Should().BeNull();
    }

    [Fact]
    public void Parse_NonPaymentScheme_ReturnsNull()
    {
        var header = "L402 macaroon=\"abc\", invoice=\"lnbc100n1pjtest\"";
        var result = MppClientChallenge.Parse(header);
        result.Should().BeNull();
    }

    [Fact]
    public void Parse_NullOrEmpty_ReturnsNull()
    {
        MppClientChallenge.Parse(null).Should().BeNull();
        MppClientChallenge.Parse("").Should().BeNull();
        MppClientChallenge.Parse("  ").Should().BeNull();
    }

    [Fact]
    public void Parse_CaseInsensitive_Works()
    {
        var header = "payment realm=\"test\", METHOD=\"Lightning\", invoice=\"lnbc100n1pjtest\"";
        var result = MppClientChallenge.Parse(header);
        result.Should().NotBeNull();
        result!.Invoice.Should().Be("lnbc100n1pjtest");
    }

    [Fact]
    public void Parse_MinimalHeader_Works()
    {
        var header = "Payment method=\"lightning\", invoice=\"lnbc100n1pjtest\"";
        var result = MppClientChallenge.Parse(header);
        result.Should().NotBeNull();
        result!.Invoice.Should().Be("lnbc100n1pjtest");
        result.Amount.Should().BeNull();
        result.Realm.Should().BeNull();
    }

    [Fact]
    public void Parse_TabAfterScheme_Works()
    {
        var header = "Payment\tmethod=\"lightning\", invoice=\"lnbc100n1pjtest\"";
        var result = MppClientChallenge.Parse(header);
        result.Should().NotBeNull();
        result!.Invoice.Should().Be("lnbc100n1pjtest");
    }

    [Fact]
    public void Parse_MultipleWhitespaceAfterScheme_Works()
    {
        var header = "Payment   \t  method=\"lightning\", invoice=\"lnbc100n1pjtest\"";
        var result = MppClientChallenge.Parse(header);
        result.Should().NotBeNull();
        result!.Invoice.Should().Be("lnbc100n1pjtest");
    }

    [Fact]
    public void Parse_WhitespaceAroundEquals_Works()
    {
        var header = "Payment method = \"lightning\", invoice = \"lnbc100n1pjtest\", amount = \"100\"";
        var result = MppClientChallenge.Parse(header);
        result.Should().NotBeNull();
        result!.Invoice.Should().Be("lnbc100n1pjtest");
        result.Amount.Should().Be("100");
    }

    [Fact]
    public void Parse_WhitespaceBeforeEquals_Works()
    {
        var header = "Payment method =\"lightning\", invoice =\"lnbc100n1pjtest\"";
        var result = MppClientChallenge.Parse(header);
        result.Should().NotBeNull();
        result!.Invoice.Should().Be("lnbc100n1pjtest");
    }
}

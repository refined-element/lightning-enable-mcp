using LightningEnable.Mcp.Models;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Models;

/// <summary>
/// The preimage is not a receipt or a tracking number — under L402/MPP it IS the
/// proof of payment. These tests pin the two invariants that keep an internal
/// identifier from ever being published in that field.
/// </summary>
public class PreimageValidationTests
{
    private const string ValidPreimage = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";

    [Theory]
    [InlineData(ValidPreimage)]
    [InlineData("0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20")] // hex is case-insensitive
    [InlineData("  0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20  ")] // tolerate whitespace
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public void IsValid_AcceptsRealPreimages(string value)
    {
        Preimage.IsValid(value).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("withdrawal-123")]                              // an OpenNode withdrawal ID
    [InlineData("b5f9e0c2-1234-4a56-8901-abcdef123456")]        // a UUID — the Coinos internal-transfer bug
    [InlineData("lnbc100n1p3abcdef")]                           // a BOLT11 invoice
    [InlineData("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f2")]   // 63 chars
    [InlineData("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f200")] // 65 chars
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]  // right length, not hex
    public void IsValid_RejectsEverythingElse(string? value)
    {
        Preimage.IsValid(value).Should().BeFalse();
    }
}

public class NwcPaymentResultTests
{
    private const string ValidPreimage = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";

    [Fact]
    public void HasPreimage_TrueOnlyForARealPreimage()
    {
        NwcPaymentResult.Succeeded(ValidPreimage).HasPreimage.Should().BeTrue();
    }

    [Theory]
    [InlineData("b5f9e0c2-1234-4a56-8901-abcdef123456")] // Coinos UUID
    [InlineData("withdrawal-123")]
    [InlineData("lnbc100n1p3abcdef")]
    public void HasPreimage_FalseWhenTheValueIsNotAPreimage(string notAPreimage)
    {
        // A wallet may hand us an internal identifier. HasPreimage is the gate that
        // stops it reaching an L402 Authorization header.
        NwcPaymentResult.Succeeded(notAPreimage).HasPreimage.Should().BeFalse();
    }

    [Fact]
    public void SucceededWithoutPreimage_HasNoPreimageButKeepsTracking()
    {
        var result = NwcPaymentResult.SucceededWithoutPreimage("withdrawal-123", "no preimage");

        result.Success.Should().BeTrue();
        result.IsPending.Should().BeFalse();
        result.HasPreimage.Should().BeFalse();
        result.PreimageHex.Should().BeNull();
        result.TrackingId.Should().Be("withdrawal-123");
    }

    [Fact]
    public void Pending_IsNotSuccessAndNotFailure()
    {
        var result = NwcPaymentResult.Pending("withdrawal-456", "still settling");

        // A payment that may still FAIL must never read as terminal success.
        result.Success.Should().BeFalse();
        result.IsPending.Should().BeTrue();
        result.HasPreimage.Should().BeFalse();
        result.TrackingId.Should().Be("withdrawal-456");
        result.ErrorCode.Should().Be("PAYMENT_PENDING");
    }

    [Fact]
    public void Pending_IsDistinguishableFromAHardFailure()
    {
        var pending = NwcPaymentResult.Pending("w-1", "still settling");
        var failed = NwcPaymentResult.Failed("PAYMENT_FAILED", "route not found");

        pending.IsPending.Should().BeTrue();
        failed.IsPending.Should().BeFalse();
        // Both are non-success, but only one may be retried.
        pending.Success.Should().BeFalse();
        failed.Success.Should().BeFalse();
    }
}

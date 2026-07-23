using System.Net;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using FluentAssertions;
using Moq;
using Moq.Protected;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// The LND wallet boundary must validate preimages as 64-char hex.
///
/// LND normally always returns a real preimage — but "normally" is not a guard.
/// This boundary base64-decoded whatever arrived and hex-encoded it, publishing
/// the result as PreimageHex without ever asking whether it was preimage-shaped.
/// </summary>
public class LndWalletServiceTests
{
    private const string ValidPreimageHex =
        "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef";

    private static Mock<HttpMessageHandler> CreateMockHandler(string responseContent)
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseContent)
            });
        return mockHandler;
    }

    /// <summary>
    /// Runs <paramref name="act"/> with LND configured, restoring env vars afterwards.
    /// </summary>
    private static async Task WithConfiguredLnd(
        string preimageBase64,
        Func<NwcPaymentResult, Task> assert)
    {
        var json = $$"""
            {"payment_preimage":"{{preimageBase64}}","payment_error":"","payment_hash":"aGFzaA=="}
            """;
        await WithLndResponse(json, assert);
    }

    /// <summary>
    /// Runs a pay_invoice against a fully custom LND JSON response body, restoring env
    /// vars afterwards. Lets a test drive shapes the preimage-only helper can't
    /// (a populated payment_error, a non-string preimage, etc.).
    /// </summary>
    private static async Task WithLndResponse(
        string responseJson,
        Func<NwcPaymentResult, Task> assert)
    {
        var originalHost = Environment.GetEnvironmentVariable("LND_REST_HOST");
        var originalMacaroon = Environment.GetEnvironmentVariable("LND_MACAROON_HEX");
        try
        {
            Environment.SetEnvironmentVariable("LND_REST_HOST", "localhost:8080");
            Environment.SetEnvironmentVariable("LND_MACAROON_HEX", "abc123");

            using var httpClient = new HttpClient(CreateMockHandler(responseJson).Object);
            using var service = new LndWalletService(httpClient);

            var result = await service.PayInvoiceAsync("lnbc1000n1p3abcdef");
            await assert(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LND_REST_HOST", originalHost);
            Environment.SetEnvironmentVariable("LND_MACAROON_HEX", originalMacaroon);
        }
    }

    public static TheoryData<byte[]> WrongLengthPreimages => new()
    {
        new byte[] { 0xde, 0xad, 0xbe, 0xef },      // 4 bytes -> "deadbeef", not a preimage
        Enumerable.Repeat((byte)0x01, 31).ToArray(), // one byte short
        Enumerable.Repeat((byte)0x01, 33).ToArray(), // one byte long
    };

    [Theory]
    [MemberData(nameof(WrongLengthPreimages))]
    public async Task PayInvoice_WrongLengthPreimage_IsNeverPublishedAsProof(byte[] raw)
    {
        await WithConfiguredLnd(Convert.ToBase64String(raw), result =>
        {
            result.HasPreimage.Should().BeFalse(
                "a value that is not 32 bytes of hex cannot be a preimage");
            result.PreimageHex.Should().BeNull(
                "a non-preimage must never occupy the field L402 treats as proof of payment");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PayInvoice_WrongLengthPreimage_StillReportsSettled()
    {
        // The funds ARE gone. Reporting failure would invite a retry that pays twice.
        await WithConfiguredLnd(Convert.ToBase64String(new byte[] { 0xde, 0xad, 0xbe, 0xef }), result =>
        {
            result.Success.Should().BeTrue("the payment settled — it is simply unprovable");
            result.IsPending.Should().BeFalse();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PayInvoice_ValidPreimage_IsReturned()
    {
        var preimageBytes = Convert.FromHexString(ValidPreimageHex);
        await WithConfiguredLnd(Convert.ToBase64String(preimageBytes), result =>
        {
            result.Success.Should().BeTrue();
            result.HasPreimage.Should().BeTrue();
            result.PreimageHex.Should().Be(ValidPreimageHex);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PayInvoice_SettledButNoPreimage_IsSuccessNotFailure()
    {
        // LND reported no payment_error, so the payment SETTLED — the funds are gone.
        // The old code returned Failed("NO_PREIMAGE", ...) here (Success=false), which the
        // consumer (L402HttpClient) surfaces as "Payment failed" WITHOUT recording the
        // spend — the agent retries and pays twice, and the budget under-counts. A settled
        // payment with no preimage is settled-but-unprovable, NOT a failure: it must be
        // SucceededWithoutPreimage (Success=true, HasPreimage=false), matching the adjacent
        // invalid-format branch and the SucceededWithoutPreimage contract in NwcConfig.cs.
        await WithConfiguredLnd(preimageBase64: "", result =>
        {
            result.Success.Should().BeTrue("the payment settled — it is unprovable, not failed");
            result.HasPreimage.Should().BeFalse();
            result.PreimageHex.Should().BeNull();
            result.IsPending.Should().BeFalse();
            // payment_hash from the LND response is the reconciliation handle ("aGFzaA==").
            result.TrackingId.Should().Be("aGFzaA==");
            return Task.CompletedTask;
        });
    }

    // Non-empty values that are NOT decodable base64: Convert.FromBase64String throws
    // FormatException on each.
    public static TheoryData<string> MalformedBase64Preimages => new()
    {
        "not-valid-base64",  // '-' is outside the base64 alphabet
        "abc",               // length not a multiple of 4 -> invalid padding
        "@@@@",              // '@' is outside the base64 alphabet
    };

    [Theory]
    [MemberData(nameof(MalformedBase64Preimages))]
    public async Task PayInvoice_SettledButMalformedBase64Preimage_IsSuccessNotFailure(string malformedBase64)
    {
        // LND reported no payment_error, so the payment SETTLED — the funds are gone.
        // Convert.FromBase64String on a non-base64 value throws FormatException; left
        // unguarded (LndWalletService.cs:137) it fell through to the generic catch ->
        // Failed("EXCEPTION") -> a RETRYABLE failure -> the agent retries and pays
        // twice. A settled-but-undecodable preimage is settled-but-unprovable, NOT a
        // failure: it must be SucceededWithoutPreimage (Success=true, HasPreimage=false),
        // matching the no-preimage / invalid-hex branches and the Python LND fix.
        await WithConfiguredLnd(malformedBase64, result =>
        {
            result.Success.Should().BeTrue("the payment settled — it is unprovable, not failed");
            result.HasPreimage.Should().BeFalse();
            result.PreimageHex.Should().BeNull();
            result.IsPending.Should().BeFalse();
            // payment_hash from the LND response is the reconciliation handle ("aGFzaA==").
            result.TrackingId.Should().Be("aGFzaA==");
            return Task.CompletedTask;
        });
    }

    // A settled response (payment_error empty) whose payment_preimage is NOT a JSON
    // string. Reading it into the typed string? model threw JsonException at
    // deserialization -> generic catch -> Failed("EXCEPTION") -> retryable -> double-pay.
    public static TheoryData<string> SettledNonStringPreimageResponses => new()
    {
        """{"payment_preimage":12345,"payment_error":"","payment_hash":"aGFzaA=="}""",  // number
        """{"payment_preimage":true,"payment_error":"","payment_hash":"aGFzaA=="}""",   // bool
        """{"payment_preimage":[1,2],"payment_error":"","payment_hash":"aGFzaA=="}""",  // array
    };

    [Theory]
    [MemberData(nameof(SettledNonStringPreimageResponses))]
    public async Task PayInvoice_SettledButNonStringPreimage_IsSuccessNotFailure(string json)
    {
        // LND reported no payment_error, so the payment SETTLED — the funds are gone.
        // A non-string payment_preimage is settled-but-unprovable, NOT a failure. Reading
        // it into the typed string? model threw JsonException before payment_error could
        // even be inspected -> generic catch -> Failed("EXCEPTION") -> a RETRYABLE failure
        // -> double-pay. It must land on SucceededWithoutPreimage (Success=true,
        // HasPreimage=false) with the payment_hash as the reconciliation handle.
        await WithLndResponse(json, result =>
        {
            result.Success.Should().BeTrue("the payment settled — it is unprovable, not failed");
            result.HasPreimage.Should().BeFalse();
            result.PreimageHex.Should().BeNull();
            result.IsPending.Should().BeFalse();
            result.TrackingId.Should().Be("aGFzaA==");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PayInvoice_GenuinePaymentError_StaysRetryableFailure()
    {
        // No over-correction: LND reported a payment_error, so the payment did NOT
        // settle. This must stay a (retryable) failure and never be downgraded to a
        // settled-but-unprovable success.
        var json = """
            {"payment_preimage":"","payment_error":"insufficient_balance","payment_hash":"aGFzaA=="}
            """;
        await WithLndResponse(json, result =>
        {
            result.Success.Should().BeFalse();
            result.IsPending.Should().BeFalse();
            result.ErrorCode.Should().Be("PAYMENT_ERROR");
            return Task.CompletedTask;
        });
    }
}

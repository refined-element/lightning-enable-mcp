using System.Text;
using System.Text.Json;
using FluentAssertions;
using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Tests.Models;

/// <summary>
/// MPP draft-00 (draft-httpauth-payment-00 + draft-lightning-charge-00) client support.
///
/// Modern "Payment" challenges carry a base64url-encoded <c>request</c> param (JCS JSON
/// with the invoice inside) instead of a top-level <c>invoice=</c> param. A server may
/// also send a SUPERSET header carrying BOTH modern params and legacy
/// invoice/amount/currency params — modern wins, and the legacy params are only a
/// fallback when the modern part is malformed.
/// </summary>
public class MppDraft00ChallengeTests
{
    // Deterministic fixtures — no real invoices, no real preimages (fixture values only).
    private const string FixtureInvoice = "lnbc100n1pjtest";
    private const string FixturePaymentHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string FixturePreimage = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FutureExpiry = "2099-01-01T00:00:00Z";

    private static string B64UrlNoPad(string s) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string B64UrlWithPad(string s) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).Replace('+', '-').Replace('/', '_');

    private static string RequestJson(string invoice = FixtureInvoice, string amount = "10") =>
        $"{{\"amount\":\"{amount}\",\"currency\":\"sat\",\"methodDetails\":{{\"invoice\":\"{invoice}\",\"paymentHash\":\"{FixturePaymentHash}\",\"network\":\"mainnet\"}}}}";

    private static string ModernHeader(string requestEncoded, string expires = FutureExpiry) =>
        $"Payment id=\"chal-1\", realm=\"api.example.com\", method=\"lightning\", intent=\"charge\", request=\"{requestEncoded}\", expires=\"{expires}\"";

    // ---------------------------------------------------------------
    // Modern challenge parsing
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_ModernChallenge_HappyPath()
    {
        var encoded = B64UrlNoPad(RequestJson());
        var result = MppClientChallenge.Parse(ModernHeader(encoded));

        result.Should().NotBeNull();
        result!.IsModern.Should().BeTrue();
        result.Id.Should().Be("chal-1");
        result.Realm.Should().Be("api.example.com");
        result.Method.Should().Be("lightning");
        result.Intent.Should().Be("charge");
        result.RequestEncoded.Should().Be(encoded);
        result.Expires.Should().Be(FutureExpiry);
        result.Invoice.Should().Be(FixtureInvoice);
        result.Amount.Should().Be("10");
        result.PaymentHash.Should().Be(FixturePaymentHash);
        result.Network.Should().Be("mainnet");
    }

    [Fact]
    public void Parse_ModernChallenge_OptionalParamsCaptured()
    {
        var encoded = B64UrlNoPad(RequestJson());
        var header = ModernHeader(encoded) +
                     ", digest=\"fixture-digest\", description=\"Premium API call\", opaque=\"fixture-opaque\"";
        var result = MppClientChallenge.Parse(header);

        result.Should().NotBeNull();
        result!.Digest.Should().Be("fixture-digest");
        result.Description.Should().Be("Premium API call");
        result.Opaque.Should().Be("fixture-opaque");
    }

    [Fact]
    public void Parse_ModernChallenge_OptionalParamsAbsent_AreNull()
    {
        var encoded = B64UrlNoPad(RequestJson());
        var result = MppClientChallenge.Parse(ModernHeader(encoded));

        result.Should().NotBeNull();
        result!.Digest.Should().BeNull();
        result.Description.Should().BeNull();
        result.Opaque.Should().BeNull();
    }

    [Fact]
    public void Parse_ModernChallenge_Base64UrlWithPadding_Accepted()
    {
        var encoded = B64UrlWithPad(RequestJson());
        var result = MppClientChallenge.Parse(ModernHeader(encoded));

        result.Should().NotBeNull();
        result!.IsModern.Should().BeTrue();
        result.Invoice.Should().Be(FixtureInvoice);
        // The received string (padding included) is preserved byte-exact for the echo.
        result.RequestEncoded.Should().Be(encoded);
    }

    [Fact]
    public void Parse_SupersetHeader_ModernWins_LegacyParamsIgnored()
    {
        // A superset header: modern params AND legacy invoice/amount/currency in one value.
        var encoded = B64UrlNoPad(RequestJson());
        var header = ModernHeader(encoded) +
                     $", invoice=\"{FixtureInvoice}\", amount=\"10\", currency=\"sat\"";
        var result = MppClientChallenge.Parse(header);

        result.Should().NotBeNull();
        result!.IsModern.Should().BeTrue("a non-empty request param means modern draft-00");
        result.Invoice.Should().Be(FixtureInvoice, "the invoice must come from the decoded request");
    }

    [Fact]
    public void Parse_MalformedModern_BadBase64_NoLegacyInvoice_ReturnsNull()
    {
        var header = ModernHeader("!!!not-base64url!!!");
        MppClientChallenge.Parse(header).Should().BeNull(
            "a malformed modern challenge must not be silently accepted");
    }

    [Fact]
    public void Parse_MalformedModern_BadJson_NoLegacyInvoice_ReturnsNull()
    {
        var header = ModernHeader(B64UrlNoPad("this is not json"));
        MppClientChallenge.Parse(header).Should().BeNull();
    }

    [Fact]
    public void Parse_MalformedModern_MissingInvoice_NoLegacyInvoice_ReturnsNull()
    {
        var header = ModernHeader(B64UrlNoPad("{\"amount\":\"10\",\"currency\":\"sat\",\"methodDetails\":{}}"));
        MppClientChallenge.Parse(header).Should().BeNull();
    }

    [Fact]
    public void Parse_MalformedModern_WithLegacyInvoiceInSameHeader_FallsBackToLegacy()
    {
        // Rule 4: legacy params in the SAME header are an intentional fallback.
        var header = ModernHeader("!!!not-base64url!!!") +
                     $", invoice=\"{FixtureInvoice}\", amount=\"10\", currency=\"sat\"";
        var result = MppClientChallenge.Parse(header);

        result.Should().NotBeNull();
        result!.IsModern.Should().BeFalse("the malformed modern part falls back to the legacy profile");
        result.Invoice.Should().Be(FixtureInvoice);
    }

    [Fact]
    public void Parse_ModernChallenge_WrongIntent_ReturnsNull()
    {
        var encoded = B64UrlNoPad(RequestJson());
        var header = $"Payment id=\"chal-1\", realm=\"r\", method=\"lightning\", intent=\"refund\", request=\"{encoded}\", expires=\"{FutureExpiry}\"";
        MppClientChallenge.Parse(header).Should().BeNull("only intent=charge is payable");
    }

    [Fact]
    public void Parse_ModernChallenge_MissingIntent_ReturnsNull()
    {
        var encoded = B64UrlNoPad(RequestJson());
        var header = $"Payment id=\"chal-1\", realm=\"r\", method=\"lightning\", request=\"{encoded}\", expires=\"{FutureExpiry}\"";
        MppClientChallenge.Parse(header).Should().BeNull("an unknown intent must not be paid");
    }

    [Fact]
    public void Parse_ModernChallenge_WrongCurrency_ReturnsNull()
    {
        var json = $"{{\"amount\":\"10\",\"currency\":\"usd\",\"methodDetails\":{{\"invoice\":\"{FixtureInvoice}\"}}}}";
        MppClientChallenge.Parse(ModernHeader(B64UrlNoPad(json))).Should().BeNull();
    }

    [Fact]
    public void Parse_ModernChallenge_NoCurrency_Accepted()
    {
        var json = $"{{\"amount\":\"10\",\"methodDetails\":{{\"invoice\":\"{FixtureInvoice}\"}}}}";
        var result = MppClientChallenge.Parse(ModernHeader(B64UrlNoPad(json)));
        result.Should().NotBeNull();
        result!.IsModern.Should().BeTrue();
    }

    [Fact]
    public void Parse_ParamValueEndingInAnotherParamName_DoesNotPoisonExtraction()
    {
        // A quoted value that ENDS with "id=" (e.g. a free-text description) must not be
        // mistaken for the id param: a poisoned id would corrupt the byte-exact credential
        // echo and get the credential rejected AFTER the invoice was paid.
        var encoded = B64UrlNoPad(RequestJson());
        var header = $"Payment description=\"client-id=\", id=\"chal-1\", realm=\"api.example.com\", " +
                     $"method=\"lightning\", intent=\"charge\", request=\"{encoded}\", expires=\"{FutureExpiry}\"";
        var result = MppClientChallenge.Parse(header);

        result.Should().NotBeNull();
        result!.Id.Should().Be("chal-1");
        result.Description.Should().Be("client-id=");
    }

    [Fact]
    public void Parse_LegacyChallenge_Unchanged()
    {
        var header = $"Payment realm=\"api.example.com\", method=\"lightning\", invoice=\"{FixtureInvoice}\", amount=\"100\", currency=\"sat\"";
        var result = MppClientChallenge.Parse(header);

        result.Should().NotBeNull();
        result!.IsModern.Should().BeFalse();
        result.Invoice.Should().Be(FixtureInvoice);
        result.Amount.Should().Be("100");
    }

    // ---------------------------------------------------------------
    // Expiry
    // ---------------------------------------------------------------

    [Fact]
    public void IsExpired_FutureExpiry_False()
    {
        var result = MppClientChallenge.Parse(ModernHeader(B64UrlNoPad(RequestJson())));
        result!.IsExpired().Should().BeFalse();
    }

    [Fact]
    public void IsExpired_PastExpiry_True()
    {
        var result = MppClientChallenge.Parse(ModernHeader(B64UrlNoPad(RequestJson()), expires: "2001-01-01T00:00:00Z"));
        result.Should().NotBeNull("an expired challenge still parses — the flow refuses it before paying");
        result!.IsExpired().Should().BeTrue();
    }

    [Fact]
    public void IsExpired_NoExpires_False()
    {
        var encoded = B64UrlNoPad(RequestJson());
        var header = $"Payment id=\"chal-1\", realm=\"r\", method=\"lightning\", intent=\"charge\", request=\"{encoded}\"";
        var result = MppClientChallenge.Parse(header);
        result!.IsExpired().Should().BeFalse();
    }

    [Fact]
    public void IsExpired_UnparseableExpires_FailsClosed()
    {
        var result = MppClientChallenge.Parse(ModernHeader(B64UrlNoPad(RequestJson()), expires: "not-a-date"));
        result!.IsExpired().Should().BeTrue("an unparseable expiry must fail closed in the money path");
    }

    // ---------------------------------------------------------------
    // Credential building
    // ---------------------------------------------------------------

    [Fact]
    public void BuildModernCredential_EchoesChallengeByteExact()
    {
        var encoded = B64UrlNoPad(RequestJson());
        var challenge = MppClientChallenge.Parse(ModernHeader(encoded))!;

        var credential = challenge.BuildModernCredential(FixturePreimage);

        credential.Should().NotContain("=", "base64url output must have no padding");
        var decoded = DecodeCredential(credential);
        var echo = decoded.GetProperty("challenge");
        echo.GetProperty("id").GetString().Should().Be("chal-1");
        echo.GetProperty("realm").GetString().Should().Be("api.example.com");
        echo.GetProperty("method").GetString().Should().Be("lightning");
        echo.GetProperty("intent").GetString().Should().Be("charge");
        echo.GetProperty("request").GetString().Should().Be(encoded, "the encoded request string must be echoed byte-exact");
        echo.GetProperty("expires").GetString().Should().Be(FutureExpiry);
        decoded.GetProperty("payload").GetProperty("preimage").GetString().Should().Be(FixturePreimage);
    }

    [Fact]
    public void BuildModernCredential_PaddedRequest_EchoedWithPadding()
    {
        var encoded = B64UrlWithPad(RequestJson());
        var challenge = MppClientChallenge.Parse(ModernHeader(encoded))!;

        var decoded = DecodeCredential(challenge.BuildModernCredential(FixturePreimage));
        decoded.GetProperty("challenge").GetProperty("request").GetString()
            .Should().Be(encoded, "echo as received — never decode/re-encode");
    }

    [Fact]
    public void BuildModernCredential_UppercasePreimage_Lowercased()
    {
        var challenge = MppClientChallenge.Parse(ModernHeader(B64UrlNoPad(RequestJson())))!;

        var decoded = DecodeCredential(challenge.BuildModernCredential(FixturePreimage.ToUpperInvariant()));
        decoded.GetProperty("payload").GetProperty("preimage").GetString().Should().Be(FixturePreimage);
    }

    [Fact]
    public void BuildModernCredential_OptionalParams_EchoedWhenPresent()
    {
        var encoded = B64UrlNoPad(RequestJson());
        var header = ModernHeader(encoded) +
                     ", digest=\"fixture-digest\", description=\"desc\", opaque=\"fixture-opaque\"";
        var challenge = MppClientChallenge.Parse(header)!;

        var echo = DecodeCredential(challenge.BuildModernCredential(FixturePreimage)).GetProperty("challenge");
        echo.GetProperty("digest").GetString().Should().Be("fixture-digest");
        echo.GetProperty("description").GetString().Should().Be("desc");
        echo.GetProperty("opaque").GetString().Should().Be("fixture-opaque");
    }

    [Fact]
    public void BuildModernCredential_AbsentOptionalParams_Omitted()
    {
        var challenge = MppClientChallenge.Parse(ModernHeader(B64UrlNoPad(RequestJson())))!;

        var echo = DecodeCredential(challenge.BuildModernCredential(FixturePreimage)).GetProperty("challenge");
        echo.TryGetProperty("digest", out _).Should().BeFalse();
        echo.TryGetProperty("description", out _).Should().BeFalse();
        echo.TryGetProperty("opaque", out _).Should().BeFalse();
    }

    [Fact]
    public void BuildModernCredential_LegacySupersetExtras_NotEchoed()
    {
        var encoded = B64UrlNoPad(RequestJson());
        var header = ModernHeader(encoded) +
                     $", invoice=\"{FixtureInvoice}\", amount=\"10\", currency=\"sat\"";
        var challenge = MppClientChallenge.Parse(header)!;

        var echo = DecodeCredential(challenge.BuildModernCredential(FixturePreimage)).GetProperty("challenge");
        echo.TryGetProperty("invoice", out _).Should().BeFalse("legacy extras are unknown params — never echoed");
        echo.TryGetProperty("amount", out _).Should().BeFalse();
        echo.TryGetProperty("currency", out _).Should().BeFalse();
    }

    private static JsonElement DecodeCredential(string credential)
    {
        var padded = credential.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    // ---------------------------------------------------------------
    // Precedence (ParseBest)
    // ---------------------------------------------------------------

    [Fact]
    public void ParseBest_ModernPreferredOverLegacy_SeparateHeaders()
    {
        var modern = ModernHeader(B64UrlNoPad(RequestJson()));
        var headers = new[]
        {
            $"Payment realm=\"legacy\", method=\"lightning\", invoice=\"lnbc200n1pjlegacy\", amount=\"20\", currency=\"sat\"",
            modern
        };

        var result = PaymentChallengeParser.ParseBest(headers);

        result.IsMpp.Should().BeTrue();
        result.Mpp!.IsModern.Should().BeTrue("modern draft-00 is preferred over the legacy profile");
        result.Mpp.Invoice.Should().Be(FixtureInvoice);
    }

    [Fact]
    public void ParseBest_ModernPreferredOverLegacy_ModernFirst()
    {
        var modern = ModernHeader(B64UrlNoPad(RequestJson()));
        var headers = new[]
        {
            modern,
            $"Payment realm=\"legacy\", method=\"lightning\", invoice=\"lnbc200n1pjlegacy\""
        };

        var result = PaymentChallengeParser.ParseBest(headers);

        result.Mpp!.IsModern.Should().BeTrue();
        result.Mpp.Invoice.Should().Be(FixtureInvoice);
    }

    [Fact]
    public void ParseBest_L402StillPreferredOverModernPayment()
    {
        // The existing L402-vs-Payment order must NOT change — only modern-vs-legacy
        // preference WITHIN the Payment scheme is new.
        var headers = new[]
        {
            ModernHeader(B64UrlNoPad(RequestJson())),
            "L402 macaroon=\"YWJjZGVm\", invoice=\"lnbc100n1pjl402\""
        };

        var result = PaymentChallengeParser.ParseBest(headers);

        result.IsMpp.Should().BeFalse();
        result.L402.Should().NotBeNull();
        result.Invoice.Should().Be("lnbc100n1pjl402");
        // The modern Payment challenge is still parsed alongside.
        result.Mpp.Should().NotBeNull();
        result.Mpp!.IsModern.Should().BeTrue();
    }

    [Fact]
    public void ParseBest_MultiChallengeSingleHeader_L402PlusModernPayment()
    {
        var combined = "L402 macaroon=\"YWJjZGVm\", invoice=\"lnbc100n1pjl402\", " +
                       ModernHeader(B64UrlNoPad(RequestJson()));

        var result = PaymentChallengeParser.ParseBest(new[] { combined });

        result.L402.Should().NotBeNull();
        result.L402!.Invoice.Should().Be("lnbc100n1pjl402");
        result.Mpp.Should().NotBeNull();
        result.Mpp!.IsModern.Should().BeTrue();
        result.Mpp.Invoice.Should().Be(FixtureInvoice);
    }

    [Fact]
    public void ParseBest_LegacyOnly_StillWorks()
    {
        var headers = new[]
        {
            $"Payment realm=\"legacy\", method=\"lightning\", invoice=\"{FixtureInvoice}\", amount=\"10\", currency=\"sat\""
        };

        var result = PaymentChallengeParser.ParseBest(headers);

        result.IsMpp.Should().BeTrue();
        result.Mpp!.IsModern.Should().BeFalse();
        result.Mpp.Invoice.Should().Be(FixtureInvoice);
    }
}

/// <summary>
/// Payment-Receipt response header parsing (draft-00). Tolerant by design: a missing or
/// malformed receipt must never fail a successful payment.
/// </summary>
public class MppPaymentReceiptTests
{
    private static string B64UrlNoPad(string s) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void Parse_ValidReceipt_ReturnsFields()
    {
        var json = "{\"challengeId\":\"chal-1\",\"method\":\"lightning\",\"reference\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"status\":\"settled\",\"timestamp\":\"2026-08-23T12:00:00Z\"}";
        var receipt = MppPaymentReceipt.Parse(B64UrlNoPad(json));

        receipt.Should().NotBeNull();
        receipt!.ChallengeId.Should().Be("chal-1");
        receipt.Method.Should().Be("lightning");
        receipt.Reference.Should().Be("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        receipt.Status.Should().Be("settled");
        receipt.Timestamp.Should().Be("2026-08-23T12:00:00Z");
    }

    [Fact]
    public void Parse_WithPadding_Accepted()
    {
        var json = "{\"challengeId\":\"chal-1\",\"status\":\"settled\"}";
        var padded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).Replace('+', '-').Replace('/', '_');
        var receipt = MppPaymentReceipt.Parse(padded);
        receipt.Should().NotBeNull();
        receipt!.ChallengeId.Should().Be("chal-1");
    }

    [Fact]
    public void Parse_PartialFields_Tolerated()
    {
        var receipt = MppPaymentReceipt.Parse(B64UrlNoPad("{\"status\":\"settled\"}"));
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be("settled");
        receipt.ChallengeId.Should().BeNull();
    }

    [Fact]
    public void Parse_NullOrEmpty_ReturnsNull()
    {
        MppPaymentReceipt.Parse(null).Should().BeNull();
        MppPaymentReceipt.Parse("").Should().BeNull();
        MppPaymentReceipt.Parse("   ").Should().BeNull();
    }

    [Fact]
    public void Parse_BadBase64_ReturnsNull()
    {
        MppPaymentReceipt.Parse("!!!not-base64!!!").Should().BeNull();
    }

    [Fact]
    public void Parse_BadJson_ReturnsNull()
    {
        MppPaymentReceipt.Parse(B64UrlNoPad("not json")).Should().BeNull();
    }
}

using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tests.Services;
using LightningEnable.Mcp.Tools;
using Xunit;

namespace LightningEnable.Mcp.Tests.Tools;

/// <summary>
/// agent_services review / reviews / reviewer_key, end to end against an in-process fake relay.
/// Mirrors Python's <c>tests/test_provider_review.py</c>.
/// </summary>
public sealed class ProviderReviewToolTests : IDisposable
{
    private const string Url = "https://API.Example.com/v1/forecast?city=Paris";
    private const string Normalized = "https://api.example.com/v1/forecast";
    private static readonly string Ph = string.Concat(Enumerable.Repeat("ab", 32));
    private static readonly string H11 = new('1', 64);
    private static readonly string H22 = new('2', 64);
    private static readonly byte[] Nonce = Convert.FromHexString(string.Concat(Enumerable.Repeat("0f", 32))); // fixed test nonce
    private static readonly string ReqDigest = ExchangeDigest.RequestDigest(Nonce, "GET", Url, null);
    private static readonly string RespDigest = ExchangeDigest.ResponseDigest(Nonce, Encoding.UTF8.GetBytes("{}"));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "le-review-" + Guid.NewGuid().ToString("N"));
    private readonly AttestationKeyStore _keys;
    private readonly ReceiptService _receipts;
    private readonly NostrRelayClient _client = new(_ => null);

    public ProviderReviewToolTests()
    {
        _keys = new AttestationKeyStore(Path.Combine(_dir, "cfg"), _ => null);
        Directory.CreateDirectory(_dir);
        _receipts = new ReceiptService(Path.Combine(_dir, "receipts.jsonl"));
        _receipts.LogPayment(new PaymentReceiptEntry { Kind = "l402", AmountSats = 10, Status = "settled", PaymentHash = Ph, Wallet = "NWC" });
        _receipts.LogExchangeDigest(Ph, "GET", Url, null, Encoding.UTF8.GetBytes("{}"), Nonce);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Secret => File.ReadAllText(_keys.KeyPath).Trim();

    private Task<string> Review(string relay, AttestationKeyStore? keys = null, string? subjectPubkey = null, string? subjectUrl = Url,
        string? paymentHash = null, bool allowUnknownPayment = false, IReceiptService? receipts = null,
        bool? responded = null, bool? onTime = null, int? quality = null, string? content = null) =>
        ProviderReviewTool.PublishReview(subjectPubkey, subjectPubkey != null ? null : subjectUrl, false,
            paymentHash, allowUnknownPayment, Checks(responded, onTime), quality, null, content,
            keys ?? _keys, receipts ?? _receipts, _client, CancellationToken.None, relay);

    private static Dictionary<string, bool>? Checks(bool? responded, bool? onTime)
    {
        var d = new Dictionary<string, bool>();
        if (responded is { } r) d["responded"] = r;
        if (onTime is { } t) d["on_time"] = t;
        return d.Count > 0 ? d : null;
    }

    [Fact]
    public async Task Review_UnknownCheck_IsInvalid()
    {
        var o = JsonNode.Parse(await ProviderReviewTool.PublishReview(null, Url, false, null, false,
            new Dictionary<string, bool> { ["fast"] = true }, null, null, null, _keys, _receipts, _client,
            CancellationToken.None, "ws://127.0.0.1:9/"))!;
        o["errorType"]!.GetValue<string>().Should().Be("invalid_input");
        o["error"]!.GetValue<string>().Should().Contain("fast");
    }

    [Fact]
    public async Task Review_SignsWithReviewerKey_AndAttachesLocalReceiptHashes()
    {
        await using var relay = new FakeNostrRelay("accept").Start();
        var o = JsonNode.Parse(await Review(relay.Url, paymentHash: Ph, responded: true, onTime: false, quality: 4,
            content: "slow but right"))!.AsObject();

        o["success"]!.GetValue<bool>().Should().BeTrue(o.ToJsonString());
        var ev = relay.Stored.Single();
        NostrEvents.Verify(ev).Should().BeTrue();
        ev["pubkey"]!.GetValue<string>().Should().Be(_keys.GetIdentity().PubkeyHex);
        o["reviewer"]!["pubkey"]!.GetValue<string>().Should().Be(_keys.GetIdentity().PubkeyHex);
        var tags = ev["tags"]!.ToJsonString();
        tags.Should().Contain($"[\"u\",\"{Normalized}\"]");
        tags.Should().Contain($"[\"proof\",\"{Ph}\"]");
        tags.Should().Contain($"[\"receipt\",\"{Ph}\",\"{ReqDigest}\",\"{RespDigest}\"]");
        tags.Should().Contain("[\"mech\",\"responded\",\"1\"]").And.Contain("[\"mech\",\"on_time\",\"0\"]");
        tags.Should().Contain("[\"judged\",\"quality\",\"4\"]");
        o["proofBacked"]!.GetValue<bool>().Should().BeTrue();
        o["receipt"]!["knownToLocalReceiptLog"]!.GetValue<bool>().Should().BeTrue();
        o.ToJsonString().Should().NotContain(Secret);
    }

    [Fact]
    public async Task Review_UnknownPaymentHash_IsRefusedByDefault()
    {
        await using var relay = new FakeNostrRelay("accept").Start();
        var o = JsonNode.Parse(await Review(relay.Url, subjectPubkey: new string('c', 64), paymentHash: new string('e', 64), quality: 5))!;
        o["success"]!.GetValue<bool>().Should().BeFalse();
        o["errorType"]!.GetValue<string>().Should().Be("unknown_payment");
        o["error"]!.GetValue<string>().Should().Contain("proof").And.Contain("allow_unknown_payment");
        relay.Stored.Should().BeEmpty();
    }

    [Fact]
    public async Task Review_UnknownPaymentHashOverride_PublishesReceiptWithoutProof()
    {
        await using var relay = new FakeNostrRelay("accept").Start();
        var o = JsonNode.Parse(await Review(relay.Url, subjectPubkey: new string('c', 64), paymentHash: new string('e', 64),
            allowUnknownPayment: true, quality: 5))!.AsObject();
        o["success"]!.GetValue<bool>().Should().BeTrue();
        o["proofBacked"]!.GetValue<bool>().Should().BeFalse();
        o["receipt"]!.ToJsonString().Should().Be(
            $"{{\"paymentHash\":\"{new string('e', 64)}\",\"requestDigest\":null,\"responseDigest\":null,\"knownToLocalReceiptLog\":false}}");
        var tags = relay.Stored.Single()["tags"]!.ToJsonString();
        tags.Should().NotContain("\"proof\"");
        tags.Should().Contain($"[\"receipt\",\"{new string('e', 64)}\",\"\",\"\"]");
    }

    [Fact]
    public async Task Review_KnownPaymentWithUnsaltedLegacyRecord_PublishesNoDigests()
    {
        var path = Path.Combine(_dir, "legacy.jsonl");
        var svc = new ReceiptService(path);
        svc.LogPayment(new PaymentReceiptEntry { Kind = "l402", AmountSats = 10, PaymentHash = Ph, Wallet = "NWC" });
        File.AppendAllText(path, $"{{\"type\":\"exchange_digest\",\"paymentHash\":\"{Ph}\",\"requestSha256\":\"{H11}\",\"responseSha256\":\"{H22}\"}}\n");
        await using var relay = new FakeNostrRelay("accept").Start();
        var o = JsonNode.Parse(await Review(relay.Url, paymentHash: Ph, receipts: svc, quality: 4))!;
        o["success"]!.GetValue<bool>().Should().BeTrue();
        o["proofBacked"]!.GetValue<bool>().Should().BeTrue();
        var tags = relay.Stored.Single()["tags"]!.ToJsonString();
        tags.Should().Contain($"[\"receipt\",\"{Ph}\",\"\",\"\"]");
        tags.Should().NotContain(H11);
    }
    [Theory]
    [InlineData(null, null)]          // nothing to attest
    [InlineData(9, null)]             // quality out of range
    [InlineData(3, "zz")]             // bad payment hash
    public async Task Review_InputErrors(int? quality, string? paymentHash)
    {
        var o = JsonNode.Parse(await Review("ws://127.0.0.1:9/", quality: quality, paymentHash: paymentHash))!;
        o["success"]!.GetValue<bool>().Should().BeFalse();
        o["errorType"]!.GetValue<string>().Should().Be("invalid_input");
    }

    [Fact]
    public async Task Review_ContentTooLong_IsInvalid()
    {
        var o = JsonNode.Parse(await Review("ws://127.0.0.1:9/", quality: 3, content: new string('x', 1001)))!;
        o["errorType"]!.GetValue<string>().Should().Be("invalid_input");
    }

    [Fact]
    public async Task Review_RelayRejectionAndAuth_AreReported()
    {
        JsonNode rejected, auth;
        await using (var relay = new FakeNostrRelay("reject").Start())
            rejected = JsonNode.Parse(await Review(relay.Url, quality: 3))!;
        await using (var relay = new FakeNostrRelay("auth").Start())
            auth = JsonNode.Parse(await Review(relay.Url, quality: 3))!;

        rejected["success"]!.GetValue<bool>().Should().BeFalse();
        rejected["errorType"]!.GetValue<string>().Should().Be("rejected");
        rejected["error"]!.GetValue<string>().Should().Contain("blocked");
        auth["errorType"]!.GetValue<string>().Should().Be("auth_required");
        auth["error"]!.GetValue<string>().Should().Contain("NIP-42");
    }

    [Fact]
    public async Task Reviews_RoundTrip_VerifiesAndSeparatesFamilies()
    {
        var other = new AttestationKeyStore(Path.Combine(_dir, "other"), _ => null);
        await using var relay = new FakeNostrRelay("accept").Start();
        await Review(relay.Url, paymentHash: Ph, responded: true, quality: 5);
        await Review(relay.Url, keys: other, responded: false, quality: 1);
        var forged = relay.Stored[0].DeepClone().AsObject();
        forged["content"] = "edited";
        relay.ExtraEvents.Add(forged);

        var o = JsonNode.Parse(await ProviderReviewTool.ReadReviews(null, Url, false, null, _client, CancellationToken.None, relay.Url))!;
        o["success"]!.GetValue<bool>().Should().BeTrue();
        o["complete"]!.GetValue<bool>().Should().BeTrue();
        o["subject"]!.ToJsonString().Should().Be($"{{\"type\":\"url\",\"value\":\"{Normalized}\"}}");
        o["rejected"]!["invalidSignature"]!.GetValue<int>().Should().Be(1);
        o["proofBacked"]!["reviews"]!.GetValue<int>().Should().Be(1);
        o["unbacked"]!["reviews"]!.GetValue<int>().Should().Be(1);
        o["proofBacked"]!["judgedQuality"]!["mean"]!.GetValue<double>().Should().Be(5);
        o["unbacked"]!["mechanical"]!["responded"]!["passRate"]!.GetValue<double>().Should().Be(0);
        o["notes"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain(Attestations.PaymentHashIsAReference);
        o.AsObject().ContainsKey("averageRating").Should().BeFalse();
    }

    [Fact]
    public async Task Reviews_AuthRequired_IsTyped()
    {
        await using var relay = new FakeNostrRelay("auth").Start();
        var o = JsonNode.Parse(await ProviderReviewTool.ReadReviews(new string('c', 64), null, false, null, _client, CancellationToken.None, relay.Url))!;
        o["success"]!.GetValue<bool>().Should().BeFalse();
        o["errorType"]!.GetValue<string>().Should().Be("auth_required");
    }

    [Fact]
    public void ReviewerKey_ReturnsOnlyPublicMaterial()
    {
        var o = JsonNode.Parse(ProviderReviewTool.ReviewerKey(_keys))!;
        o["success"]!.GetValue<bool>().Should().BeTrue();
        o["pubkey"]!.GetValue<string>().Should().Be(_keys.GetIdentity().PubkeyHex);
        o["npub"]!.GetValue<string>().Should().StartWith("npub1");
        o["note"]!.GetValue<string>().Should().Contain("new reviewer identity");
        o.ToJsonString().Should().NotContain(Secret);
    }

    [Fact]
    public async Task NullServices_GiveDescriptiveErrors()
    {
        JsonNode.Parse(ProviderReviewTool.ReviewerKey(null))!["errorType"]!.GetValue<string>().Should().Be("key_unavailable");
        var o = JsonNode.Parse(await ProviderReviewTool.PublishReview(null, Url, false, null, false,
            new Dictionary<string, bool> { ["responded"] = true }, null, null, null, null, null, null, CancellationToken.None))!;
        o["errorType"]!.GetValue<string>().Should().Be("key_unavailable");
    }
}

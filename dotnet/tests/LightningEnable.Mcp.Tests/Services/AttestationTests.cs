using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LightningEnable.Mcp.Services;
using NBitcoin.Secp256k1;
using Xunit;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// Client-signed provider attestations: event bytes, URL rule, verification, aggregation.
/// The golden vector in <c>shared/attestation-golden-vector.json</c> is checked by BOTH ports
/// (Python: <c>tests/test_attestation.py</c>): same inputs → same event bytes, same id and,
/// with the fixed auxiliary randomness, the same BIP-340 signature.
/// </summary>
public class AttestationTests
{
    private static readonly JsonObject Vector = LoadVector();

    // BIP-340 test scalars 3, 1 and 2. Test-only, public knowledge — never real keys.
    private static readonly ECPrivKey Sk = Key(Vector["secretKeyHex"]!.GetValue<string>());
    private static readonly ECPrivKey SkOther = Key("0000000000000000000000000000000000000000000000000000000000000001");
    private static readonly ECPrivKey SkThird = Key("0000000000000000000000000000000000000000000000000000000000000002");
    private static readonly byte[] Aux = Convert.FromHexString(Vector["auxRandHex"]!.GetValue<string>());

    private static ECPrivKey Key(string hex)
    {
        NostrEvents.TryParseSecretKey(hex, out var k).Should().BeTrue();
        return k!;
    }

    internal static JsonObject LoadVector()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "shared", "attestation-golden-vector.json");
            if (File.Exists(candidate))
                return JsonNode.Parse(File.ReadAllText(candidate))!.AsObject();
            dir = dir.Parent;
        }
        throw new FileNotFoundException("shared/attestation-golden-vector.json not found above the test output directory.");
    }

    private static JsonObject BuildFromInput(JsonObject inp, long createdAt)
    {
        var subject = Attestations.ResolveSubject(
            inp["subjectPubkey"]?.GetValue<string>(),
            inp["subjectUrl"]?.GetValue<string>(),
            inp["includeQuery"]?.GetValue<bool>() ?? false);
        var receipt = Attestations.MakeReceipt(
            inp["paymentHash"]?.GetValue<string>(),
            inp["requestSha256"]?.GetValue<string>(),
            inp["responseSha256"]?.GetValue<string>());
        var mech = inp["mechanical"]?.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<bool>());
        var tags = Attestations.BuildTags(subject, receipt, mech,
            inp["quality"]?.GetValue<int>(), inp["agreementId"]?.GetValue<string>());
        return Attestations.Sign(Sk, tags, inp["content"]?.GetValue<string>() ?? "", createdAt, Aux);
    }

    public static IEnumerable<object[]> EventCases() =>
        LoadVector()["events"]!.AsArray().Select(c => new object[] { c!["name"]!.GetValue<string>() });

    public static IEnumerable<object[]> UrlCases() =>
        LoadVector()["urlNormalization"]!.AsArray().Select((c, i) => new object[] { i });

    private static JsonObject EventCase(string name) =>
        Vector["events"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == name)!.AsObject();

    // ── golden vector (parity with Python) ─────────────────────────────────

    [Fact]
    public void Vector_KeyDerivation_MatchesPubkeyAndNpub()
    {
        NostrEvents.XOnlyPubkeyHex(Sk).Should().Be(Vector["pubkeyHex"]!.GetValue<string>());
        NostrEvents.NpubEncode(Vector["pubkeyHex"]!.GetValue<string>()).Should().Be(Vector["npub"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(EventCases))]
    public void GoldenVector_SameTagsBytesIdAndSignature(string name)
    {
        var c = EventCase(name);
        var exp = c["expected"]!.AsObject();
        var ev = BuildFromInput(c["input"]!.AsObject(), c["createdAt"]!.GetValue<long>());

        ev["tags"]!.ToJsonString().Should().Be(exp["tags"]!.ToJsonString());
        var serialized = NostrEvents.Serialize(
            ev["pubkey"]!.GetValue<string>(), ev["created_at"]!.GetValue<long>(),
            ev["kind"]!.GetValue<int>(), ev["tags"]!.AsArray(), ev["content"]!.GetValue<string>());
        serialized.Should().Be(exp["serialized"]!.GetValue<string>());
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serialized))).ToLowerInvariant()
            .Should().Be(exp["serializedUtf8Sha256"]!.GetValue<string>());
        ev["id"]!.GetValue<string>().Should().Be(exp["id"]!.GetValue<string>());
        // Deterministic with the fixed aux randomness: the Python port produces this too.
        ev["sig"]!.GetValue<string>().Should().Be(exp["sig"]!.GetValue<string>());
        NostrEvents.Verify(ev).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(EventCases))]
    public void GoldenVector_VerifiesAsPublished(string name)
    {
        // The exact event the Python port signs must verify here, independent of building it.
        var c = EventCase(name);
        var exp = c["expected"]!.AsObject();
        var ev = new JsonObject
        {
            ["id"] = exp["id"]!.GetValue<string>(),
            ["pubkey"] = Vector["pubkeyHex"]!.GetValue<string>(),
            ["created_at"] = c["createdAt"]!.GetValue<long>(),
            ["kind"] = 38403,
            ["tags"] = exp["tags"]!.DeepClone(),
            ["content"] = c["input"]!["content"]!.GetValue<string>(),
            ["sig"] = exp["sig"]!.GetValue<string>(),
        };
        NostrEvents.Verify(ev).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(EventCases))]
    public void FreshAuxSignature_SameId_VerifiesButDiffers(string name)
    {
        var c = EventCase(name);
        var exp = c["expected"]!.AsObject();
        var ev = Attestations.Sign(Sk, exp["tags"]!.AsArray(), c["input"]!["content"]!.GetValue<string>(),
            c["createdAt"]!.GetValue<long>());
        ev["id"]!.GetValue<string>().Should().Be(exp["id"]!.GetValue<string>());
        NostrEvents.Verify(ev).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(UrlCases))]
    public void UrlNormalization_FollowsSharedRule(int index)
    {
        var c = Vector["urlNormalization"]!.AsArray()[index]!.AsObject();
        var input = c["input"]!.GetValue<string>();
        var includeQuery = c["includeQuery"]!.GetValue<bool>();
        var expected = c["expected"]?.GetValue<string>();
        if (expected == null)
        {
            var act = () => Attestations.NormalizeEndpointUrl(input, includeQuery);
            act.Should().Throw<AttestationException>();
        }
        else
        {
            Attestations.NormalizeEndpointUrl(input, includeQuery).Should().Be(expected);
        }
    }

    [Fact]
    public void CanonicalSerializer_MatchesPreviousEncoderOnAsciiNwcTraffic()
    {
        // NWC events are ASCII (hex tags, base64 ciphertext); the shared serializer must give
        // the bytes the old UnsafeRelaxedJsonEscaping path gave, so NWC ids are unchanged.
        var tags = new JsonArray { new JsonArray { "p", new string('a', 64) }, new JsonArray { "encryption", "nip44_v2" } };
        const string content = "AbC+/=?iv=xyz+/==";
        var relaxed = new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var old = new JsonArray { 0, new string('b', 64), 1700000000L, 23194, JsonNode.Parse(tags.ToJsonString(relaxed)), content }
            .ToJsonString(relaxed);
        NostrEvents.Serialize(new string('b', 64), 1700000000L, 23194, tags, content).Should().Be(old);
    }

    // ── construction rules ─────────────────────────────────────────────────

    [Fact]
    public void ReceiptTag_HasFourElements_AndNoPreimage()
    {
        var subject = Attestations.ResolveSubject(null, "https://api.example.com/x");
        var tags = Attestations.BuildTags(subject, Attestations.MakeReceipt(string.Concat(Enumerable.Repeat("AB", 32)), null, new string('c', 64)),
            new Dictionary<string, bool> { ["responded"] = true });
        var receipt = tags.Single(t => t![0]!.GetValue<string>() == "receipt")!.AsArray();
        receipt.Select(n => n!.GetValue<string>()).Should().Equal("receipt", string.Concat(Enumerable.Repeat("ab", 32)), "", new string('c', 64));
        tags[0]!.AsArray()[1]!.GetValue<string>().Should().Be("receipt:" + string.Concat(Enumerable.Repeat("ab", 32)));
        // The NIP proof tag carries the payment hash, never a preimage.
        tags.ToJsonString().Should().Contain("[\"proof\",\"" + string.Concat(Enumerable.Repeat("ab", 32)) + "\"]");
        tags.Select(t => t![0]!.GetValue<string>()).Should().NotContain("preimage");
    }

    [Fact]
    public void RequestHashWithoutPaymentHash_Rejected()
    {
        var act = () => Attestations.MakeReceipt(null, new string('2', 64));
        act.Should().Throw<AttestationException>();
    }

    [Theory]
    [InlineData("zz")]
    [InlineData("1111111111111111111111111111111111111111111111111111111111111")]
    public void BadPaymentHash_Rejected(string hash)
    {
        var act = () => Attestations.MakeReceipt(hash);
        act.Should().Throw<AttestationException>();
    }

    [Fact]
    public void MechanicalAndJudged_AreSeparateTags_InCanonicalOrder()
    {
        var pk = new string('a', 64);
        var tags = Attestations.BuildTags(Attestations.ResolveSubject(pk, null), null,
            new Dictionary<string, bool> { ["on_time"] = false, ["responded"] = true }, 2);
        tags.ToJsonString().Should().Be(
            $"[[\"d\",\"p:{pk}\"],[\"v\",\"2\"],[\"L\",\"nostr.agent.attestation\"],[\"l\",\"completed\",\"nostr.agent.attestation\"],"
            + $"[\"p\",\"{pk}\",\"\",\"subject\"],[\"mech\",\"responded\",\"1\"],[\"mech\",\"on_time\",\"0\"],[\"judged\",\"quality\",\"2\"],[\"rating\",\"2\"]]");
    }

    [Fact]
    public void Receipt_AlsoEmitsTheNipProofTag()
    {
        var ab = string.Concat(Enumerable.Repeat("ab", 32));
        var tags = Attestations.BuildTags(Attestations.ResolveSubject(null, "https://api.example.com/x"),
            Attestations.MakeReceipt(ab), quality: 3);
        tags.ToJsonString().Should().Contain($"[\"proof\",\"{ab}\"]");
    }

    [Fact]
    public void SpecStyleEvent_WithoutJudgedTag_ReadsRatingAndProof()
    {
        var ab = string.Concat(Enumerable.Repeat("ab", 32));
        var tags = new JsonArray
        {
            new JsonArray { "d", "x" }, new JsonArray { "v", "2" }, new JsonArray { "p", new string('a', 64), "", "subject" },
            new JsonArray { "rating", "4" }, new JsonArray { "proof", ab },
        };
        var parsed = Attestations.Parse(Attestations.Sign(Sk, tags, "", 1));
        parsed.Should().NotBeNull();
        parsed!.Quality.Should().Be(4);
        parsed.Receipt.Should().Be(new AttestationReceipt(ab));
    }

    [Fact]
    public void RatingDisagreeingWithJudged_IsMalformed()
    {
        var tags = new JsonArray
        {
            new JsonArray { "d", "x" }, new JsonArray { "v", "2" }, new JsonArray { "u", Url },
            new JsonArray { "judged", "quality", "5" }, new JsonArray { "rating", "1" },
        };
        Attestations.Parse(Attestations.Sign(Sk, tags, "", 1)).Should().BeNull();
    }

    [Fact]
    public void NothingToAttest_Rejected()
    {
        var act = () => Attestations.BuildTags(Attestations.ResolveSubject(new string('a', 64), null));
        act.Should().Throw<AttestationException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void QualityOutOfRange_Rejected(int quality)
    {
        var act = () => Attestations.BuildTags(Attestations.ResolveSubject(new string('a', 64), null), quality: quality);
        act.Should().Throw<AttestationException>();
    }

    [Fact]
    public void UnknownMechCheck_Rejected()
    {
        var act = () => Attestations.BuildTags(Attestations.ResolveSubject(new string('a', 64), null), null,
            new Dictionary<string, bool> { ["fast"] = true });
        act.Should().Throw<AttestationException>();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "https://x.example/")]
    [InlineData("npub1xyz", null)]
    public void SubjectRules(string? pk, string? url)
    {
        var act = () => Attestations.ResolveSubject(pk, url);
        act.Should().Throw<AttestationException>();
    }

    [Fact]
    public void ContentLengthLimit()
    {
        var act = () => Attestations.CheckContent(new string('x', Attestations.MaxContentChars + 1));
        act.Should().Throw<AttestationException>();
    }

    // ── reading / aggregation ──────────────────────────────────────────────

    private const string Url = "https://api.example.com/v1/forecast";
    private static readonly AttestationSubject Subject = new("u", Url);

    private static JsonObject Ev(ECPrivKey sk, long createdAt, string? paymentHash = null,
        Dictionary<string, bool>? mech = null, int? quality = null, AttestationSubject? subject = null)
    {
        var tags = Attestations.BuildTags(subject ?? Subject,
            paymentHash != null ? Attestations.MakeReceipt(paymentHash) : null, mech, quality);
        return Attestations.Sign(sk, tags, "", createdAt);
    }

    private static JsonObject Agg(params JsonNode?[] events) =>
        JsonNode.Parse(JsonSerializer.Serialize(Attestations.Aggregate(events, Subject)))!.AsObject();

    [Fact]
    public void Parse_RoundTrip()
    {
        var tags = Attestations.BuildTags(Subject,
            Attestations.MakeReceipt(new string('1', 64), new string('2', 64), new string('3', 64)),
            new Dictionary<string, bool> { ["responded"] = true }, 3);
        var parsed = Attestations.Parse(Attestations.Sign(Sk, tags, "", 100));
        parsed.Should().NotBeNull();
        parsed!.ProofBacked.Should().BeTrue();
        parsed.Receipt.Should().Be(new AttestationReceipt(new string('1', 64), new string('2', 64), new string('3', 64)));
        parsed.Quality.Should().Be(3);
        parsed.Mech.Should().ContainKey("responded").WhoseValue.Should().BeTrue();
    }

    [Fact]
    public void Aggregate_SeparatesBackedUnbacked_AndFamilies_NoBlendedNumber()
    {
        var o = Agg(
            Ev(Sk, 100, new string('1', 64), new() { ["responded"] = true, ["on_time"] = false }, 5),
            Ev(SkOther, 101, string.Concat(Enumerable.Repeat("12", 32)), new() { ["responded"] = true, ["on_time"] = true }),
            Ev(SkThird, 102, null, new() { ["responded"] = false }, 1));

        o["verifiedEvents"]!.GetValue<int>().Should().Be(3);
        var backed = o["proofBacked"]!;
        var unbacked = o["unbacked"]!;
        backed["reviews"]!.GetValue<int>().Should().Be(2);
        unbacked["reviews"]!.GetValue<int>().Should().Be(1);
        backed["mechanical"]!["responded"]!.ToJsonString().Should().Be("{\"passed\":2,\"reported\":2,\"passRate\":1}");
        backed["mechanical"]!["on_time"]!.ToJsonString().Should().Be("{\"passed\":1,\"reported\":2,\"passRate\":0.5}");
        backed["mechanical"]!["well_formed"]!["passRate"].Should().BeNull();
        backed["judgedQuality"]!.ToJsonString().Should().Be("{\"ratings\":1,\"mean\":5}");
        unbacked["mechanical"]!["responded"]!["passRate"]!.GetValue<double>().Should().Be(0);
        unbacked["judgedQuality"]!["mean"]!.GetValue<double>().Should().Be(1);
        foreach (var k in new[] { "averageRating", "score", "rating", "trustScore" })
            o.ContainsKey(k).Should().BeFalse();
        o["notes"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain(Attestations.PaymentHashIsAReference);
    }

    [Fact]
    public void Aggregate_DedupsSameReviewerSameReceipt_LatestWins()
    {
        var oldEv = Ev(Sk, 100, new string('1', 64), quality: 1);
        var newEv = Ev(Sk, 200, new string('1', 64), quality: 5);
        var other = Ev(Sk, 150, string.Concat(Enumerable.Repeat("12", 32)), quality: 3);
        var o = Agg(newEv, oldEv, other, newEv.DeepClone());
        o["duplicatesCollapsed"]!.GetValue<int>().Should().Be(1);
        o["proofBacked"]!["reviews"]!.GetValue<int>().Should().Be(2);
        o["reviews"]!.AsArray().Select(r => r!["judgedQuality"]!.GetValue<int>()).Should().BeEquivalentTo(new[] { 5, 3 });
        o["distinctReviewers"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public void Aggregate_DedupsUnbackedByReviewerAndSubject()
    {
        var o = Agg(Ev(Sk, 1, quality: 2), Ev(Sk, 2, quality: 4));
        o["unbacked"]!["reviews"]!.GetValue<int>().Should().Be(1);
        o["reviews"]![0]!["judgedQuality"]!.GetValue<int>().Should().Be(4);
    }

    [Fact]
    public void Aggregate_RejectsTamperedEvents()
    {
        var good = Ev(Sk, 100, mech: new() { ["responded"] = true });
        var tampered = good.DeepClone().AsObject();
        var tags = tampered["tags"]!.AsArray();
        tags[^1] = new JsonArray { "mech", "responded", "0" }; // flip the verdict, keep id/sig
        var forged = Ev(SkOther, 100, mech: new() { ["responded"] = true });
        forged["sig"] = good["sig"]!.GetValue<string>();

        var o = Agg(tampered, forged);
        o["rejected"]!["invalidSignature"]!.GetValue<int>().Should().Be(2);
        o["reviews"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public void Aggregate_CountsLegacyAndWrongSubject()
    {
        var legacyTags = new JsonArray
        {
            new JsonArray { "d", "att-1" }, new JsonArray { "p", new string('a', 64), "", "subject" }, new JsonArray { "rating", "5" },
        };
        var legacy = Attestations.Sign(Sk, legacyTags, "great", 5);
        var elsewhere = Ev(Sk, 5, quality: 4, subject: new AttestationSubject("u", "https://other.example/"));
        var o = Agg(legacy, elsewhere);
        o["legacyIgnored"]!.GetValue<int>().Should().Be(1);
        o["rejected"]!["wrongSubject"]!.GetValue<int>().Should().Be(1);
        o["reviews"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public void Aggregate_RejectsMalformedV2()
    {
        var tags = new JsonArray
        {
            new JsonArray { "d", "x" }, new JsonArray { "v", "2" }, new JsonArray { "u", Url }, new JsonArray { "judged", "quality", "9" },
        };
        var o = Agg(Attestations.Sign(Sk, tags, "", 1), JsonValue.Create("not-an-event"));
        o["rejected"]!["malformed"]!.GetValue<int>().Should().Be(2);
    }
}

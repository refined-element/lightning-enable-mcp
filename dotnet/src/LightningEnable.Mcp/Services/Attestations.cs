using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NBitcoin.Secp256k1;

namespace LightningEnable.Mcp.Services;

/// <summary>Invalid attestation input. The message is safe to show to the caller.</summary>
public sealed class AttestationException(string message) : Exception(message);

/// <summary>Who is reviewed: a Nostr pubkey (<c>p</c>) or a normalized endpoint URL (<c>u</c>).</summary>
public sealed record AttestationSubject(string Type, string Value)
{
    public object ToResult() => new { type = Type == "p" ? "pubkey" : "url", value = Value };
}

/// <summary>A receipt reference: payment hash plus optional request/response SHA-256 ("" when unknown).</summary>
public sealed record AttestationReceipt(string PaymentHash, string RequestSha256 = "", string ResponseSha256 = "");

/// <summary>
/// Client-signed provider attestations (kind 38403, shape version 2). Mirrors the Python
/// <c>attestation</c> module; <c>shared/attestation-golden-vector.json</c> pins both ports
/// to the same event bytes.
///
/// Tags, in this order (optional ones omitted, never sent empty):
/// <c>["d", key]</c>, <c>["v","2"]</c>, <c>["p", pubkey]</c> or <c>["u", url]</c>,
/// <c>["agreement", id]</c>?, <c>["receipt", payment_hash, request_sha256, response_sha256]</c>?,
/// one <c>["mech", check, "1"|"0"]</c> per check, <c>["judged","quality","1".."5"]</c>?.
///
/// Kind 38403 is addressable, so a relay keeps one event per (reviewer, kind, d). <c>d</c> is
/// <c>receipt:&lt;payment_hash&gt;</c> for a receipt-backed review and <c>p:&lt;pubkey&gt;</c> /
/// <c>u:&lt;url&gt;</c> otherwise, so re-reviewing the same purchase replaces the earlier review.
///
/// Mechanical checks and judged quality are separate families everywhere; nothing here blends
/// them into one number. A payment hash in a receipt tag is a reference, not a verified payment.
/// </summary>
public static class Attestations
{
    public const int Kind = 38403;
    public const string ShapeVersion = "2";
    public const int MaxContentChars = 1000;

    public static readonly IReadOnlyList<string> MechChecks =
        new[] { "responded", "on_time", "well_formed", "price_honored" };

    public const string PaymentHashIsAReference =
        "A payment hash in a receipt tag is a reference, not a verified payment: any "
        + "reviewer can write any hash. Verifying it needs the invoice or preimage disclosed, "
        + "which this tool does not do. 'proofBacked' means the review carries a well-formed "
        + "receipt reference, nothing more.";

    public const string NotATrustScore =
        "Mechanical pass rates and the judged-quality mean are unweighted counts over the "
        + "verified events below. They are not a trust score: reviewers are not weighted by "
        + "identity, history or stake, and one operator can run many reviewer keys.";

    // ── endpoint URL normalization ──────────────────────────────────────────

    private static readonly Regex UrlRe = new(
        @"^(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*)://(?<authority>[^/?#]*)(?<path>[^?#]*)(?:\?(?<query>[^#]*))?(?:#.*)?$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex PortRe = new(@"^[0-9]{1,5}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The subject identity for a seller that has no Nostr pubkey. Rule (identical in both
    /// ports): absolute http/https only; scheme and host lowercased (ASCII host — send IDNs as
    /// punycode); default port (80/443) dropped, others kept; user info rejected (it would
    /// publish a credential); path kept exactly as given, empty path becomes <c>/</c>; fragment
    /// always dropped; query dropped unless <paramref name="includeQuery"/> (set it only when
    /// the query is part of the resource identity, never when it carries a credential), and
    /// then kept verbatim; an empty query is dropped.
    /// </summary>
    public static string NormalizeEndpointUrl(string? url, bool includeQuery = false)
    {
        var text = (url ?? string.Empty).Trim();
        var m = UrlRe.Match(text);
        if (!m.Success)
            throw new AttestationException("subject_url must be an absolute http(s) URL.");

        var scheme = m.Groups["scheme"].Value.ToLowerInvariant();
        string defaultPort = scheme switch
        {
            "http" => "80",
            "https" => "443",
            _ => throw new AttestationException("subject_url must use http or https."),
        };

        var authority = m.Groups["authority"].Value;
        if (authority.Contains('@'))
            throw new AttestationException("subject_url must not contain user info (user:pass@) - it would be published.");

        string host, rest;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0) throw new AttestationException("subject_url has a malformed IPv6 host.");
            host = authority[..(close + 1)];
            rest = authority[(close + 1)..];
        }
        else
        {
            var colon = authority.IndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            rest = colon < 0 ? string.Empty : authority[colon..];
        }

        var port = string.Empty;
        if (rest.Length > 0)
        {
            if (!rest.StartsWith(':') || !PortRe.IsMatch(rest[1..]))
                throw new AttestationException("subject_url has an invalid port.");
            port = rest[1..];
        }

        if (host.Length == 0 || host.Any(c => c >= 0x7F || c < 0x21))
            throw new AttestationException("subject_url needs an ASCII host (send internationalized names as punycode).");
        host = host.ToLowerInvariant();

        var path = m.Groups["path"].Value;
        if (path.Length == 0) path = "/";
        if (path.Any(IsBadUrlChar))
            throw new AttestationException("subject_url path must not contain whitespace or control characters.");

        var outp = $"{scheme}://{host}";
        if (port.Length > 0 && port != defaultPort) outp += ":" + port;
        outp += path;

        var query = m.Groups["query"].Success ? m.Groups["query"].Value : string.Empty;
        if (includeQuery && query.Length > 0)
        {
            if (query.Any(IsBadUrlChar))
                throw new AttestationException("subject_url query must not contain whitespace or control characters.");
            outp += "?" + query;
        }
        return outp;
    }

    // Whitespace per Python's str.isspace for the characters a URL could carry, plus controls.
    private static bool IsBadUrlChar(char c) => c < 0x20 || c == 0x7F || char.IsWhiteSpace(c);

    // ── building ────────────────────────────────────────────────────────────

    /// <summary>Exactly one of <paramref name="subjectPubkey"/> (64 hex) or <paramref name="subjectUrl"/>.</summary>
    public static AttestationSubject ResolveSubject(string? subjectPubkey, string? subjectUrl, bool includeQuery = false)
    {
        var pk = (subjectPubkey ?? string.Empty).Trim();
        var url = (subjectUrl ?? string.Empty).Trim();
        if (pk.Length > 0 && url.Length > 0)
            throw new AttestationException("Pass subject_pubkey OR subject_url, not both.");
        if (pk.Length > 0)
        {
            if (!NostrEvents.IsHex(pk, 64))
                throw new AttestationException("subject_pubkey must be a 64-character hex Nostr pubkey.");
            return new AttestationSubject("p", pk.ToLowerInvariant());
        }
        if (url.Length > 0)
            return new AttestationSubject("u", NormalizeEndpointUrl(url, includeQuery));
        throw new AttestationException(
            "A subject is required: subject_pubkey (the seller's Nostr pubkey) or, when the "
            + "seller has none, subject_url (the L402 endpoint).");
    }

    /// <summary>Validate a receipt reference; null when no payment hash is given.</summary>
    public static AttestationReceipt? MakeReceipt(string? paymentHash, string? requestSha256 = null, string? responseSha256 = null)
    {
        var ph = HashOrEmpty("payment_hash", paymentHash);
        var req = HashOrEmpty("request_sha256", requestSha256);
        var resp = HashOrEmpty("response_sha256", responseSha256);
        if (ph.Length == 0)
        {
            if (req.Length > 0 || resp.Length > 0)
                throw new AttestationException("request_sha256/response_sha256 need a payment_hash.");
            return null;
        }
        return new AttestationReceipt(ph, req, resp);
    }

    private static string HashOrEmpty(string name, string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length > 0 && !NostrEvents.IsHex(text, 64))
            throw new AttestationException($"{name} must be 64 hex characters (a SHA-256).");
        return text;
    }

    /// <summary>The tag list for a version-2 attestation, in canonical order.</summary>
    public static JsonArray BuildTags(
        AttestationSubject subject,
        AttestationReceipt? receipt = null,
        IReadOnlyDictionary<string, bool>? mech = null,
        int? quality = null,
        string? agreementId = null)
    {
        mech ??= new Dictionary<string, bool>();
        var unknown = mech.Keys.Where(k => !MechChecks.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new AttestationException($"Unknown mechanical check(s): {string.Join(", ", unknown)}.");
        if (quality is < 1 or > 5)
            throw new AttestationException("quality must be an integer from 1 to 5.");
        if (mech.Count == 0 && quality == null)
            throw new AttestationException(
                "Nothing to attest: give at least one mechanical check (responded, on_time, "
                + "well_formed, price_honored) or a judged quality from 1 to 5.");
        var agreement = (agreementId ?? string.Empty).Trim().ToLowerInvariant();
        if (agreement.Length > 0 && !NostrEvents.IsHex(agreement, 64))
            throw new AttestationException("agreement_id must be a 64-character hex event id.");

        var d = receipt != null ? $"receipt:{receipt.PaymentHash}" : $"{subject.Type}:{subject.Value}";
        var tags = new JsonArray
        {
            new JsonArray { "d", d },
            new JsonArray { "v", ShapeVersion },
            new JsonArray { subject.Type, subject.Value },
        };
        if (agreement.Length > 0)
            tags.Add(new JsonArray { "agreement", agreement });
        if (receipt != null)
            tags.Add(new JsonArray { "receipt", receipt.PaymentHash, receipt.RequestSha256, receipt.ResponseSha256 });
        foreach (var check in MechChecks)
        {
            if (mech.TryGetValue(check, out var passed))
                tags.Add(new JsonArray { "mech", check, passed ? "1" : "0" });
        }
        if (quality != null)
            tags.Add(new JsonArray { "judged", "quality", quality.Value.ToString(CultureInfo.InvariantCulture) });
        return tags;
    }

    public static string CheckContent(string? content)
    {
        var text = content ?? string.Empty;
        if (text.Length > MaxContentChars)
            throw new AttestationException(
                $"content is limited to {MaxContentChars} characters; it is a short note, never the prompt or the response.");
        return text;
    }

    /// <summary>
    /// A complete signed kind-38403 event. With <paramref name="auxRand32"/> null, fresh random
    /// auxiliary bytes are drawn (production); fixed bytes give a deterministic signature.
    /// </summary>
    public static JsonObject Sign(ECPrivKey key, JsonArray tags, string content, long createdAt, byte[]? auxRand32 = null)
    {
        var pubkey = NostrEvents.XOnlyPubkeyHex(key);
        var id = NostrEvents.ComputeId(pubkey, createdAt, Kind, tags, content);
        var sig = auxRand32 != null ? NostrEvents.SignId(key, id, auxRand32) : NostrEvents.SignIdWithFreshAux(key, id);
        return new JsonObject
        {
            ["id"] = id,
            ["pubkey"] = pubkey,
            ["created_at"] = createdAt,
            ["kind"] = Kind,
            ["tags"] = tags.DeepClone(),
            ["content"] = content,
            ["sig"] = sig,
        };
    }

    // ── reading ─────────────────────────────────────────────────────────────

    public sealed record Parsed(
        string EventId,
        string Reviewer,
        long CreatedAt,
        AttestationSubject Subject,
        AttestationReceipt? Receipt,
        IReadOnlyDictionary<string, bool> Mech,
        int? Quality,
        string? AgreementId,
        string Content)
    {
        public bool ProofBacked => Receipt != null;

        public (string Reviewer, string Ref) DedupKey =>
            (Reviewer, Receipt != null ? $"receipt:{Receipt.PaymentHash}" : $"{Subject.Type}:{Subject.Value}");

        public object ToResult() => new
        {
            eventId = EventId,
            reviewerPubkey = Reviewer,
            createdAt = CreatedAt,
            proofBacked = ProofBacked,
            receipt = Receipt == null ? null : new
            {
                paymentHash = Receipt.PaymentHash,
                requestSha256 = Receipt.RequestSha256.Length > 0 ? Receipt.RequestSha256 : null,
                responseSha256 = Receipt.ResponseSha256.Length > 0 ? Receipt.ResponseSha256 : null,
            },
            mechanical = MechChecks.Where(Mech.ContainsKey).ToDictionary(c => c, c => Mech[c]),
            judgedQuality = Quality,
            agreementId = AgreementId,
            content = Content,
        };
    }

    public static bool IsVersion2(JsonObject ev)
    {
        if (ev["tags"] is not JsonArray tags) return false;
        foreach (var t in tags)
        {
            if (t is JsonArray a && a.Count >= 2 && Str(a[0]) == "v" && Str(a[1]) == ShapeVersion)
                return true;
        }
        return false;
    }

    /// <summary>Parse a version-2 attestation's tags; null when malformed. Does NOT verify the signature.</summary>
    public static Parsed? Parse(JsonObject ev)
    {
        try
        {
            if (ev["kind"]?.GetValue<int>() != Kind || !IsVersion2(ev)) return null;
            AttestationSubject? subject = null;
            AttestationReceipt? receipt = null;
            var mech = new Dictionary<string, bool>();
            int? quality = null;
            string? agreement = null;

            foreach (var node in (JsonArray)ev["tags"]!)
            {
                if (node is not JsonArray tag || tag.Count == 0 || Str(tag[0]) is not string name) continue;
                var vals = new List<string>();
                for (var i = 1; i < tag.Count; i++)
                {
                    if (Str(tag[i]) is not string s) return null;
                    vals.Add(s);
                }

                if ((name == "p" || name == "u") && subject == null && vals.Count > 0)
                {
                    if (name == "p" && !NostrEvents.IsHex(vals[0], 64)) return null;
                    subject = new AttestationSubject(name, name == "p" ? vals[0].ToLowerInvariant() : vals[0]);
                }
                else if (name == "receipt" && receipt == null)
                {
                    if (vals.Count != 3 || !NostrEvents.IsHex(vals[0], 64)) return null;
                    if (vals.Skip(1).Any(h => h.Length > 0 && !NostrEvents.IsHex(h, 64))) return null;
                    receipt = new AttestationReceipt(vals[0].ToLowerInvariant(), vals[1].ToLowerInvariant(), vals[2].ToLowerInvariant());
                }
                else if (name == "mech" && vals.Count == 2 && MechChecks.Contains(vals[0]))
                {
                    if (vals[1] != "0" && vals[1] != "1") return null;
                    mech.TryAdd(vals[0], vals[1] == "1");
                }
                else if (name == "judged" && vals.Count == 2 && vals[0] == "quality")
                {
                    if (vals[1] is not ("1" or "2" or "3" or "4" or "5") || quality != null) return null;
                    quality = vals[1][0] - '0';
                }
                else if (name == "agreement" && vals.Count > 0 && agreement == null)
                {
                    agreement = vals[0];
                }
            }

            if (subject == null || (mech.Count == 0 && quality == null)) return null;
            return new Parsed(
                ev["id"]!.GetValue<string>(),
                ev["pubkey"]!.GetValue<string>().ToLowerInvariant(),
                ev["created_at"]!.GetValue<long>(),
                subject, receipt, mech, quality, agreement,
                Str(ev["content"]) ?? string.Empty);
        }
        catch
        {
            return null;
        }
    }

    private static string? Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private sealed class Family
    {
        public int Reviews;
        public readonly Dictionary<string, (int Passed, int Reported)> Checks =
            MechChecks.ToDictionary(c => c, _ => (0, 0));
        public readonly List<int> Quality = new();

        public void Add(Parsed a)
        {
            Reviews++;
            foreach (var (check, passed) in a.Mech)
            {
                var (p, n) = Checks[check];
                Checks[check] = (p + (passed ? 1 : 0), n + 1);
            }
            if (a.Quality != null) Quality.Add(a.Quality.Value);
        }

        public object ToResult() => new
        {
            reviews = Reviews,
            mechanical = MechChecks.ToDictionary(c => c, c => new
            {
                passed = Checks[c].Passed,
                reported = Checks[c].Reported,
                passRate = Checks[c].Reported > 0
                    ? Math.Round((double)Checks[c].Passed / Checks[c].Reported, 4)
                    : (double?)null,
            }),
            judgedQuality = new
            {
                ratings = Quality.Count,
                mean = Quality.Count > 0 ? Math.Round(Quality.Average(), 2) : (double?)null,
            },
        };
    }

    /// <summary>
    /// Verify, parse, deduplicate and summarize attestation events for <paramref name="subject"/>.
    /// Every event's id and BIP-340 signature are checked (failures counted and dropped); events
    /// without the version-2 marker are counted as legacy; one reviewer's several attestations
    /// for the same receipt reference (or, without one, the same subject) collapse to the latest
    /// (NIP-01 tie-break: lowest id); proof-backed and unbacked reviews are summarized separately,
    /// and within each the mechanical checks and the judged quality are reported separately.
    /// </summary>
    public static object Aggregate(IReadOnlyList<JsonNode?> events, AttestationSubject subject)
    {
        int invalidSignature = 0, malformed = 0, wrongSubject = 0, legacy = 0, verified = 0, accepted = 0;
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var latest = new Dictionary<(string, string), Parsed>();

        foreach (var node in events)
        {
            if (node is not JsonObject ev)
            {
                malformed++;
                continue;
            }
            var id = Str(ev["id"]);
            if (id != null && seenIds.Contains(id)) continue; // the same event delivered twice
            if (!NostrEvents.Verify(ev))
            {
                invalidSignature++;
                continue;
            }
            seenIds.Add(id!);
            verified++;
            if (ev["kind"]?.GetValue<int>() != Kind || !IsVersion2(ev))
            {
                legacy++;
                continue;
            }
            var parsed = Parse(ev);
            if (parsed == null)
            {
                malformed++;
                continue;
            }
            if (parsed.Subject != subject)
            {
                wrongSubject++;
                continue;
            }
            accepted++;
            if (!latest.TryGetValue(parsed.DedupKey, out var cur)
                || parsed.CreatedAt > cur.CreatedAt
                || (parsed.CreatedAt == cur.CreatedAt && string.CompareOrdinal(parsed.EventId, cur.EventId) < 0))
            {
                latest[parsed.DedupKey] = parsed;
            }
        }

        var kept = latest.Values
            .OrderByDescending(a => a.CreatedAt)
            .ThenBy(a => a.EventId, StringComparer.Ordinal)
            .ToList();
        var backed = new Family();
        var unbacked = new Family();
        foreach (var a in kept) (a.ProofBacked ? backed : unbacked).Add(a);

        return new
        {
            subject = subject.ToResult(),
            eventsReceived = events.Count,
            verifiedEvents = verified,
            rejected = new { invalidSignature, malformed, wrongSubject },
            legacyIgnored = legacy,
            duplicatesCollapsed = accepted - kept.Count,
            distinctReviewers = kept.Select(a => a.Reviewer).Distinct().Count(),
            proofBacked = backed.ToResult(),
            unbacked = unbacked.ToResult(),
            reviews = kept.Select(a => a.ToResult()).ToList(),
            notes = new[] { PaymentHashIsAReference, NotATrustScore },
        };
    }
}

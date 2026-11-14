using System.Text.Json;
using System.Text.Json.Nodes;
using LightningEnable.Mcp.Services;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// Client-signed provider reviews — the <c>agent_services</c> actions <c>review</c>,
/// <c>reviews</c> and <c>reviewer_key</c>.
/// <list type="bullet">
/// <item><c>review</c> signs a kind-38403 (shape v2) attestation with this installation's own
///   reviewer key and publishes it straight to the relay. Needs no API key and no wallet.</item>
/// <item><c>reviews</c> reads a subject's attestations from the relay, verifies every signature,
///   and reports proof-backed and unbacked reviews separately, mechanical checks and judged
///   quality separately. No blended score.</item>
/// <item><c>reviewer_key</c> shows the reviewer public key (hex and npub), never the secret.</item>
/// </list>
/// The deprecated <c>attest</c>/<c>reputation</c> actions are unchanged. Mirrors Python's
/// <c>tools/provider_review.py</c>; the result keys are the same in both ports.
/// </summary>
public static class ProviderReviewTool
{
    public const int DefaultReviewsLimit = 20;
    public const int MaxReviewsLimit = 500;
    private static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string Error(string message, string errorType, object? extra = null)
    {
        var o = new JsonObject { ["success"] = false, ["error"] = message, ["errorType"] = errorType };
        if (extra != null && JsonSerializer.SerializeToNode(extra) is JsonObject more)
        {
            foreach (var (k, v) in more) o[k] = v?.DeepClone();
        }
        return o.ToJsonString(Indented);
    }

    public const string UnknownPaymentMessage =
        "This payment hash is not in the local receipt log, so this server cannot vouch that the "
        + "payment happened. Readers that follow the NIP weight a 'proof' tag as a verified payment, "
        + "so it is refused. Pass the hash of a payment this server made, omit it, or set "
        + "allow_unknown_payment=true to publish it as an unbacked receipt reference without the proof tag.";

    /// <summary>
    /// Sign and publish one provider review. A payment hash the local receipt log knows becomes
    /// the NIP <c>proof</c> tag plus the <c>receipt</c> reference with the salted digests recorded
    /// for that call. A hash the log does not know is refused (<c>unknown_payment</c>) unless
    /// <paramref name="allowUnknownPayment"/>; then only the <c>receipt</c> reference is published,
    /// WITHOUT <c>proof</c>, and the review counts as unbacked.
    /// </summary>
    public static async Task<string> PublishReview(
        string? subjectPubkey, string? subjectUrl, bool includeQuery,
        string? paymentHash, bool allowUnknownPayment,
        IReadOnlyDictionary<string, bool>? checks,
        int? quality, string? agreementId, string? content,
        AttestationKeyStore? keyStore, IReceiptService? receiptService, INostrRelayClient? relayClient,
        CancellationToken cancellationToken, string? relayUrl = null)
    {
        AttestationSubject subject;
        AttestationReceipt? receipt;
        Dictionary<string, bool> mech;
        JsonArray tags;
        string text;
        var knownLocally = false;
        try
        {
            subject = Attestations.ResolveSubject(subjectPubkey, subjectUrl, includeQuery);
            receipt = Attestations.MakeReceipt(paymentHash);
            mech = new Dictionary<string, bool>(checks ?? new Dictionary<string, bool>());
            var unknown = mech.Keys.FirstOrDefault(k => !Attestations.MechChecks.Contains(k));
            if (unknown != null)
                throw new AttestationException($"Unknown check '{unknown}'. Use: {string.Join(", ", Attestations.MechChecks)}.");
            text = Attestations.CheckContent(content);
            if (receipt != null)
            {
                var local = receiptService?.LookupPayment(receipt.PaymentHash);
                if (local != null)
                {
                    knownLocally = true;
                    receipt = new AttestationReceipt(receipt.PaymentHash,
                        (local.RequestDigest ?? string.Empty).ToLowerInvariant(),
                        (local.ResponseDigest ?? string.Empty).ToLowerInvariant());
                }
                else if (!allowUnknownPayment)
                {
                    return Error(UnknownPaymentMessage, "unknown_payment");
                }
            }
            tags = Attestations.BuildTags(subject, receipt, mech, quality, agreementId, proof: knownLocally);
        }
        catch (AttestationException ex)
        {
            return Error(ex.Message, "invalid_input");
        }

        if (keyStore == null)
            return Error("Reviewer key store not available.", "key_unavailable");
        if (relayClient == null)
            return Error("Relay client not available. The MCP server may not be configured correctly.", "relay_unavailable");

        JsonObject ev;
        ReviewerIdentity identity;
        try
        {
            identity = keyStore.GetIdentity();
            ev = Attestations.Sign(keyStore.GetSigningKey(), tags, text, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        catch (AttestationKeyException ex)
        {
            return Error(ex.Message, "key_unavailable");
        }

        var relay = relayUrl ?? relayClient.DefaultRelayUrl;
        RelayOutcome outcome;
        try
        {
            outcome = await relayClient.PublishAsync(ev, relay, RelayTimeout, cancellationToken);
        }
        catch (Exception ex)
        {
            return Error($"Relay publish failed: {ex.GetType().Name}", RelayOutcome.ProtocolError);
        }

        var summary = new
        {
            eventId = ev["id"]!.GetValue<string>(),
            reviewer = new { pubkey = identity.PubkeyHex, npub = identity.Npub },
            relay,
            subject = subject.ToResult(),
            proofBacked = knownLocally,
            receipt = receipt == null ? null : new
            {
                paymentHash = receipt.PaymentHash,
                requestDigest = receipt.RequestSha256.Length > 0 ? receipt.RequestSha256 : null,
                responseDigest = receipt.ResponseSha256.Length > 0 ? receipt.ResponseSha256 : null,
                knownToLocalReceiptLog = knownLocally,
            },
            mechanical = mech,
            judgedQuality = quality,
        };

        if (!outcome.Ok)
        {
            var extra = JsonSerializer.SerializeToNode(summary)!.AsObject();
            extra["event"] = ev.DeepClone();
            return Error($"The relay did not accept the review ({outcome.Status}): {outcome.Message}", outcome.Status, extra);
        }

        var result = new JsonObject { ["success"] = true };
        foreach (var (k, v) in JsonSerializer.SerializeToNode(summary)!.AsObject()) result[k] = v?.DeepClone();
        result["relayMessage"] = outcome.Message.Length > 0 ? outcome.Message : null;
        result["event"] = ev.DeepClone();
        result["message"] = "Review signed with this installation's reviewer key and accepted by the relay "
            + $"as kind {Attestations.Kind} (shape v{Attestations.ShapeVersion}).";
        result["note"] = Attestations.PaymentHashIsAReference;
        return result.ToJsonString(Indented);
    }

    public static async Task<string> ReadReviews(
        string? subjectPubkey, string? subjectUrl, bool includeQuery, int? limit,
        INostrRelayClient? relayClient, CancellationToken cancellationToken, string? relayUrl = null)
    {
        AttestationSubject subject;
        try
        {
            subject = Attestations.ResolveSubject(subjectPubkey, subjectUrl, includeQuery);
        }
        catch (AttestationException ex)
        {
            return Error(ex.Message, "invalid_input");
        }
        if (relayClient == null)
            return Error("Relay client not available. The MCP server may not be configured correctly.", "relay_unavailable");

        var n = Math.Clamp(limit ?? DefaultReviewsLimit, 1, MaxReviewsLimit);
        var relay = relayUrl ?? relayClient.DefaultRelayUrl;
        // Fetch the full window (not just n) so one prolific key cannot fill it and hide everyone
        // else; n only bounds how many individual reviews are listed.
        var filter = new JsonObject
        {
            ["kinds"] = new JsonArray { Attestations.Kind },
            [$"#{subject.Type}"] = new JsonArray { subject.Value },
            ["limit"] = NostrRelayClient.MaxQueryEvents,
        };
        RelayOutcome outcome;
        try
        {
            outcome = await relayClient.QueryAsync(filter, relay, RelayTimeout, cancellationToken);
        }
        catch (Exception ex)
        {
            return Error($"Relay query failed: {ex.GetType().Name}", RelayOutcome.ProtocolError);
        }
        if (!outcome.Ok && outcome.Status != RelayOutcome.Timeout)
            return Error($"Could not read reviews from the relay ({outcome.Status}): {outcome.Message}", outcome.Status, new { relay });

        var events = outcome.Events.ToList();
        var result = new JsonObject { ["success"] = true, ["relay"] = relay, ["complete"] = outcome.Ok };
        if (!outcome.Ok) result["warning"] = outcome.Message + " Results are partial.";
        foreach (var (k, v) in JsonSerializer.SerializeToNode(Attestations.Aggregate(events, subject, n))!.AsObject())
            result[k] = v?.DeepClone();
        return result.ToJsonString(Indented);
    }

    public static string ReviewerKey(AttestationKeyStore? keyStore)
    {
        if (keyStore == null)
            return Error("Reviewer key store not available.", "key_unavailable");
        ReviewerIdentity identity;
        try
        {
            identity = keyStore.GetIdentity();
        }
        catch (AttestationKeyException ex)
        {
            return Error(ex.Message, "key_unavailable");
        }
        var result = new JsonObject { ["success"] = true };
        foreach (var (k, v) in JsonSerializer.SerializeToNode(identity.ToResult())!.AsObject()) result[k] = v?.DeepClone();
        result["note"] = "This key signs only reviews. It is not your wallet key and cannot move money. "
            + "The secret never leaves this machine. " + AttestationKeyStore.RotationNote;
        return result.ToJsonString(Indented);
    }
}

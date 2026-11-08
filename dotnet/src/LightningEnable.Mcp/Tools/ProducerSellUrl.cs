using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>One claim error code: what to tell the operator, and whether a retry can help.</summary>
internal sealed record ClaimErrorInfo(string Message, bool Retryable);

/// <summary>
/// The claim error vocabulary (claim-endpoint spec §3.2 plus the codes the endpoint really
/// answers with). Pinned against <c>shared/l402-producer-sell-url.json</c> so both ports agree.
/// </summary>
internal static class ProducerClaimErrors
{
    public const string FeatureNotEnabled = "feature_not_enabled";
    public const string RateLimited = "rate_limited";
    public const string NetworkError = "network_error";

    public static readonly IReadOnlyDictionary<string, ClaimErrorInfo> Table = new Dictionary<string, ClaimErrorInfo>
    {
        ["invalid_target_url"] = new(
            "The target URL must be https on a public hostname, at most 500 characters, with no IP address, "
            + "credentials, or #fragment. A private or internal address is refused on purpose.", false),
        ["invalid_price"] = new(
            "The price per call is outside the allowed range: see minProxyPriceSats and maxProxyPriceSats.", false),
        ["invalid_lightning_address"] = new(
            "The Lightning Address did not resolve, or the price per call is outside what it accepts "
            + "(minSendableSats to maxSendableSats). Check the spelling and that the wallet provider supports "
            + "Lightning Addresses.", false),
        ["invalid_recovery"] = new(
            "Recovery must be an email or a 64-hex-character Nostr public key, not both. Disposable email, and a "
            + "Nostr key already bound to another account, are refused.", false),
        ["terms_required"] = new(
            "acceptTerms must equal the quote's terms.version exactly. Show the operator the terms and ask again.", false),
        ["invalid_name"] = new("The name is required, 1 to 100 characters.", false),
        ["invalid_description"] = new("The description can be at most 500 characters.", false),
        ["invalid_receive"] = new("Only a Lightning Address receive lane is accepted here.", false),
        ["connect_not_available"] = new(
            "The wallet-connect hand-off for Strike, OpenNode, and NWC is not available on this server. Use a "
            + "Lightning Address, or l402_producer configure_receive on an existing account.", false),
        ["claim_body_mismatch"] = new(
            "The payment was made for a different claim body. This tool always resends the saved body, so contact "
            + "support with the payment hash.", false),
        ["invalid_l402_token"] = new(
            "The payment token is invalid, expired, or not for a claim. A new claim is a new payment.", false),
        ["invalid_authorization"] = new("The Authorization header was malformed.", false),
        ["plan_limit_reached"] = new(
            "That account's plan is at its proxy limit. Claim without an API key, or upgrade the plan.", false),
        ["receive_lane_already_configured"] = new(
            "That account already has a different receive wallet, and a claim never replaces it.", false),
        ["merchant_exists"] = new(
            "An account already uses that recovery email. Sign in to the dashboard, then claim with that "
            + "account's API key, or use a different recovery.", false),
        ["payment_provider_busy"] = new(
            "The platform's payment provider did not return an invoice in time. Nothing was invoiced or charged.", true),
        ["payment_provider_rejected"] = new(
            "The platform's payment provider refused to create the invoice. Nothing was charged; a retry repeats "
            + "the refusal until the provider side changes.", false),
        ["upstream_unavailable"] = new("A transient upstream failure. Nothing was invoiced.", true),
        ["challenge_persist_failed"] = new("The challenge could not be recorded. Nothing was invoiced.", true),
        ["claim_contended"] = new("A unique proxy id could not be allocated. Nothing was created.", true),
        ["claim_unavailable"] = new(
            "Claims are not available on this deployment right now. Check the quote again later.", false),
        ["claim_failed"] = new("The server hit an unexpected claim state.", true),
        [FeatureNotEnabled] = new("Claims are not enabled on this Lightning Enable server.", false),
        [RateLimited] = new("Too many claim requests from this address. Wait, then try again.", true),
        [NetworkError] = new("The Lightning Enable API could not be reached.", true),
    };

    /// <summary>
    /// Refusals that are permanent before payment but can clear on a phase-2 retry: phase 2
    /// re-runs the DNS check and the Lightning Address lookup, so a provider or resolver that
    /// was briefly down recovers.
    /// </summary>
    public static readonly IReadOnlySet<string> RetryAfterPaymentAlso = new HashSet<string>
    {
        "invalid_lightning_address", "invalid_target_url",
    };

    public static ClaimErrorInfo Describe(string code, int status) =>
        Table.TryGetValue(code, out var info)
            ? info
            : new ClaimErrorInfo($"The claim endpoint answered HTTP {status}.", status >= 500);
}

/// <summary>Seams for tests; production uses the defaults.</summary>
internal sealed class SellUrlOptions
{
    public static readonly SellUrlOptions Default = new();

    /// <summary>Waits between phase-2 attempts.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>The merchant key in the environment, which outranks the config file.</summary>
    public Func<string?> EnvironmentApiKey { get; init; } =
        () => Environment.GetEnvironmentVariable("LIGHTNING_ENABLE_API_KEY");

    /// <summary>Overrides the config file path (and so the scratch and key directory).</summary>
    public string? ConfigPath { get; init; }

    /// <summary>The clock, for the saved challenge's expiry.</summary>
    public Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;
}

/// <summary>
/// <c>l402_producer(action="sell_url")</c>: a URL plus a Lightning Address becomes a paid L402
/// endpoint in one call, through <c>POST /api/producer/claim</c>.
///
/// <para><b>Flow.</b> Quote (free) → phase 1 (402 with invoice and macaroon) → pay through
/// <see cref="PayL402ChallengeTool"/>, the same budget and out-of-band confirmation path every
/// paying tool uses → phase 2 with the byte-identical body and <c>Authorization: L402</c>.</para>
///
/// <para><b>Crash safety.</b> The exact body and the challenge are written to
/// <c>&lt;config dir&gt;/claim/sell-&lt;hash&gt;.json</c> (0600) BEFORE paying, and the preimage
/// right after, so calling again with the same arguments skips phase 1 and redeems the token
/// already paid for. The file is deleted once the claim answers 200.</para>
///
/// <para><b>Secrets.</b> The macaroon and preimage never enter a tool result; the API key
/// enters exactly one (the first 200), after it has been stored.</para>
/// </summary>
internal static class ProducerSellUrl
{
    internal const int MaxRedeemAttempts = 4;
    private const string ConfigKey = "lightningEnableApiKey";
    private const int MaxRetryDelaySeconds = 10;

    private const string BodyRule =
        "The claim body is written once and sent twice: the payment is bound to the target URL, the Lightning "
        + "Address, and the recovery binding. Changing any of them after paying makes the payment unusable, so "
        + "call sell_url again with exactly the same arguments.";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static async Task<string> SellUrlAsync(
        string? targetUrl,
        string? lightningAddress,
        long priceSats,
        string? name,
        string? description,
        string? recoveryEmail,
        string? recoveryNostrPubkey,
        bool listPublicly,
        string? acceptTerms,
        string? confirmationNonce,
        IProducerClaimClient? claimClient,
        IL402HttpClient? l402Client,
        IBudgetService? budgetService,
        IPriceService? priceService,
        IPaymentHistoryService? paymentHistory,
        IBudgetConfigurationService? configService,
        McpServer? server,
        SellUrlOptions? options,
        CancellationToken cancellationToken)
    {
        options ??= SellUrlOptions.Default;
        try
        {
            // ── Local checks: nothing is sent, nothing is minted. ──────────────────────
            targetUrl = targetUrl?.Trim();
            lightningAddress = lightningAddress?.Trim();
            if (string.IsNullOrEmpty(targetUrl))
                return LocalRefusal("invalid_target_url", "targetUrl is required: the https URL of the API to sell.");
            if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var target)
                || (target.Scheme != Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttp)
                || string.IsNullOrEmpty(target.Host))
                return LocalRefusal("invalid_target_url", "targetUrl must be an absolute https URL, like https://api.example.com.");
            if (string.IsNullOrEmpty(lightningAddress) || !lightningAddress.Contains('@'))
                return LocalRefusal("invalid_lightning_address",
                    "lightningAddress is required, as name@domain from a wallet provider that supports Lightning Addresses.");
            var email = string.IsNullOrWhiteSpace(recoveryEmail) ? null : recoveryEmail.Trim();
            var pubkey = string.IsNullOrWhiteSpace(recoveryNostrPubkey) ? null : recoveryNostrPubkey.Trim();
            if (email != null && pubkey != null)
                return LocalRefusal("invalid_recovery", "Give recoveryEmail or recoveryNostrPubkey, not both.");
            if (priceSats < 0)
                return LocalRefusal("invalid_price", "priceSats must be positive; omit it for the server default.");
            if (claimClient == null)
                return Failure("not_available", "The producer claim client is not available on this server.", stage: "setup");

            var serviceName = string.IsNullOrWhiteSpace(name) ? target.Host : name.Trim();
            var configPath = options.ConfigPath ?? configService?.ConfigFilePath ?? DefaultConfigPath();
            var stateDir = Path.Combine(Path.GetDirectoryName(configPath) ?? ".", "claim");

            // ── Resume: a saved challenge for this exact body skips phase 1. ───────────
            string? body = null;
            if (!string.IsNullOrWhiteSpace(acceptTerms))
            {
                body = BuildBody(targetUrl, serviceName, description, priceSats, lightningAddress, email, pubkey,
                    listPublicly, acceptTerms.Trim());
                var saved = ClaimState.Load(stateDir, body);
                if (saved != null)
                {
                    if (saved.Phase == ClaimState.Paid)
                    {
                        return await RedeemAsync(saved, claimClient, configPath, options, resumed: true, cancellationToken);
                    }
                    if (saved.Phase == ClaimState.Paying || !saved.IsExpired(options.UtcNow()))
                    {
                        return await PayThenRedeemAsync(saved, confirmationNonce, claimClient, l402Client,
                            budgetService, priceService, paymentHistory, server, configPath, options, cancellationToken);
                    }
                    saved.Delete(); // an unpaid, expired challenge is worth nothing
                }
            }

            // ── 1. The quote (free). ───────────────────────────────────────────────────
            ClaimHttpResponse quoteResponse;
            try
            {
                quoteResponse = await claimClient.GetQuoteAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return ProblemResult(ProducerClaimErrors.NetworkError, 0, null, null, null, "quote", paid: false);
            }
            if (quoteResponse.Status != 200)
                return HttpProblem(quoteResponse, "quote", paid: false);

            var quote = ParseObject(quoteResponse.Body);
            if (quote == null)
                return Failure("invalid_quote", "The claim quote was not valid JSON.", stage: "quote");
            if (quote["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var isEnabled) && !isEnabled)
                return ProblemResult("claim_unavailable", 200, null, null, null, "quote", paid: false);

            var termsVersion = GetString(quote["terms"]?["version"]);
            var quotedSats = GetLong(quote["price"]?["sats"]);
            if (string.IsNullOrEmpty(termsVersion) || quotedSats <= 0)
                return Failure("invalid_quote", "The claim quote has no terms version or price.", stage: "quote");

            if (string.IsNullOrWhiteSpace(acceptTerms) || !string.Equals(acceptTerms.Trim(), termsVersion, StringComparison.Ordinal))
            {
                return QuoteResult(quote, termsVersion, acceptTerms, serviceName);
            }

            // ── 2. Phase 1: the 402. ───────────────────────────────────────────────────
            if (l402Client == null)
            {
                return Failure("no_wallet",
                    "No spending wallet is configured, so the claim cannot be paid. Configure LND, NWC, or Strike "
                    + "(setup_wallet), then call sell_url again. Nothing was minted or charged.", stage: "phase1");
            }

            ClaimHttpResponse challengeResponse;
            try
            {
                challengeResponse = await claimClient.PostAsync(body!, null, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return ProblemResult(ProducerClaimErrors.NetworkError, 0, null, null, null, "phase1", paid: false);
            }

            var challenge = challengeResponse.Status == 402 ? ParseObject(challengeResponse.Body) : null;
            var invoice = GetString(challenge?["invoice"]);
            var macaroon = GetString(challenge?["macaroon"]);
            if (challengeResponse.Status != 402 || string.IsNullOrEmpty(invoice) || string.IsNullOrEmpty(macaroon))
                return HttpProblem(challengeResponse, "phase1", paid: false);

            var challengeSats = GetLong(challenge!["priceSats"]);
            if (challengeSats <= 0 || challengeSats > quotedSats)
            {
                return Failure("price_changed",
                    $"The claim invoice asks for {challengeSats:N0} sats but the quote said {quotedSats:N0}. Nothing "
                    + "was paid. Show the operator the new figure and call sell_url again if they agree.",
                    stage: "phase1", extra: new JsonObject { ["quotedSats"] = quotedSats, ["invoiceSats"] = challengeSats });
            }

            var state = new ClaimState(stateDir, body!)
            {
                ClaimUrl = claimClient.ClaimUrl,
                Invoice = invoice,
                Macaroon = macaroon,
                PaymentHash = GetString(challenge["paymentHash"]),
                ExpiresAt = GetString(challenge["expiresAt"]) ?? challenge["expiresAt"]?.ToString(),
                PriceSats = challengeSats,
                Phase = ClaimState.Challenge,
            };
            state.Save(); // before paying: a crash from here on is recoverable

            return await PayThenRedeemAsync(state, confirmationNonce, claimClient, l402Client, budgetService,
                priceService, paymentHistory, server, configPath, options, cancellationToken);
        }
        catch (Exception ex)
        {
            return Failure("sell_url_failed", $"sell_url failed: {CreateAccountTool.Scrub(ex.Message)}", stage: "unknown");
        }
    }

    // ── Pay ──────────────────────────────────────────────────────────────────────────

    private static async Task<string> PayThenRedeemAsync(
        ClaimState state, string? confirmationNonce, IProducerClaimClient claimClient,
        IL402HttpClient? l402Client, IBudgetService? budgetService, IPriceService? priceService,
        IPaymentHistoryService? paymentHistory, McpServer? server, string configPath, SellUrlOptions options,
        CancellationToken cancellationToken)
    {
        if (l402Client == null)
        {
            return Failure("no_wallet",
                "No spending wallet is configured, so the claim cannot be paid. Configure LND, NWC, or Strike "
                + "(setup_wallet), then call sell_url again with the same arguments.", stage: "payment");
        }

        var wasPaying = state.Phase == ClaimState.Paying;
        state.Phase = ClaimState.Paying;
        state.Save();

        // The SAME path pay_l402_challenge runs: budget check, out-of-band confirmation through
        // RequestConfirmationAsync, the atomic reservation, receipts and history. The 402's
        // priceSats is the ceiling, so the budget is checked against it.
        var payJson = await PayL402ChallengeTool.PayL402Challenge(
            invoice: state.Invoice,
            macaroon: state.Macaroon,
            maxSats: (int)Math.Min(state.PriceSats, int.MaxValue),
            confirmationNonce: confirmationNonce,
            challengeHeader: null,
            server: server,
            l402Client: l402Client,
            budgetService: budgetService,
            priceService: priceService,
            paymentHistory: paymentHistory,
            cancellationToken: cancellationToken);

        var pay = ParseObject(payJson) ?? new JsonObject();
        if (GetBool(pay["success"]))
        {
            var token = GetString(pay["l402Token"]) ?? string.Empty;
            var prefix = state.Macaroon + ":";
            state.Preimage = token.StartsWith(prefix, StringComparison.Ordinal)
                ? token[prefix.Length..]
                : token[(token.LastIndexOf(':') + 1)..];
            state.Phase = ClaimState.Paid;
            state.Save(); // the preimage first, before anything else can fail
            return await RedeemAsync(state, claimClient, configPath, options, resumed: false, cancellationToken);
        }

        if (GetBool(pay["requiresConfirmation"]))
        {
            state.Phase = ClaimState.Challenge;
            state.Save();
            return JsonSerializer.Serialize(new JsonObject
            {
                ["success"] = false,
                ["action"] = "sell_url",
                ["stage"] = "payment",
                ["requiresConfirmation"] = true,
                ["confirmationChannel"] = pay["confirmationChannel"]?.DeepClone(),
                ["error"] = "The claim payment requires human confirmation",
                ["message"] = pay["message"]?.DeepClone(),
                ["howToConfirm"] =
                    "Ask the human operator for the confirmation code, then call l402_producer(action=\"sell_url\") "
                    + "again with exactly the same arguments plus confirmationNonce=\"<code-from-human>\".",
                ["expiresInSeconds"] = pay["expiresInSeconds"]?.DeepClone(),
                ["amount"] = pay["amount"]?.DeepClone(),
                ["paid"] = false,
            }, Indented);
        }

        // Did money move? receipt_written is null when nothing was paid, true/false once the
        // wallet seam saw a payment (pending, or settled without a preimage).
        var moneyMoved = pay["receipt_written"] is JsonValue;
        if (moneyMoved || wasPaying)
        {
            return JsonSerializer.Serialize(new JsonObject
            {
                ["success"] = false,
                ["action"] = "sell_url",
                ["stage"] = "payment",
                ["error"] = moneyMoved ? "payment_unproven" : "payment_state_unknown",
                ["message"] = moneyMoved
                    ? "The claim payment left the wallet (or is in flight), but the wallet returned no proof of payment, "
                      + "so the claim cannot be redeemed yet. Do NOT pay again."
                    : "An earlier sell_url call started paying this claim and did not finish, and the wallet now refuses "
                      + "the invoice. It may have been paid. Do NOT pay again.",
                ["detail"] = CreateAccountTool.Scrub(pay["error"]?.ToString(), 300),
                ["retryable"] = false,
                ["paid"] = true,
                ["recovery"] = RecoveryBlock(state),
            }, Indented);
        }

        // Refused before the wallet was asked: budget, ceiling, approval channel. Nothing was spent.
        state.Phase = ClaimState.Challenge;
        state.Save();
        var refused = new JsonObject
        {
            ["success"] = false,
            ["action"] = "sell_url",
            ["stage"] = "payment",
            ["error"] = "payment_refused",
            ["message"] = "The claim was not paid: " + CreateAccountTool.Scrub(pay["error"]?.ToString(), 300)
                          + " Nothing was charged.",
            ["retryable"] = false,
            ["paid"] = false,
            ["priceSats"] = state.PriceSats,
        };
        if (pay["budget"] is JsonNode budget)
            refused["budget"] = budget.DeepClone();
        if (pay["confirmationChannel"] is JsonNode cc)
            refused["confirmationChannel"] = cc.DeepClone();
        return JsonSerializer.Serialize(refused, Indented);
    }

    // ── Phase 2 ──────────────────────────────────────────────────────────────────────

    private static async Task<string> RedeemAsync(
        ClaimState state, IProducerClaimClient claimClient, string configPath, SellUrlOptions options, bool resumed,
        CancellationToken cancellationToken)
    {
        var authorization = $"L402 {state.Macaroon}:{state.Preimage}";
        var code = ProducerClaimErrors.NetworkError;
        var status = 0;
        JsonObject? problem = null;
        int? retryAfter = null;

        for (var attempt = 1; attempt <= MaxRedeemAttempts; attempt++)
        {
            ClaimHttpResponse? response = null;
            try
            {
                response = await claimClient.PostAsync(state.Body, authorization, cancellationToken);
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
            {
                code = ProducerClaimErrors.NetworkError;
                status = 0;
                problem = null;
                retryAfter = null;
            }

            if (response != null)
            {
                if (response.Status == 200 && ParseObject(response.Body) is JsonObject claimed)
                {
                    return SuccessResult(claimed, state, configPath, options, resumed);
                }
                status = response.Status;
                problem = ParseObject(response.Body);
                code = response.Status == 200 ? "claim_failed" : CodeFor(response.Status, problem);
                retryAfter = response.RetryAfterSeconds;
            }

            var retry = ProducerClaimErrors.Describe(code, status).Retryable
                        || ProducerClaimErrors.RetryAfterPaymentAlso.Contains(code);
            if (!retry || attempt == MaxRedeemAttempts)
            {
                return ProblemResult(code, status, problem, retryAfter, state, "phase2", paid: true, attempts: attempt);
            }
            await options.Delay(BackoffFor(attempt, retryAfter), cancellationToken);
        }

        return ProblemResult(code, status, problem, retryAfter, state, "phase2", paid: true, attempts: MaxRedeemAttempts);
    }

    private static TimeSpan BackoffFor(int attempt, int? retryAfter) =>
        TimeSpan.FromSeconds(retryAfter is int s
            ? Math.Clamp(s, 1, MaxRetryDelaySeconds)
            : Math.Min(1 << (attempt - 1), MaxRetryDelaySeconds));

    private static string SuccessResult(
        JsonObject claimed, ClaimState state, string configPath, SellUrlOptions options, bool resumed)
    {
        var proxy = claimed["proxy"] as JsonObject;
        var proxyId = GetString(proxy?["proxyId"]) ?? "claim";
        var replayed = GetBool(claimed["replayed"]);
        var keyRotated = GetBool(claimed["keyRotated"]);
        var apiKey = GetString(claimed["apiKey"]);
        var recovery = GetString(claimed["recovery"]);

        var result = new JsonObject
        {
            ["success"] = true,
            ["action"] = "sell_url",
            ["replayed"] = replayed,
            ["keyRotated"] = keyRotated,
            ["proxyUrl"] = proxy?["url"]?.DeepClone(),
            ["manifestUrl"] = proxy?["manifest"]?.DeepClone(),
            ["openapiUrl"] = proxy?["openapi"]?.DeepClone(),
            ["proxyId"] = proxyId,
            ["proxyActive"] = proxy?["active"]?.DeepClone(),
            ["receive"] = claimed["receive"]?.DeepClone(),
            ["paidThrough"] = claimed["paidThrough"]?.DeepClone(),
            ["renew"] = claimed["renew"]?.DeepClone(),
            ["recovery"] = recovery,
            ["listing"] = claimed["listing"]?.DeepClone(),
            ["warnings"] = claimed["warnings"]?.DeepClone() ?? new JsonArray(),
            ["claimPaidSats"] = state.PriceSats,
        };

        if (!string.IsNullOrEmpty(apiKey))
        {
            var stored = StoreApiKey(configPath, proxyId, apiKey, options);
            result["apiKey"] = apiKey;
            result["apiKeyStorage"] = new JsonObject
            {
                ["written"] = stored.Written,
                ["location"] = stored.Location,
                ["path"] = stored.Path,
                ["error"] = stored.Error,
            };
            var only = recovery == "none" ? " With no recovery binding it is the account's only credential." : "";
            var rotated = keyRotated ? " This is a fresh key: any earlier key for this account no longer works." : "";
            result["apiKeyNote"] = stored.Written
                ? stored.Location == "config"
                    ? $"This API key is shown once, here. It is stored in {stored.Path} as {ConfigKey}; restart the MCP "
                      + $"server so l402_producer uses it.{only}{rotated}"
                    : $"This API key is shown once, here. Another account's key is already configured, so this one was "
                      + $"stored in {stored.Path} (readable only by you) instead of config.json. Set "
                      + $"LIGHTNING_ENABLE_API_KEY to it to manage this proxy.{only}{rotated}"
                : $"This API key is shown once, here, and could not be saved ({stored.Error}). Save it now.{only}{rotated}";
        }
        else if (replayed)
        {
            result["apiKeyNote"] = resumed
                ? "This claim had already been redeemed, so the server does not return the API key again. If the "
                  + "first answer was lost, recover the account with the recovery binding, or contact support with the "
                  + "payment hash."
                : "The server returned the stored claim without the API key: only the first answer carries it.";
            result["paymentHash"] = state.PaymentHash;
        }

        state.Delete();
        result["message"] = "The URL is now a paid L402 endpoint. Share proxyUrl; buyers pay per call to the "
                            + "Lightning Address, and agents find the price at manifestUrl.";
        return JsonSerializer.Serialize(result, Indented);
    }

    // ── API key storage ──────────────────────────────────────────────────────────────

    internal sealed record KeyStorage(bool Written, string Location, string Path, string? Error);

    /// <summary>
    /// Empty config slot (and no key in the environment): merge into config.json the way
    /// <c>create_lightning_enable_account</c> does. Otherwise another account owns the slot, so
    /// write <c>claim-&lt;proxyId&gt;.key</c> beside it, 0600.
    /// </summary>
    internal static KeyStorage StoreApiKey(string configPath, string proxyId, string apiKey, SellUrlOptions options)
    {
        var existing = options.EnvironmentApiKey();
        if (string.IsNullOrWhiteSpace(existing) || existing.StartsWith("${", StringComparison.Ordinal))
        {
            existing = ReadConfigKey(configPath);
        }

        if (string.IsNullOrWhiteSpace(existing) || existing == apiKey)
        {
            var (ok, _) = CreateAccountTool.MergeApiKeyIntoConfig(configPath, apiKey);
            if (ok)
                return new KeyStorage(true, "config", configPath, null);
            // An unparseable config is never overwritten; fall through to the key file.
        }

        var safeId = new string(proxyId.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? char.ToLowerInvariant(c) : '-').ToArray());
        var keyPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(configPath) ?? ".", $"claim-{safeId}.key");
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(keyPath)!);
            File.WriteAllText(keyPath, apiKey + "\n");
            try { BudgetConfigurationService.RestrictFilePermissions(keyPath); } catch { /* best effort */ }
            return new KeyStorage(true, "key_file", keyPath, null);
        }
        catch (Exception ex)
        {
            return new KeyStorage(false, "none", keyPath, CreateAccountTool.Scrub(ex.Message));
        }
    }

    private static string? ReadConfigKey(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return null;
            return JsonNode.Parse(File.ReadAllText(configPath)) is JsonObject root
                ? GetString(root[ConfigKey])
                : null;
        }
        catch
        {
            return null;
        }
    }

    // ── Results ──────────────────────────────────────────────────────────────────────

    private static string QuoteResult(JsonObject quote, string termsVersion, string? acceptTerms, string serviceName)
    {
        var mismatch = !string.IsNullOrWhiteSpace(acceptTerms);
        return JsonSerializer.Serialize(new JsonObject
        {
            ["success"] = true,
            ["action"] = "sell_url",
            ["stage"] = "quote",
            ["requiresTermsAcceptance"] = true,
            ["paid"] = false,
            ["price"] = quote["price"]?.DeepClone(),
            ["covers"] = quote["covers"]?.DeepClone(),
            ["renewal"] = quote["renewal"]?.DeepClone(),
            ["limits"] = quote["limits"]?.DeepClone(),
            ["terms"] = quote["terms"]?.DeepClone(),
            ["listing"] = quote["listing"]?.DeepClone(),
            ["name"] = serviceName,
            ["bodyRule"] = BodyRule,
            ["message"] = (mismatch
                              ? $"acceptTerms \"{acceptTerms!.Trim()}\" is not the current terms version. "
                              : "")
                          + "Nothing was sent or charged. Show the operator the claim price, what it covers, the renewal, "
                          + "and the terms link, and get an explicit yes before paying.",
            ["nextStep"] = $"Call l402_producer(action=\"sell_url\") again with the same arguments plus acceptTerms=\"{termsVersion}\".",
        }, Indented);
    }

    private static string HttpProblem(ClaimHttpResponse response, string stage, bool paid)
    {
        var problem = ParseObject(response.Body);
        return ProblemResult(CodeFor(response.Status, problem), response.Status, problem, response.RetryAfterSeconds,
            null, stage, paid);
    }

    private static string CodeFor(int status, JsonObject? problem)
    {
        var code = GetString(problem?["error"]);
        if (!string.IsNullOrEmpty(code)) return code;
        return status switch
        {
            404 => ProducerClaimErrors.FeatureNotEnabled,
            429 => ProducerClaimErrors.RateLimited,
            0 => ProducerClaimErrors.NetworkError,
            _ => $"http_{status}",
        };
    }

    private static readonly HashSet<string> ProblemCoreMembers = new(StringComparer.Ordinal)
    {
        "type", "title", "status", "detail", "error", "message", "instance", "traceId",
        // Never echo a challenge's secrets, should a 402 body ever reach this path.
        "macaroon", "invoice", "preimage", "apiKey",
    };

    private static string ProblemResult(
        string code, int status, JsonObject? problem, int? retryAfter, ClaimState? state, string stage, bool paid,
        int? attempts = null)
    {
        var info = ProducerClaimErrors.Describe(code, status);
        var result = new JsonObject
        {
            ["success"] = false,
            ["action"] = "sell_url",
            ["stage"] = stage,
            ["error"] = code,
            ["message"] = info.Message,
            ["retryable"] = info.Retryable,
            ["paid"] = paid,
        };
        if (status > 0) result["httpStatus"] = status;
        var detail = GetString(problem?["detail"]) ?? GetString(problem?["message"]);
        if (!string.IsNullOrEmpty(detail)) result["detail"] = CreateAccountTool.Scrub(detail, 300);
        if (retryAfter is int ra) result["retryAfterSeconds"] = ra;
        if (attempts is int n) result["redeemAttempts"] = n;

        if (problem != null)
        {
            var extras = new JsonObject();
            foreach (var (key, value) in problem)
            {
                if (!ProblemCoreMembers.Contains(key) && value is not null)
                    extras[key] = value.DeepClone();
            }
            if (extras.Count > 0) result["problem"] = extras;
        }

        if (paid && state != null)
        {
            result["recovery"] = RecoveryBlock(state);
        }
        else if (!paid)
        {
            result["note"] = "A refusal before payment costs nothing. Fix the named field and call sell_url again.";
        }
        return JsonSerializer.Serialize(result, Indented);
    }

    private static JsonObject RecoveryBlock(ClaimState state) => new()
    {
        ["paymentSpent"] = true,
        ["paymentHash"] = state.PaymentHash,
        ["stateFile"] = state.FilePath,
        ["tokenExpiresAt"] = state.ExpiresAt,
        ["instructions"] =
            "The claim payment is spent; do not pay again. The saved file holds the exact claim body and the paid token "
            + "(macaroon and preimage). Do not edit it and never paste its contents into chat. To retry, call "
            + "l402_producer(action=\"sell_url\") again with exactly the same arguments before the token expires; the tool "
            + $"skips the payment and redeems the saved token. By hand: POST the file's \"body\" byte for byte to {state.ClaimUrl} "
            + "with Content-Type: application/json and Authorization: L402 <macaroon>:<preimage> taken from the file. If it "
            + "keeps failing, contact support@lightningenable.com with the payment hash only.",
    };

    private static string LocalRefusal(string code, string message) =>
        JsonSerializer.Serialize(new JsonObject
        {
            ["success"] = false,
            ["action"] = "sell_url",
            ["stage"] = "input",
            ["error"] = code,
            ["message"] = message + " Nothing was sent or charged.",
            ["retryable"] = false,
            ["paid"] = false,
        }, Indented);

    private static string Failure(string code, string message, string stage, JsonObject? extra = null)
    {
        var result = new JsonObject
        {
            ["success"] = false,
            ["action"] = "sell_url",
            ["stage"] = stage,
            ["error"] = code,
            ["message"] = message,
            ["retryable"] = false,
            ["paid"] = false,
        };
        if (extra != null)
        {
            foreach (var (k, v) in extra) result[k] = v?.DeepClone();
        }
        return JsonSerializer.Serialize(result, Indented);
    }

    // ── Body and JSON helpers ────────────────────────────────────────────────────────

    /// <summary>The claim body, field order as the skill writes it. Serialized ONCE.</summary>
    internal static string BuildBody(
        string targetUrl, string name, string? description, long priceSats, string lightningAddress,
        string? email, string? pubkey, bool listPublicly, string acceptTerms)
    {
        var body = new JsonObject
        {
            ["targetUrl"] = targetUrl,
            ["name"] = name,
        };
        if (!string.IsNullOrWhiteSpace(description)) body["description"] = description.Trim();
        if (priceSats > 0) body["defaultPriceSats"] = priceSats;
        body["receive"] = new JsonObject { ["lightningAddress"] = lightningAddress };
        if (email != null) body["recovery"] = new JsonObject { ["email"] = email };
        else if (pubkey != null) body["recovery"] = new JsonObject { ["nostrPubkey"] = pubkey };
        body["listPublicly"] = listPublicly;
        body["acceptTerms"] = acceptTerms;
        return body.ToJsonString();
    }

    private static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long GetLong(JsonNode? node) =>
        node is JsonValue v
            ? v.TryGetValue<long>(out var l) ? l
            : v.TryGetValue<double>(out var d) ? (long)d
            : v.TryGetValue<string>(out var s) && long.TryParse(s, out var p) ? p : 0
            : 0;

    private static bool GetBool(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static string DefaultConfigPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lightning-enable", "config.json");

    // ── Scratch state ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The claim in flight, one file per exact body: <c>claim/sell-&lt;sha256(body)[:16]&gt;.json</c>,
    /// 0600. Holds the body and the challenge from before payment, and the preimage from the
    /// moment payment settles, so a crash anywhere after paying is recoverable.
    /// </summary>
    internal sealed class ClaimState
    {
        public const string Challenge = "challenge";
        public const string Paying = "paying";
        public const string Paid = "paid";

        public ClaimState(string directory, string body)
        {
            Body = body;
            FilePath = Path.Combine(directory, FileNameFor(body));
        }

        public string Body { get; }
        public string FilePath { get; }
        public string? ClaimUrl { get; set; }
        public string? Invoice { get; set; }
        public string? Macaroon { get; set; }
        public string? PaymentHash { get; set; }
        public string? ExpiresAt { get; set; }
        public long PriceSats { get; set; }
        public string Phase { get; set; } = Challenge;
        public string? Preimage { get; set; }

        public static string FileNameFor(string body)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            return $"sell-{hash[..16]}.json";
        }

        public bool IsExpired(DateTimeOffset now) =>
            DateTimeOffset.TryParse(ExpiresAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var expires) && expires <= now;

        public static ClaimState? Load(string directory, string body)
        {
            var path = Path.Combine(directory, FileNameFor(body));
            if (!File.Exists(path)) return null;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) return null;
                // Same file name but a different body would be a hash collision; refuse it.
                if (GetString(o["body"]) != body) return null;
                return new ClaimState(directory, body)
                {
                    ClaimUrl = GetString(o["claimUrl"]),
                    Invoice = GetString(o["invoice"]),
                    Macaroon = GetString(o["macaroon"]),
                    PaymentHash = GetString(o["paymentHash"]),
                    ExpiresAt = GetString(o["expiresAt"]),
                    PriceSats = GetLong(o["priceSats"]),
                    Phase = GetString(o["phase"]) ?? Challenge,
                    Preimage = GetString(o["preimage"]),
                };
            }
            catch
            {
                return null;
            }
        }

        public void Save()
        {
            var directory = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(directory))
            {
                if (OperatingSystem.IsWindows())
                    Directory.CreateDirectory(directory);
                else
                    Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var o = new JsonObject
            {
                ["version"] = 1,
                ["claimUrl"] = ClaimUrl,
                ["body"] = Body,
                ["invoice"] = Invoice,
                ["macaroon"] = Macaroon,
                ["paymentHash"] = PaymentHash,
                ["expiresAt"] = ExpiresAt,
                ["priceSats"] = PriceSats,
                ["phase"] = Phase,
            };
            if (Preimage != null) o["preimage"] = Preimage;

            // Write-then-rename so a crash mid-write never leaves a torn file. Tighten the
            // temp file BEFORE the secrets matter: it is created, restricted, then filled.
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, string.Empty);
            try { BudgetConfigurationService.RestrictFilePermissions(tmp); } catch { /* best effort */ }
            File.WriteAllText(tmp, o.ToJsonString(Indented));
            File.Move(tmp, FilePath, overwrite: true);
        }

        public void Delete()
        {
            try { File.Delete(FilePath); } catch { /* best effort */ }
        }
    }
}

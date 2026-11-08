using System.Collections.Concurrent;
using System.Globalization;
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
    /// <c>merchant_exists</c> after payment: the server checks the recovery email only in phase 2
    /// (spec §17.3). Changing the arguments would be a new claim and a new payment.
    /// </summary>
    public const string MerchantExistsAfterPayment =
        "The claim payment is spent, and an account already uses that recovery email. Do not change the "
        + "arguments or claim again: a different body is a new claim and a new payment. Contact "
        + "support@lightningenable.com with the payment hash.";

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
/// <para><b>State.</b> One file per exact body, <c>&lt;config dir&gt;/claim/sell-&lt;hash&gt;.json</c>
/// (0600), whose phase only moves forward: <c>challenge</c> (saved before paying) →
/// <c>paying</c> (the wallet may be asked) → <c>paid</c> (preimage saved) → <c>redeemed</c>
/// (a secret-free marker). The only step back is <c>paying</c> → <c>challenge</c>, and only
/// when the pay path refused before the wallet was asked and the operation ledger has no
/// record of the invoice. Only a <c>challenge</c> file may expire. Calls for the same body are
/// serialized by a per-file lock, and every save re-reads the file and refuses to move it
/// backwards.</para>
///
/// <para><b>Money safety.</b> "Nothing was charged" is said only for refusals known to happen
/// before the wallet. Any other failure after the wallet may have been asked is
/// <c>payment_state_unknown</c> with <c>paid: "unknown"</c>, never paid again by this tool.</para>
///
/// <para><b>Secrets.</b> The macaroon and preimage never enter a tool result; the API key
/// enters exactly one result per issue, after it has been stored.</para>
/// </summary>
internal static class ProducerSellUrl
{
    internal const int MaxRedeemAttempts = 4;
    private const string ConfigKey = "lightningEnableApiKey";
    private const int MaxRetryDelaySeconds = 10;

    /// <summary>Used only when the server's quote carries no <c>bodyRule</c> of its own.</summary>
    private const string FallbackBodyRule =
        "The claim body is written once and sent twice: the payment is bound to the target URL, the Lightning "
        + "Address, and the recovery binding. Changing any of them after paying makes the payment unusable, so "
        + "call sell_url again with exactly the same arguments.";

    internal const string RecoveryEmailWarning =
        "An email that already belongs to a Lightning Enable account is refused only after payment "
        + "(merchant_exists), and the paid claim cannot be reused for a different body. Use an email that is "
        + "not registered yet, a Nostr public key, or no recovery.";

    internal const string ReplayRotatedNote =
        "This claim was replayed, so the server rotated the account's API key: the key returned here is now the "
        + "only valid one, and any earlier key for this account no longer works.";

    internal const string ReplayWithoutKeyNote =
        "The server answered this replay without an API key. A replay normally rotates the key and returns the new "
        + "one; if the account's key is lost, recover it with the recovery binding, or contact support with the "
        + "payment hash.";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Serializes calls for the same claim body within this process.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<string> SellUrlAsync(
        string? targetUrl,
        string? lightningAddress,
        long? defaultPriceSats,
        string? name,
        string? description,
        string? recoveryEmail,
        string? recoveryNostrPubkey,
        bool listPublicly,
        string? acceptTerms,
        string? confirmationNonce,
        IProducerClaimClient? claimClient,
        IL402HttpClient? l402Client,
        IWalletService? walletService,
        IOperationLedger? ledger,
        IBudgetService? budgetService,
        IPriceService? priceService,
        IPaymentHistoryService? paymentHistory,
        IBudgetConfigurationService? configService,
        McpServer? server,
        SellUrlOptions? options,
        CancellationToken cancellationToken)
    {
        options ??= SellUrlOptions.Default;

        // ── Local checks: nothing is sent, nothing is minted. ──────────────────────────
        targetUrl = targetUrl?.Trim();
        lightningAddress = lightningAddress?.Trim();
        if (string.IsNullOrEmpty(targetUrl))
            return LocalRefusal("invalid_target_url", "targetUrl is required: the https URL of the API to sell.");
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var target)
            || target.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(target.Host))
            return LocalRefusal("invalid_target_url", "targetUrl must be an absolute https URL, like https://api.example.com.");
        if (string.IsNullOrEmpty(lightningAddress) || !lightningAddress.Contains('@'))
            return LocalRefusal("invalid_lightning_address",
                "lightningAddress is required, as name@domain from a wallet provider that supports Lightning Addresses.");
        var email = string.IsNullOrWhiteSpace(recoveryEmail) ? null : recoveryEmail.Trim();
        var pubkey = string.IsNullOrWhiteSpace(recoveryNostrPubkey) ? null : recoveryNostrPubkey.Trim();
        if (email != null && pubkey != null)
            return LocalRefusal("invalid_recovery", "Give recoveryEmail or recoveryNostrPubkey, not both.");
        if (defaultPriceSats is < 0)
            return LocalRefusal("invalid_price", "defaultPriceSats must be positive; omit it for the server default.");
        if (claimClient == null)
            return Failure("not_available", "The producer claim client is not available on this server.", stage: "setup");

        var ctx = new Ctx
        {
            ClaimClient = claimClient,
            L402Client = l402Client,
            // IL402HttpClient is always registered, so the wallet itself says whether it can pay.
            HasWallet = l402Client != null && walletService?.IsConfigured != false,
            Ledger = ledger,
            BudgetService = budgetService,
            PriceService = priceService,
            PaymentHistory = paymentHistory,
            Server = server,
            Options = options,
            ConfigPath = options.ConfigPath ?? configService?.ConfigFilePath ?? DefaultConfigPath(),
            ConfirmationNonce = confirmationNonce,
            RecoveryEmail = email,
        };
        var stateDir = Path.Combine(Path.GetDirectoryName(ctx.ConfigPath) ?? ".", "claim");
        var serviceName = string.IsNullOrWhiteSpace(name) ? target.Host : name.Trim();

        string? body = null;
        SemaphoreSlim? gate = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(acceptTerms))
            {
                body = BuildBody(targetUrl, serviceName, description, defaultPriceSats, lightningAddress, email, pubkey,
                    listPublicly, acceptTerms.Trim());
                var bodyLock = Locks.GetOrAdd(Path.GetFullPath(Path.Combine(stateDir, ClaimState.FileNameFor(body))),
                    _ => new SemaphoreSlim(1, 1));
                await bodyLock.WaitAsync(cancellationToken);
                gate = bodyLock; // released in finally only once actually held
            }

            return await RunAsync(ctx, stateDir, body, acceptTerms, serviceName, cancellationToken);
        }
        catch (Exception ex)
        {
            // Whatever escaped, the file says whether money may have moved.
            var saved = body == null ? null : ClaimState.Load(stateDir, body);
            if (saved?.Phase == ClaimState.Paid)
                return Paid(saved, "sell_url_failed",
                    $"sell_url stopped after the claim was paid: {CreateAccountTool.Scrub(ex.Message)}", paid: true);
            if (saved?.Phase == ClaimState.Paying)
                return Unknown(saved, CreateAccountTool.Scrub(ex.Message, 300));
            if (saved?.Phase == ClaimState.Redeemed)
                return AlreadyClaimed(saved);
            return Failure("sell_url_failed", $"sell_url failed: {CreateAccountTool.Scrub(ex.Message)}", stage: "unknown");
        }
        finally
        {
            gate?.Release();
        }
    }

    private sealed class Ctx
    {
        public required IProducerClaimClient ClaimClient { get; init; }
        public IL402HttpClient? L402Client { get; init; }
        public bool HasWallet { get; init; }
        public IOperationLedger? Ledger { get; init; }
        public IBudgetService? BudgetService { get; init; }
        public IPriceService? PriceService { get; init; }
        public IPaymentHistoryService? PaymentHistory { get; init; }
        public McpServer? Server { get; init; }
        public required SellUrlOptions Options { get; init; }
        public required string ConfigPath { get; init; }
        public string? ConfirmationNonce { get; init; }
        public string? RecoveryEmail { get; init; }
    }

    private static async Task<string> RunAsync(
        Ctx ctx, string stateDir, string? body, string? acceptTerms, string serviceName, CancellationToken ct)
    {
        // ── Resume: the saved state for this exact body decides. ─────────────────────────
        if (body != null && ClaimState.Load(stateDir, body) is { } saved)
        {
            switch (saved.Phase)
            {
                case ClaimState.Redeemed:
                    return AlreadyClaimed(saved);
                case ClaimState.Paid:
                    return await RedeemAsync(saved, ctx, resumed: true, ct);
                case ClaimState.Paying:
                    // The wallet may have been asked. Only the ledger can prove it was not.
                    if (ctx.Ledger != null && !LedgerKnows(ctx.Ledger, saved.Invoice))
                    {
                        saved.Phase = ClaimState.Challenge;
                        saved.Save(allowDowngradeFromPaying: true);
                        return await PayThenRedeemAsync(saved, ctx, ct);
                    }
                    return Unknown(saved, null);
                default:
                    if (!saved.IsExpired(ctx.Options.UtcNow()))
                        return await PayThenRedeemAsync(saved, ctx, ct);
                    saved.Delete(); // a challenge the wallet never saw, and expired: worth nothing
                    break;
            }
        }

        // ── 1. The quote (free). ───────────────────────────────────────────────────────
        ClaimHttpResponse quoteResponse;
        try
        {
            quoteResponse = await ctx.ClaimClient.GetQuoteAsync(ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            return ProblemResult(ProducerClaimErrors.NetworkError, 0, null, null, null, "quote", paid: false);
        }
        if (quoteResponse.Status != 200)
            return HttpProblem(quoteResponse, "quote");

        var quote = ParseObject(quoteResponse.Body);
        if (quote == null)
            return Failure("invalid_quote", "The claim quote was not valid JSON.", stage: "quote");
        if (quote["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var isEnabled) && !isEnabled)
            return ProblemResult("claim_unavailable", 200, null, null, null, "quote", paid: false);

        var termsVersion = GetString(quote["terms"]?["version"]);
        var quotedSats = GetLong(quote["price"]?["sats"]);
        if (string.IsNullOrEmpty(termsVersion) || quotedSats <= 0)
            return Failure("invalid_quote", "The claim quote has no terms version or price.", stage: "quote");

        if (body == null || !string.Equals(acceptTerms!.Trim(), termsVersion, StringComparison.Ordinal))
        {
            return QuoteResult(quote, termsVersion, acceptTerms, serviceName, ctx.RecoveryEmail != null);
        }

        // ── 2. Phase 1: the 402. ───────────────────────────────────────────────────────
        if (!ctx.HasWallet)
            return NoWallet("phase1");

        ClaimHttpResponse challengeResponse;
        try
        {
            challengeResponse = await ctx.ClaimClient.PostAsync(body, null, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            return ProblemResult(ProducerClaimErrors.NetworkError, 0, null, null, null, "phase1", paid: false);
        }

        var challenge = challengeResponse.Status == 402 ? ParseObject(challengeResponse.Body) : null;
        var invoice = GetString(challenge?["invoice"]);
        var macaroon = GetString(challenge?["macaroon"]);
        if (challengeResponse.Status != 402 || string.IsNullOrEmpty(invoice) || string.IsNullOrEmpty(macaroon))
            return HttpProblem(challengeResponse, "phase1");

        var challengeSats = GetLong(challenge!["priceSats"]);
        if (challengeSats <= 0 || challengeSats > quotedSats)
        {
            return Failure("price_changed",
                $"The claim invoice asks for {challengeSats:N0} sats but the quote said {quotedSats:N0}. Nothing "
                + "was paid. Show the operator the new figure and call sell_url again if they agree.",
                stage: "phase1", extra: new JsonObject { ["quotedSats"] = quotedSats, ["invoiceSats"] = challengeSats });
        }

        var state = new ClaimState(stateDir, body)
        {
            ClaimUrl = ctx.ClaimClient.ClaimUrl,
            Invoice = invoice,
            Macaroon = macaroon,
            PaymentHash = GetString(challenge["paymentHash"]),
            ExpiresAt = GetString(challenge["expiresAt"]) ?? challenge["expiresAt"]?.ToString(),
            PriceSats = challengeSats,
            Phase = ClaimState.Challenge,
        };
        state.Save(); // before paying: a crash from here on is recoverable

        return await PayThenRedeemAsync(state, ctx, ct);
    }

    // ── Pay ──────────────────────────────────────────────────────────────────────────

    private static async Task<string> PayThenRedeemAsync(ClaimState state, Ctx ctx, CancellationToken ct)
    {
        if (!ctx.HasWallet)
            return NoWallet("payment");

        state.Phase = ClaimState.Paying;
        state.Save();

        // The SAME path pay_l402_challenge runs: budget check, out-of-band confirmation through
        // RequestConfirmationAsync, the atomic reservation, receipts and history. The 402's
        // priceSats is the ceiling, so the budget is checked against it.
        var payJson = await PayL402ChallengeTool.PayL402Challenge(
            invoice: state.Invoice,
            macaroon: state.Macaroon,
            maxSats: (int)Math.Min(state.PriceSats, int.MaxValue),
            confirmationNonce: ctx.ConfirmationNonce,
            challengeHeader: null,
            server: ctx.Server,
            l402Client: ctx.L402Client,
            budgetService: ctx.BudgetService,
            priceService: ctx.PriceService,
            paymentHistory: ctx.PaymentHistory,
            cancellationToken: ct);

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
            return await RedeemAsync(state, ctx, resumed: false, ct);
        }

        // Money left the wallet (or is in flight) without a proof: the wallet seam wrote a receipt.
        if (pay["receipt_written"] is JsonValue)
        {
            return Paid(state, "payment_unproven",
                "The claim payment left the wallet (or is in flight), but the wallet returned no proof of payment, "
                + "so the claim cannot be redeemed yet. Do NOT pay again.", paid: true,
                detail: CreateAccountTool.Scrub(pay["error"]?.ToString(), 300));
        }

        // Anything not provably refused before the wallet is ambiguous: keep 'paying'.
        if (!IsKnownPreWalletRefusal(pay) || LedgerKnows(ctx.Ledger, state.Invoice))
        {
            return Unknown(state, CreateAccountTool.Scrub(pay["error"]?.ToString(), 300));
        }

        state.Phase = ClaimState.Challenge;
        state.Save(allowDowngradeFromPaying: true);

        if (GetBool(pay["requiresConfirmation"]))
        {
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
            ["nextStep"] = "If a confirmation code was rejected, call sell_url again with the same arguments and no "
                           + "confirmationNonce to request a new one.",
        };
        if (pay["budget"] is JsonNode budget)
            refused["budget"] = budget.DeepClone();
        if (pay["confirmationChannel"] is JsonNode cc)
            refused["confirmationChannel"] = cc.DeepClone();
        return JsonSerializer.Serialize(refused, Indented);
    }

    /// <summary>
    /// The pay results <see cref="PayL402ChallengeTool"/> returns only BEFORE the wallet is
    /// asked: budget deny, confirmation required or refused by the channel, an invalid or
    /// expired code, an amountless or invalid invoice, the <c>maxSats</c> ceiling, the
    /// reservation refusal, and no wallet. Everything else may have reached the wallet.
    /// </summary>
    internal static bool IsKnownPreWalletRefusal(JsonObject pay)
    {
        if (pay.ContainsKey("budget") || pay.ContainsKey("requiresConfirmation"))
            return true;
        var error = GetString(pay["error"]) ?? string.Empty;
        return error.StartsWith("Confirmation code is invalid", StringComparison.Ordinal)
               || error.StartsWith("Invoice has no amount", StringComparison.Ordinal)
               || error.StartsWith("Invoice is required", StringComparison.Ordinal)
               || error.StartsWith("L402 HTTP client not available", StringComparison.Ordinal)
               || error.StartsWith("Budget check failed:", StringComparison.Ordinal)
               || error.StartsWith("NWC wallet not configured", StringComparison.Ordinal)
               || (error.StartsWith("Invoice amount ", StringComparison.Ordinal)
                   && error.Contains(" exceeds maximum ", StringComparison.Ordinal));
    }

    private static bool LedgerKnows(IOperationLedger? ledger, string? invoice) =>
        ledger != null && !string.IsNullOrEmpty(invoice)
        && ledger.Lookup(IdempotentWalletService.DeriveOperationId(invoice)) != null;

    // ── Phase 2 ──────────────────────────────────────────────────────────────────────

    private static async Task<string> RedeemAsync(ClaimState state, Ctx ctx, bool resumed, CancellationToken ct)
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
                response = await ctx.ClaimClient.PostAsync(state.Body, authorization, ct);
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
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
                    return SuccessResult(claimed, state, ctx);
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
            await ctx.Options.Delay(BackoffFor(attempt, retryAfter), ct);
        }

        return ProblemResult(code, status, problem, retryAfter, state, "phase2", paid: true, attempts: MaxRedeemAttempts);
    }

    private static TimeSpan BackoffFor(int attempt, int? retryAfter) =>
        TimeSpan.FromSeconds(retryAfter is int s
            ? Math.Clamp(s, 1, MaxRetryDelaySeconds)
            : Math.Min(1 << (attempt - 1), MaxRetryDelaySeconds));

    private static string SuccessResult(JsonObject claimed, ClaimState state, Ctx ctx)
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

        KeyStorage? stored = null;
        if (!string.IsNullOrEmpty(apiKey))
        {
            // A rotated key replaces this claim's earlier key where that one was stored: the old
            // key is dead. The target is recorded in the state file BEFORE the key is written, so
            // a crash between the two still lets the next replay find it.
            var replaceConfig = keyRotated && state.KeyLocation == "config";
            var plan = PlanKeyTarget(ctx.ConfigPath, proxyId, apiKey, ctx.Options, replaceConfig);
            state.KeyLocation = plan.Location;
            state.KeyPath = plan.Path;
            state.Save();
            stored = WriteKey(plan, ctx.ConfigPath, proxyId, apiKey);
            if (stored.Location != plan.Location)
            {
                state.KeyLocation = stored.Location;
                state.KeyPath = stored.Path;
                state.Save();
            }

            result["apiKey"] = apiKey;
            result["apiKeyStorage"] = new JsonObject
            {
                ["written"] = stored.Written,
                ["location"] = stored.Location,
                ["path"] = stored.Path,
                ["replacedRotatedKey"] = replaceConfig && stored.Location == "config",
                ["error"] = stored.Error,
            };
            var only = recovery == "none" ? " With no recovery binding it is the account's only credential." : "";
            var lead = replayed || keyRotated ? ReplayRotatedNote : "This API key is shown once, here.";
            var where = !stored.Written
                ? $" It could not be saved ({stored.Error}). Save it now."
                : stored.Location == "config"
                    ? replaceConfig
                        ? $" It replaced this claim's earlier key in {stored.Path} ({ConfigKey}); restart the MCP server so "
                          + "l402_producer uses it."
                        : $" It is stored in {stored.Path} as {ConfigKey}; restart the MCP server so l402_producer uses it."
                    : $" Another account's key is already configured, so this one was stored in {stored.Path} (readable "
                      + "only by you) instead of config.json. Set LIGHTNING_ENABLE_API_KEY to it to manage this proxy.";
            result["apiKeyNote"] = lead + where + only;
        }
        else if (replayed)
        {
            result["apiKeyNote"] = ReplayWithoutKeyNote;
            result["paymentHash"] = state.PaymentHash;
        }

        // Replace the token with a secret-free marker, so a re-call answers "already claimed"
        // instead of buying a second claim.
        state.MarkRedeemed(proxyId, GetString(proxy?["url"]), GetString(proxy?["manifest"]),
            stored is { Written: true } ? stored.Location : state.KeyLocation,
            stored is { Written: true } ? stored.Path : state.KeyPath,
            ctx.Options.UtcNow());

        result["message"] = "The URL is now a paid L402 endpoint. Share proxyUrl; buyers pay per call to the "
                            + "Lightning Address, and agents find the price at manifestUrl.";
        return JsonSerializer.Serialize(result, Indented);
    }

    // ── API key storage ──────────────────────────────────────────────────────────────

    internal sealed record KeyStorage(bool Written, string Location, string Path, string? Error);

    internal sealed record KeyTarget(string Location, string Path);

    /// <summary>
    /// Where the key goes. The config slot when it is empty (and no key is in the environment),
    /// already holds this key, or holds this claim's own key that a replay just rotated.
    /// Otherwise another account owns the slot, so <c>claim-&lt;proxyId&gt;.key</c> beside it.
    /// </summary>
    internal static KeyTarget PlanKeyTarget(
        string configPath, string proxyId, string apiKey, SellUrlOptions options, bool replaceConfig)
    {
        if (replaceConfig)
            return new KeyTarget("config", configPath);

        var existing = options.EnvironmentApiKey();
        if (string.IsNullOrWhiteSpace(existing) || existing.StartsWith("${", StringComparison.Ordinal))
            existing = ReadConfigKey(configPath);

        return string.IsNullOrWhiteSpace(existing) || existing == apiKey
            ? new KeyTarget("config", configPath)
            : new KeyTarget("key_file", KeyFilePath(configPath, proxyId));
    }

    private static string KeyFilePath(string configPath, string proxyId)
    {
        var safeId = new string(proxyId.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? char.ToLowerInvariant(c) : '-').ToArray());
        return Path.Combine(Path.GetDirectoryName(configPath) ?? ".", $"claim-{safeId}.key");
    }

    private static KeyStorage WriteKey(KeyTarget plan, string configPath, string proxyId, string apiKey)
    {
        if (plan.Location == "config")
        {
            var (ok, _) = CreateAccountTool.MergeApiKeyIntoConfig(configPath, apiKey);
            if (ok)
                return new KeyStorage(true, "config", configPath, null);
            // An unparseable config is never overwritten; fall back to the key file.
        }

        var keyPath = KeyFilePath(configPath, proxyId);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            WriteRestricted(keyPath, apiKey + "\n");
            return new KeyStorage(true, "key_file", keyPath, null);
        }
        catch (Exception ex)
        {
            return new KeyStorage(false, "none", keyPath, CreateAccountTool.Scrub(ex.Message));
        }
    }

    /// <summary>Create the file empty, restrict it to the owner, THEN write the secret.</summary>
    private static void WriteRestricted(string path, string contents)
    {
        File.WriteAllText(path, string.Empty);
        try { BudgetConfigurationService.RestrictFilePermissions(path); } catch { /* best effort */ }
        File.WriteAllText(path, contents);
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

    private static string QuoteResult(
        JsonObject quote, string termsVersion, string? acceptTerms, string serviceName, bool hasRecoveryEmail)
    {
        var mismatch = !string.IsNullOrWhiteSpace(acceptTerms);
        var result = new JsonObject
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
            // The server's own wording, verbatim, so the agent can show it without another call.
            ["pricingNote"] = quote["pricingNote"]?.DeepClone(),
            ["bodyRule"] = quote["bodyRule"]?.DeepClone() ?? FallbackBodyRule,
            ["name"] = serviceName,
            ["message"] = (mismatch
                              ? $"acceptTerms \"{acceptTerms!.Trim()}\" is not the current terms version. "
                              : "")
                          + "Nothing was sent or charged. Show the operator the claim price, what it covers, the renewal, "
                          + "and the terms link, and get an explicit yes before paying.",
            ["nextStep"] = $"Call l402_producer(action=\"sell_url\") again with the same arguments plus acceptTerms=\"{termsVersion}\".",
        };
        if (hasRecoveryEmail)
            result["recoveryEmailWarning"] = RecoveryEmailWarning;
        return JsonSerializer.Serialize(result, Indented);
    }

    private static string AlreadyClaimed(ClaimState s) =>
        JsonSerializer.Serialize(new JsonObject
        {
            ["success"] = true,
            ["action"] = "sell_url",
            ["stage"] = "already_claimed",
            ["alreadyClaimed"] = true,
            ["paidThisCall"] = false,
            ["proxyId"] = s.ProxyId,
            ["proxyUrl"] = s.ProxyUrl,
            ["manifestUrl"] = s.ManifestUrl,
            ["paymentHash"] = s.PaymentHash,
            ["apiKeyStorage"] = new JsonObject { ["location"] = s.KeyLocation, ["path"] = s.KeyPath },
            ["redeemedAt"] = s.RedeemedAt,
            ["message"] = $"This exact claim was already completed at {s.RedeemedAt}. Nothing was paid again. "
                          + (s.KeyPath != null
                              ? $"The API key was stored in {s.KeyPath} and is not shown again."
                              : "The API key was shown only in the first result.")
                          + " Selling a different URL or changing the details is a new claim and a new payment.",
        }, Indented);

    private static string Unknown(ClaimState state, string? detail)
    {
        var result = new JsonObject
        {
            ["success"] = false,
            ["action"] = "sell_url",
            ["stage"] = "payment",
            ["error"] = "payment_state_unknown",
            ["message"] =
                "The claim payment may have been made: the attempt did not finish cleanly after the wallet may have "
                + "been asked. Do NOT pay again and do not change the arguments. Check the wallet's payment history "
                + "for the payment hash, then contact support@lightningenable.com with it.",
            ["retryable"] = false,
            ["paid"] = "unknown",
            ["recovery"] = RecoveryBlock(state),
        };
        if (!string.IsNullOrEmpty(detail)) result["detail"] = detail;
        return JsonSerializer.Serialize(result, Indented);
    }

    private static string Paid(ClaimState state, string code, string message, bool paid, string? detail = null)
    {
        var result = new JsonObject
        {
            ["success"] = false,
            ["action"] = "sell_url",
            ["stage"] = state.Phase == ClaimState.Paid ? "phase2" : "payment",
            ["error"] = code,
            ["message"] = message,
            ["retryable"] = false,
            ["paid"] = paid,
            ["recovery"] = RecoveryBlock(state),
        };
        if (!string.IsNullOrEmpty(detail)) result["detail"] = detail;
        return JsonSerializer.Serialize(result, Indented);
    }

    private static string NoWallet(string stage) =>
        Failure("no_wallet",
            "No spending wallet is configured, so the claim cannot be paid. Configure LND, NWC, or Strike "
            + "(setup_wallet), then call sell_url again with the same arguments. Nothing was minted or charged.",
            stage: stage);

    private static string HttpProblem(ClaimHttpResponse response, string stage)
    {
        var problem = ParseObject(response.Body);
        return ProblemResult(CodeFor(response.Status, problem), response.Status, problem, response.RetryAfterSeconds,
            null, stage, paid: false);
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
        var message = paid && code == "merchant_exists" ? ProducerClaimErrors.MerchantExistsAfterPayment : info.Message;
        var result = new JsonObject
        {
            ["success"] = false,
            ["action"] = "sell_url",
            ["stage"] = stage,
            ["error"] = code,
            ["message"] = message,
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
        ["paymentSpent"] = state.Preimage != null ? true : "unknown",
        ["paymentHash"] = state.PaymentHash,
        ["stateFile"] = state.FilePath,
        ["tokenExpiresAt"] = state.ExpiresAt,
        ["instructions"] = state.Preimage != null
            ? "The claim payment is spent; do not pay again. The saved file holds the exact claim body and the paid "
              + "token (macaroon and preimage). Do not edit it and never paste its contents into chat. To retry, call "
              + "l402_producer(action=\"sell_url\") again with exactly the same arguments before the token expires; "
              + "the tool skips the payment and redeems the saved token. By hand: POST the file's \"body\" byte for "
              + $"byte to {state.ClaimUrl} with Content-Type: application/json and Authorization: L402 "
              + "<macaroon>:<preimage> taken from the file. If it keeps failing, contact support@lightningenable.com "
              + "with the payment hash only."
            : "No proof of payment was saved, and the wallet may have paid. Do not pay again and do not change the "
              + "arguments; calling sell_url again with the same arguments answers from the saved file and never pays "
              + "this claim again. Check the wallet's payment history for the payment hash, then contact "
              + "support@lightningenable.com with the payment hash only. Keep the saved file and never paste its "
              + "contents into chat.",
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

    // ── Body: one canonical serialization, shared with the Python port ───────────────

    /// <summary>
    /// The claim body, serialized ONCE, canonically (<c>shared/l402-producer-sell-url.json</c>
    /// <c>canonicalBody</c>): fixed key order, compact separators, UTF-8, no HTML escaping, and
    /// only <c>"</c>, <c>\</c> and control characters escaped (<c>\b \f \n \r \t</c>, else
    /// lowercase <c>\u00xx</c>). This is exactly Python's
    /// <c>json.dumps(separators=(",", ":"), ensure_ascii=False)</c>, so both ports send the
    /// same bytes and name the same state file.
    /// </summary>
    internal static string BuildBody(
        string targetUrl, string name, string? description, long? defaultPriceSats, string lightningAddress,
        string? email, string? pubkey, bool listPublicly, string acceptTerms)
    {
        var sb = new StringBuilder("{");
        Field(sb, "targetUrl"); Str(sb, targetUrl);
        sb.Append(','); Field(sb, "name"); Str(sb, name);
        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.Append(','); Field(sb, "description"); Str(sb, description.Trim());
        }
        if (defaultPriceSats is > 0)
        {
            sb.Append(','); Field(sb, "defaultPriceSats");
            sb.Append(defaultPriceSats.Value.ToString(CultureInfo.InvariantCulture));
        }
        sb.Append(','); Field(sb, "receive"); sb.Append('{'); Field(sb, "lightningAddress"); Str(sb, lightningAddress); sb.Append('}');
        if (email != null)
        {
            sb.Append(','); Field(sb, "recovery"); sb.Append('{'); Field(sb, "email"); Str(sb, email); sb.Append('}');
        }
        else if (pubkey != null)
        {
            sb.Append(','); Field(sb, "recovery"); sb.Append('{'); Field(sb, "nostrPubkey"); Str(sb, pubkey); sb.Append('}');
        }
        sb.Append(','); Field(sb, "listPublicly"); sb.Append(listPublicly ? "true" : "false");
        sb.Append(','); Field(sb, "acceptTerms"); Str(sb, acceptTerms);
        return sb.Append('}').ToString();

        static void Field(StringBuilder b, string key)
        {
            Str(b, key);
            b.Append(':');
        }
    }

    private static void Str(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
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

    /// <summary>Thrown when a save would move the state file backwards.</summary>
    internal sealed class ClaimStateConflictException(string message) : InvalidOperationException(message);

    /// <summary>
    /// The claim, one file per exact body: <c>claim/sell-&lt;sha256(body)[:16]&gt;.json</c>, 0600.
    /// The phase only moves forward (see <see cref="ProducerSellUrl"/>).
    /// </summary>
    internal sealed class ClaimState
    {
        public const string Challenge = "challenge";
        public const string Paying = "paying";
        public const string Paid = "paid";
        public const string Redeemed = "redeemed";

        private static int Rank(string? phase) => phase switch
        {
            Paying => 1,
            Paid => 2,
            Redeemed => 3,
            _ => 0,
        };

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
        public string? ProxyId { get; set; }
        public string? ProxyUrl { get; set; }
        public string? ManifestUrl { get; set; }
        public string? KeyLocation { get; set; }
        public string? KeyPath { get; set; }
        public string? RedeemedAt { get; set; }

        public static string FileNameFor(string body)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            return $"sell-{hash[..16]}.json";
        }

        /// <summary>Only a <c>challenge</c> the wallet never saw can expire.</summary>
        public bool IsExpired(DateTimeOffset now) =>
            Phase == Challenge
            && DateTimeOffset.TryParse(ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires)
            && expires <= now;

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
                    ProxyId = GetString(o["proxyId"]),
                    ProxyUrl = GetString(o["proxyUrl"]),
                    ManifestUrl = GetString(o["manifestUrl"]),
                    KeyLocation = GetString(o["keyLocation"]),
                    KeyPath = GetString(o["keyPath"]),
                    RedeemedAt = GetString(o["redeemedAt"]),
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Replace the token with a secret-free marker of the finished claim.</summary>
        public void MarkRedeemed(string proxyId, string? proxyUrl, string? manifestUrl, string? keyLocation,
            string? keyPath, DateTimeOffset now)
        {
            Phase = Redeemed;
            Invoice = null;
            Macaroon = null;
            Preimage = null;
            ProxyId = proxyId;
            ProxyUrl = proxyUrl;
            ManifestUrl = manifestUrl;
            KeyLocation = keyLocation;
            KeyPath = keyPath;
            RedeemedAt = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            Save();
        }

        /// <summary>
        /// Writes the state, re-reading the file first: a save never moves the phase backwards,
        /// except <c>paying</c> → <c>challenge</c> when <paramref name="allowDowngradeFromPaying"/>
        /// is set AND the file still says <c>paying</c>.
        /// </summary>
        public void Save(bool allowDowngradeFromPaying = false)
        {
            var directory = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(directory))
            {
                if (OperatingSystem.IsWindows())
                    Directory.CreateDirectory(directory);
                else
                    Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var onDisk = Load(directory, Body);
            if (onDisk != null && Rank(onDisk.Phase) > Rank(Phase))
            {
                var permitted = allowDowngradeFromPaying && onDisk.Phase == Paying && Phase == Challenge;
                if (!permitted)
                    throw new ClaimStateConflictException(
                        $"The claim state moved to '{onDisk.Phase}' elsewhere; not overwriting it with '{Phase}'.");
            }

            var o = new JsonObject
            {
                ["version"] = 1,
                ["claimUrl"] = ClaimUrl,
                ["body"] = Body,
                ["paymentHash"] = PaymentHash,
                ["expiresAt"] = ExpiresAt,
                ["priceSats"] = PriceSats,
                ["phase"] = Phase,
            };
            if (Invoice != null) o["invoice"] = Invoice;
            if (Macaroon != null) o["macaroon"] = Macaroon;
            if (Preimage != null) o["preimage"] = Preimage;
            if (ProxyId != null) o["proxyId"] = ProxyId;
            if (ProxyUrl != null) o["proxyUrl"] = ProxyUrl;
            if (ManifestUrl != null) o["manifestUrl"] = ManifestUrl;
            if (KeyLocation != null) o["keyLocation"] = KeyLocation;
            if (KeyPath != null) o["keyPath"] = KeyPath;
            if (RedeemedAt != null) o["redeemedAt"] = RedeemedAt;

            // Write-then-rename so a crash mid-write never leaves a torn file. The temp file is
            // created and restricted BEFORE the secrets go in.
            var tmp = FilePath + ".tmp";
            WriteRestricted(tmp, o.ToJsonString(Indented));
            File.Move(tmp, FilePath, overwrite: true);
        }

        public void Delete()
        {
            try { File.Delete(FilePath); } catch { /* best effort */ }
        }
    }
}

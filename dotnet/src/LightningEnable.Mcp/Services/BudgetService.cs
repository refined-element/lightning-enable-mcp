using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Service for managing spending budget limits with multi-tier approval.
/// Configuration is READ-ONLY - loaded from user config file at startup.
/// AI agents CANNOT modify budget configuration.
/// </summary>
public class BudgetService : IBudgetService
{
    private readonly object _lock = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly IBudgetConfigurationService _configService;
    private readonly IPriceService _priceService;

    private long _sessionSpentSats;
    private int _requestCount;
    private DateTime _sessionStarted;
    private DateTime _lastPaymentTime;
    private bool _isFirstPayment;
    private readonly Dictionary<string, PendingConfirmation> _pendingConfirmations = new();

    // Cached sats thresholds (updated when price changes significantly)
    private long _autoApproveSats;
    private long _logAndApproveSats;
    private long _formConfirmSats;
    private long _urlConfirmSats;
    private long _maxPerPaymentSats;
    private long _maxPerSessionSats;
    private DateTime _thresholdsCacheExpiry;

    // Tighten-only runtime caps (sats) set by the agent via configure_budget.
    // Null = no runtime cap. Enforced in addition to the USD config limits
    // (most-restrictive-wins). An agent can only ever LOWER these.
    private long? _runtimeMaxPerRequestSats;
    private long? _runtimeMaxPerSessionSats;

    public BudgetService(
        IBudgetConfigurationService configService,
        IPriceService priceService)
    {
        _configService = configService;
        _priceService = priceService;
        _sessionStarted = DateTime.UtcNow;
        _lastPaymentTime = DateTime.MinValue;
        _isFirstPayment = true;
        _thresholdsCacheExpiry = DateTime.MinValue;
    }

    public async Task<ApprovalCheckResult> CheckApprovalLevelAsync(
        long amountSats,
        CancellationToken cancellationToken = default)
    {
        // FAIL CLOSED on a price outage. The budget limits/tiers are USD-denominated,
        // so without a BTC price we cannot evaluate the payment safely. Three sources
        // are tried in parallel (first wins) and there is no stale-price fallback, so
        // all-down is rare — but when it happens we REFUSE the payment rather than
        // guess. Priming the price here (60s cache) means the conversions below reuse
        // the same value instead of re-hitting the network.
        try
        {
            await _priceService.GetBtcPriceAsync(cancellationToken);
        }
        catch (PriceUnavailableException)
        {
            return new ApprovalCheckResult
            {
                Level = ApprovalLevel.Deny,
                AmountSats = amountSats,
                AmountUsd = 0,
                DenialReason = "BTC price is currently unavailable (all price sources failed), so this " +
                               "payment cannot be checked against your budget and was refused. Please retry shortly.",
                RemainingSessionBudgetUsd = 0
            };
        }

        await UpdateThresholdsIfNeededAsync(cancellationToken);

        var config = _configService.Configuration;
        var amountUsd = await _priceService.SatsToUsdAsync(amountSats, cancellationToken);

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var sessionSpentUsd = await _priceService.SatsToUsdAsync(_sessionSpentSats, cancellationToken);
            var sessionLimitUsd = config.Limits.MaxPerSession ?? decimal.MaxValue;
            var remainingSessionUsd = sessionLimitUsd - sessionSpentUsd;

            // Check session limit first
            if (config.Limits.MaxPerSession.HasValue &&
                sessionSpentUsd + amountUsd > config.Limits.MaxPerSession.Value)
            {
                return new ApprovalCheckResult
                {
                    Level = ApprovalLevel.Deny,
                    AmountSats = amountSats,
                    AmountUsd = amountUsd,
                    DenialReason = $"Payment of {amountUsd:C} would exceed session limit. " +
                                   $"Spent: {sessionSpentUsd:C}, Limit: {sessionLimitUsd:C}, Remaining: {remainingSessionUsd:C}",
                    RemainingSessionBudgetUsd = Math.Max(0, remainingSessionUsd)
                };
            }

            // Check per-payment limit
            if (config.Limits.MaxPerPayment.HasValue &&
                amountUsd > config.Limits.MaxPerPayment.Value)
            {
                return new ApprovalCheckResult
                {
                    Level = ApprovalLevel.Deny,
                    AmountSats = amountSats,
                    AmountUsd = amountUsd,
                    DenialReason = $"Payment of {amountUsd:C} exceeds maximum per-payment limit of {config.Limits.MaxPerPayment.Value:C}. " +
                                   "Edit ~/.lightning-enable/config.json to change limits.",
                    RemainingSessionBudgetUsd = Math.Max(0, remainingSessionUsd)
                };
            }

            // Runtime tighten-only caps (set via configure_budget). Sats-based, enforced
            // on top of the USD config limits above — most-restrictive-wins.
            if (_runtimeMaxPerRequestSats.HasValue && amountSats > _runtimeMaxPerRequestSats.Value)
            {
                return new ApprovalCheckResult
                {
                    Level = ApprovalLevel.Deny,
                    AmountSats = amountSats,
                    AmountUsd = amountUsd,
                    DenialReason = $"Payment of {amountSats:N0} sats exceeds the runtime per-request cap of " +
                                   $"{_runtimeMaxPerRequestSats.Value:N0} sats set via configure_budget.",
                    RemainingSessionBudgetUsd = Math.Max(0, remainingSessionUsd)
                };
            }
            if (_runtimeMaxPerSessionSats.HasValue && _sessionSpentSats + amountSats > _runtimeMaxPerSessionSats.Value)
            {
                return new ApprovalCheckResult
                {
                    Level = ApprovalLevel.Deny,
                    AmountSats = amountSats,
                    AmountUsd = amountUsd,
                    DenialReason = $"Payment of {amountSats:N0} sats would exceed the runtime per-session cap of " +
                                   $"{_runtimeMaxPerSessionSats.Value:N0} sats (already spent {_sessionSpentSats:N0}) set via configure_budget.",
                    RemainingSessionBudgetUsd = Math.Max(0, remainingSessionUsd)
                };
            }

            // Check cooldown
            if (!IsCooldownElapsed())
            {
                var cooldownRemaining = config.Session.CooldownSeconds -
                    (DateTime.UtcNow - _lastPaymentTime).TotalSeconds;
                return new ApprovalCheckResult
                {
                    Level = ApprovalLevel.Deny,
                    AmountSats = amountSats,
                    AmountUsd = amountUsd,
                    DenialReason = $"Cooldown active. Please wait {cooldownRemaining:F1} seconds before next payment.",
                    RemainingSessionBudgetUsd = Math.Max(0, remainingSessionUsd)
                };
            }

            // Determine approval level based on tiers
            ApprovalLevel level;
            string? confirmMessage = null;

            // First payment of session always requires at least form confirmation
            if (_isFirstPayment && config.Session.RequireApprovalForFirstPayment)
            {
                level = amountUsd > config.Tiers.FormConfirm
                    ? ApprovalLevel.UrlConfirm
                    : ApprovalLevel.FormConfirm;
                confirmMessage = $"First payment of session: {amountUsd:C} ({amountSats:N0} sats)";
            }
            else if (amountUsd <= config.Tiers.AutoApprove)
            {
                level = ApprovalLevel.AutoApprove;
            }
            else if (amountUsd <= config.Tiers.LogAndApprove)
            {
                level = ApprovalLevel.LogAndApprove;
            }
            else if (amountUsd <= config.Tiers.FormConfirm)
            {
                level = ApprovalLevel.FormConfirm;
                confirmMessage = $"Approve payment of {amountUsd:C} ({amountSats:N0} sats)?";
            }
            else if (amountUsd <= config.Tiers.UrlConfirm)
            {
                level = ApprovalLevel.UrlConfirm;
                confirmMessage = $"Large payment of {amountUsd:C} requires browser confirmation.";
            }
            else
            {
                // Above all tiers - need URL confirmation for any amount with limit
                level = ApprovalLevel.UrlConfirm;
                confirmMessage = $"Payment of {amountUsd:C} requires secure browser confirmation.";
            }

            return new ApprovalCheckResult
            {
                Level = level,
                AmountSats = amountSats,
                AmountUsd = amountUsd,
                ConfirmationMessage = confirmMessage,
                RemainingSessionBudgetUsd = Math.Max(0, remainingSessionUsd)
            };
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public BudgetCheckResult CheckBudget(long amountSats)
    {
        // Synchronous wrapper. CRITICAL: this path has NO interactive
        // confirmation / nonce flow (unlike pay_invoice / pay_l402_challenge),
        // and is used by send_onchain, settle_agent_service, and L402 auto-pay.
        // So a payment the tier logic says "requires confirmation"
        // (FormConfirm/UrlConfirm) MUST be denied here, not silently allowed —
        // only AutoApprove/LogAndApprove may proceed. (C-1: this previously
        // mapped any non-Deny result to Allow, silently skipping confirmation
        // on the most dangerous tools, including irreversible on-chain sends.)
        var result = CheckApprovalLevelAsync(amountSats).GetAwaiter().GetResult();

        // Rough "remaining" figure for the caller. Clamp to avoid OverflowException
        // when no session limit is configured (RemainingSessionBudgetUsd is then
        // ~decimal.MaxValue, and *100 would overflow). Informational only.
        var remaining = result.RemainingSessionBudgetUsd >= (decimal)long.MaxValue / 100m
            ? long.MaxValue
            : (long)(result.RemainingSessionBudgetUsd * 100);
        var maxPerRequest = _maxPerPaymentSats > 0 ? _maxPerPaymentSats : 100000;

        if (result.RequiresConfirmation)
        {
            var detail = string.IsNullOrEmpty(result.ConfirmationMessage)
                ? "This payment exceeds the auto-approve limit."
                : result.ConfirmationMessage;
            return BudgetCheckResult.Deny(
                $"{detail} It requires explicit confirmation and cannot be auto-approved on this path. " +
                "Use pay_invoice / pay_l402_challenge (which support the confirmation flow), or raise the " +
                "auto-approve limit in ~/.lightning-enable/config.json.",
                remaining, maxPerRequest);
        }

        return result.CanProceed
            ? BudgetCheckResult.Allow(remaining, maxPerRequest)
            : BudgetCheckResult.Deny(result.DenialReason ?? "Payment denied", remaining, maxPerRequest);
    }

    public void RecordSpend(long amountSats)
    {
        if (amountSats < 0)
            throw new ArgumentOutOfRangeException(nameof(amountSats), "Amount cannot be negative");

        lock (_lock)
        {
            _sessionSpentSats += amountSats;
            _requestCount++;
            _isFirstPayment = false;
        }
    }

    public async Task<ConfigureBudgetResult> ConfigureBudgetAsync(
        long perRequestSats, long perSessionSats, CancellationToken cancellationToken = default)
    {
        if (perRequestSats <= 0)
            return ConfigureBudgetResult.Fail("per_request must be a positive number of sats.");
        if (perSessionSats <= 0)
            return ConfigureBudgetResult.Fail("per_session must be a positive number of sats.");
        if (perRequestSats > perSessionSats)
            return ConfigureBudgetResult.Fail("per_request cannot exceed per_session.");

        // Make sure the config-derived sats caps are current before we compare.
        await UpdateThresholdsIfNeededAsync(cancellationToken);

        lock (_lock)
        {
            // Effective cap = most restrictive of the operator's config-file limit
            // (USD→sats) and any existing runtime cap. A 0 config cap means "no
            // config limit set" → treat as unlimited for this comparison.
            long configReq = _maxPerPaymentSats > 0 ? _maxPerPaymentSats : long.MaxValue;
            long configSess = _maxPerSessionSats > 0 ? _maxPerSessionSats : long.MaxValue;
            long effReq = Math.Min(configReq, _runtimeMaxPerRequestSats ?? long.MaxValue);
            long effSess = Math.Min(configSess, _runtimeMaxPerSessionSats ?? long.MaxValue);

            // TIGHTEN-ONLY. An agent may only LOWER its caps. Refusing to raise them
            // above the current effective limit is the whole point: a prompt-injected
            // agent must not be able to loosen its own spending authority and then
            // drain the wallet. To raise limits, the operator edits config.json.
            if (perRequestSats > effReq || perSessionSats > effSess)
            {
                string Fmt(long v) => v == long.MaxValue ? "unlimited" : $"{v:N0} sats";
                return ConfigureBudgetResult.Fail(
                    "configure_budget can only LOWER spending limits, not raise them. " +
                    $"Current effective caps: {Fmt(effReq)}/request, {Fmt(effSess)}/session. " +
                    "To increase limits, the operator must edit ~/.lightning-enable/config.json — " +
                    "an agent cannot raise its own spending authority.");
            }

            _runtimeMaxPerRequestSats = perRequestSats;
            _runtimeMaxPerSessionSats = perSessionSats;
            return ConfigureBudgetResult.Ok(perRequestSats, perSessionSats);
        }
    }

    public BudgetConfig GetConfig()
    {
        lock (_lock)
        {
            return new BudgetConfig
            {
                MaxSatsPerRequest = _maxPerPaymentSats,
                MaxSatsPerSession = _maxPerSessionSats,
                SessionSpent = _sessionSpentSats,
                RequestCount = _requestCount,
                SessionStarted = _sessionStarted,
                HardMaxSatsPerRequest = _maxPerPaymentSats,
                HardMaxSatsPerSession = _maxPerSessionSats,
                RuntimeMaxPerRequestSats = _runtimeMaxPerRequestSats,
                RuntimeMaxPerSessionSats = _runtimeMaxPerSessionSats
            };
        }
    }

    public UserBudgetConfiguration GetUserConfiguration()
    {
        return _configService.Configuration;
    }

    public void ResetSession()
    {
        lock (_lock)
        {
            _sessionSpentSats = 0;
            _requestCount = 0;
            _sessionStarted = DateTime.UtcNow;
            _isFirstPayment = true;
        }
    }

    public bool IsCooldownElapsed()
    {
        var config = _configService.Configuration;
        var elapsed = DateTime.UtcNow - _lastPaymentTime;
        return elapsed.TotalSeconds >= config.Session.CooldownSeconds;
    }

    public void RecordPaymentTime()
    {
        lock (_lock)
        {
            _lastPaymentTime = DateTime.UtcNow;
        }
    }

    public PendingConfirmation CreatePendingConfirmation(long amountSats, decimal amountUsd, string toolName, string description, string destination)
    {
        lock (_lock)
        {
            // Clean expired entries first
            CleanExpiredConfirmationsLocked();

            var nonce = GenerateNonce();
            var confirmation = new PendingConfirmation
            {
                Nonce = nonce,
                AmountSats = amountSats,
                AmountUsd = amountUsd,
                ToolName = toolName,
                Description = description,
                Destination = (destination ?? string.Empty).Trim(),
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(2)
            };

            _pendingConfirmations[nonce] = confirmation;
            return confirmation;
        }
    }

    public PendingConfirmation? ValidateConfirmation(string nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
            return null;

        lock (_lock)
        {
            if (!_pendingConfirmations.TryGetValue(nonce, out var confirmation))
                return null;

            if (confirmation.IsExpired)
            {
                _pendingConfirmations.Remove(nonce);
                return null;
            }

            return confirmation;
        }
    }

    public PendingConfirmation? ValidateAndConsumeConfirmation(string nonce, long expectedAmountSats, string expectedToolName, string expectedDestination)
    {
        if (string.IsNullOrWhiteSpace(nonce))
            return null;

        lock (_lock)
        {
            if (!_pendingConfirmations.TryGetValue(nonce, out var confirmation))
                return null;

            if (confirmation.IsExpired)
            {
                _pendingConfirmations.Remove(nonce);
                return null;
            }

            // C-3: bind the approval to the EXACT amount AND tool it was created for.
            // A code approved for X sats on pay_invoice must not authorize a different
            // amount, NOR a different tool (e.g. send_onchain) even if the sats match.
            // On mismatch we do NOT consume — the nonce stays valid so the correct
            // (amount, tool) retry still works, but this request is refused.
            if (confirmation.AmountSats != expectedAmountSats)
                return null;
            if (!string.Equals(confirmation.ToolName, expectedToolName, StringComparison.Ordinal))
                return null;
            // #21 anti-redirect: bind to the EXACT destination too. A code approved to pay
            // invoice/URL/address X must never authorize paying a different one (compared
            // after trimming, mirroring how the destination was stored).
            if (!string.Equals(confirmation.Destination, (expectedDestination ?? string.Empty).Trim(), StringComparison.Ordinal))
                return null;

            // Amount + tool + destination match — consume (one-time use).
            _pendingConfirmations.Remove(nonce);
            return confirmation;
        }
    }

    public void CleanExpiredConfirmations()
    {
        lock (_lock)
        {
            CleanExpiredConfirmationsLocked();
        }
    }

    private void CleanExpiredConfirmationsLocked()
    {
        var expired = _pendingConfirmations
            .Where(kvp => kvp.Value.IsExpired)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expired)
        {
            _pendingConfirmations.Remove(key);
        }
    }

    private static string GenerateNonce()
    {
        // C-4: cryptographically-random nonce. Previously used System.Random,
        // which is predictable (time-seeded PRNG) — a weak basis for a payment
        // confirmation token. NOTE: even a strong nonce does NOT protect against
        // the agent itself, because confirm_payment is a model-callable tool; the
        // nonce only guards against accidental auto-approval. True out-of-band
        // confirmation (MCP elicitation / a URL the model can't read) is the
        // deeper fix and a separate design decision.
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var buf = new char[6];
        for (int i = 0; i < buf.Length; i++)
            buf[i] = chars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(chars.Length)];
        return new string(buf);
    }

    private async Task UpdateThresholdsIfNeededAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow < _thresholdsCacheExpiry)
        {
            return;
        }

        var config = _configService.Configuration;

        // Convert USD thresholds to sats
        _autoApproveSats = await _priceService.UsdToSatsAsync(config.Tiers.AutoApprove, cancellationToken);
        _logAndApproveSats = await _priceService.UsdToSatsAsync(config.Tiers.LogAndApprove, cancellationToken);
        _formConfirmSats = await _priceService.UsdToSatsAsync(config.Tiers.FormConfirm, cancellationToken);
        _urlConfirmSats = await _priceService.UsdToSatsAsync(config.Tiers.UrlConfirm, cancellationToken);

        if (config.Limits.MaxPerPayment.HasValue)
        {
            _maxPerPaymentSats = await _priceService.UsdToSatsAsync(config.Limits.MaxPerPayment.Value, cancellationToken);
        }

        if (config.Limits.MaxPerSession.HasValue)
        {
            _maxPerSessionSats = await _priceService.UsdToSatsAsync(config.Limits.MaxPerSession.Value, cancellationToken);
        }

        // Cache for 5 minutes
        _thresholdsCacheExpiry = DateTime.UtcNow.AddMinutes(5);
    }
}

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

    // Active spend reservations (id -> reserved sats). The sum is mirrored in
    // _reservedSats so the reservation gate is O(1). Guarded by _lock together with
    // _sessionSpentSats — evaluating (settled + reserved) and inserting a reservation
    // happen in ONE critical section, which is what makes the cap race-safe.
    private readonly Dictionary<string, long> _reservations = new();
    private long _reservedSats;

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

    // Whether the last cap evaluation had a BTC price. False means the USD limits could
    // not be converted and the sats limits carried the budget on their own — surfaced by
    // GetEffectiveCaps so the operator can see it.
    private volatile bool _priceAvailable = true;

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

    // =========================================================================
    // Effective caps
    //
    // Every gate resolves the same way: take the most restrictive of the USD config limit
    // (converted), the sats config limit (used as-is), and the tighten-only runtime cap.
    // `usdAvailable` is what makes a sats budget price-independent — with no price the USD
    // leg is simply absent from the comparison.
    // =========================================================================

    /// <summary>Where an effective cap came from, for <c>budget action=status</c>.</summary>
    internal const string CapSourceUsd = "config USD limit (maxPerPayment / maxPerSession)";
    internal const string CapSourceSats = "config sats limit (maxPerPaymentSats / maxPerSessionSats)";
    internal const string CapSourceRuntime = "runtime cap (budget action=tighten)";
    internal const string CapSourceNone = "no limit configured";

    private static string DenominationOf(string source) => source switch
    {
        CapSourceUsd => "usd",
        CapSourceSats => "sats",
        CapSourceRuntime => "runtime",
        _ => "none",
    };

    /// <summary>
    /// Most-restrictive-wins, remembering WHICH limit won. Ties resolve to the config
    /// limits before the runtime cap, and to USD before sats, so the reported source names
    /// the operator's own setting rather than an equal agent-set one.
    /// </summary>
    private static (long? Sats, string Source) ResolveCap(
        long? usdDerived, long? satsConfig, long? runtime)
    {
        long? winner = null;
        foreach (var candidate in new[] { usdDerived, satsConfig, runtime })
        {
            if (candidate.HasValue && (!winner.HasValue || candidate.Value < winner.Value))
            {
                winner = candidate.Value;
            }
        }

        if (!winner.HasValue)
        {
            return (null, CapSourceNone);
        }
        if (usdDerived == winner)
        {
            return (winner, CapSourceUsd);
        }
        if (satsConfig == winner)
        {
            return (winner, CapSourceSats);
        }
        return (winner, CapSourceRuntime);
    }

    private (long? Sats, string Source) EffectiveRequestCap(bool usdAvailable)
    {
        var limits = _configService.Configuration.Limits;
        long? usdCap = usdAvailable && limits.MaxPerPayment.HasValue && _maxPerPaymentSats > 0
            ? _maxPerPaymentSats
            : null;
        return ResolveCap(usdCap, limits.MaxPerPaymentSats, _runtimeMaxPerRequestSats);
    }

    private (long? Sats, string Source) EffectiveSessionCap(bool usdAvailable)
    {
        var limits = _configService.Configuration.Limits;
        long? usdCap = usdAvailable && limits.MaxPerSession.HasValue && _maxPerSessionSats > 0
            ? _maxPerSessionSats
            : null;
        return ResolveCap(usdCap, limits.MaxPerSessionSats, _runtimeMaxPerSessionSats);
    }

    /// <summary>
    /// Primes the BTC price and the USD-&gt;sats cap cache. Returns whether the USD limits
    /// can be evaluated on this pass; throws <see cref="PriceUnavailableException"/> when
    /// there is no price AND no sats limits to fall back on, so the caller fails closed
    /// exactly as it always has.
    /// </summary>
    private async Task<bool> RefreshPriceAndThresholdsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _priceService.GetBtcPriceAsync(cancellationToken);
            await UpdateThresholdsIfNeededAsync(cancellationToken);
            _priceAvailable = true;
            return true;
        }
        catch (PriceUnavailableException)
        {
            _priceAvailable = false;
            if (!_configService.Configuration.Limits.HasSatsLimits)
            {
                throw;
            }
            Console.Error.WriteLine(
                "[Lightning Enable] BTC price unavailable; enforcing the satoshi limits only. "
                + "The USD limits cannot be evaluated until a price source recovers.");
            return false;
        }
    }

    /// <summary>
    /// The caps in force right now, for reporting. <paramref name="usdAvailable"/> says
    /// whether a BTC price could be fetched just now — <c>budget action=status</c> refreshes
    /// the price itself, so it passes what it found. Omit it to report the state the last
    /// budget gate observed.
    /// </summary>
    public EffectiveBudgetCaps GetEffectiveCaps(bool? usdAvailable = null)
    {
        var available = usdAvailable ?? _priceAvailable;
        var limits = _configService.Configuration.Limits;

        (long? Sats, string Source) request;
        (long? Sats, string Source) session;
        lock (_lock)
        {
            request = EffectiveRequestCap(available);
            session = EffectiveSessionCap(available);
        }

        // The tighter of the two caps decides the headline denomination.
        string binding;
        if (!request.Sats.HasValue && !session.Sats.HasValue)
        {
            binding = "none";
        }
        else if (!session.Sats.HasValue)
        {
            binding = DenominationOf(request.Source);
        }
        else if (!request.Sats.HasValue)
        {
            binding = DenominationOf(session.Source);
        }
        else
        {
            binding = DenominationOf(
                request.Sats.Value <= session.Sats.Value ? request.Source : session.Source);
        }

        // "Outage mode" is the sats-only path actually being in force: no price, and sats
        // limits able to carry the check. Without them an outage refuses payments outright,
        // which is the pre-existing fail-closed path rather than a mode.
        var outageModeActive = !available && limits.HasSatsLimits;

        var unattended = limits.AutoApproveSats.HasValue
            ? $"payments up to {limits.AutoApproveSats.Value:N0} sats proceed unattended "
              + "(limits.autoApproveSats); anything above needs confirmation"
            : "every payment needs confirmation, because limits.autoApproveSats is not set";

        var note = (available, limits.HasSatsLimits) switch
        {
            (false, true) =>
                "The BTC price is unavailable, so the USD limits and tiers are not being enforced; "
                + $"your satoshi limits are carrying the budget on their own and {unattended}. Set "
                + "both maxPerPaymentSats and maxPerSessionSats to close every gap during an outage.",
            (false, false) =>
                "The BTC price is unavailable and no satoshi limits are configured, so payments "
                + "are refused until a price source recovers. Set limits.maxPerPaymentSats / "
                + "maxPerSessionSats for a budget that needs no price feed.",
            (true, true) =>
                "USD and satoshi limits are both in force; the stricter one wins on every check. "
                + "limits.autoApproveSats applies only while the BTC price is unavailable — right "
                + "now the USD tiers decide what needs confirmation.",
            _ => "Limits are USD-denominated and are converted at the current BTC price.",
        };

        return new EffectiveBudgetCaps(
            request.Sats, request.Source, session.Sats, session.Source, binding, available,
            limits.AutoApproveSats, outageModeActive, note);
    }

    public async Task<ApprovalCheckResult> CheckApprovalLevelAsync(
        long amountSats,
        CancellationToken cancellationToken = default)
    {
        // FAIL CLOSED on a price outage. The USD limits/tiers cannot be evaluated without
        // a BTC price. Three sources are tried in parallel (first wins) and there is no
        // stale-price fallback, so all-down is rare — but when it happens we REFUSE the
        // payment rather than guess. Priming the price here (60s cache) means the
        // conversions below reuse the same value instead of re-hitting the network.
        //
        // The exception is a budget with satoshi limits: those need no conversion, which
        // is exactly why an operator sets them. Then the outage narrows the check to the
        // sats caps instead of refusing.
        bool usdAvailable;
        try
        {
            usdAvailable = await RefreshPriceAndThresholdsAsync(cancellationToken);
        }
        catch (PriceUnavailableException)
        {
            return new ApprovalCheckResult
            {
                Level = ApprovalLevel.Deny,
                AmountSats = amountSats,
                AmountUsd = 0,
                DenialReason = "BTC price is currently unavailable (all price sources failed), so this " +
                               "payment cannot be checked against your budget and was refused. Please retry " +
                               "shortly, or set limits.maxPerPaymentSats / maxPerSessionSats in " +
                               "~/.lightning-enable/config.json for a budget that needs no price feed.",
                RemainingSessionBudgetUsd = 0
            };
        }

        if (!usdAvailable)
        {
            return CheckSatsOnly(amountSats);
        }

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

            // Sats config limits, enforced on top of the USD ones — most-restrictive-wins.
            // Whichever denomination is tighter denies first, so an operator can set both
            // and get the stricter of the two without ordering mattering.
            var satsDenial = SatsConfigDenialLocked(amountSats);
            if (satsDenial != null)
            {
                return new ApprovalCheckResult
                {
                    Level = ApprovalLevel.Deny,
                    AmountSats = amountSats,
                    AmountUsd = amountUsd,
                    DenialReason = satsDenial,
                    RemainingSessionBudgetUsd = Math.Max(0, remainingSessionUsd)
                };
            }

            // Runtime tighten-only caps (set via budget action=tighten). Sats-based, enforced
            // on top of the USD config limits above — most-restrictive-wins.
            if (_runtimeMaxPerRequestSats.HasValue && amountSats > _runtimeMaxPerRequestSats.Value)
            {
                return new ApprovalCheckResult
                {
                    Level = ApprovalLevel.Deny,
                    AmountSats = amountSats,
                    AmountUsd = amountUsd,
                    DenialReason = $"Payment of {amountSats:N0} sats exceeds the runtime per-request cap of " +
                                   $"{_runtimeMaxPerRequestSats.Value:N0} sats set via budget action=tighten.",
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
                                   $"{_runtimeMaxPerSessionSats.Value:N0} sats (already spent {_sessionSpentSats:N0}) set via budget action=tighten.",
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
        // Report the config-derived per-payment cap, or NULL when there isn't one. Never
        // invent a figure: this value is informational (not enforced here), but an agent
        // reads it as a real limit, and a fabricated 100,000-sat "limit" is a lie about the
        // operator's configuration.
        long? maxPerRequest = _maxPerPaymentSats > 0 ? _maxPerPaymentSats : null;

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

    /// <summary>
    /// Why the config's satoshi limits refuse <paramref name="amountSats"/>, or null if
    /// they allow it. Caller must hold <c>_lock</c> (it reads <c>_sessionSpentSats</c>).
    /// </summary>
    private string? SatsConfigDenialLocked(long amountSats)
    {
        var limits = _configService.Configuration.Limits;

        if (limits.MaxPerPaymentSats.HasValue && amountSats > limits.MaxPerPaymentSats.Value)
        {
            return $"Payment of {amountSats:N0} sats exceeds the per-payment limit of " +
                   $"{limits.MaxPerPaymentSats.Value:N0} sats (limits.maxPerPaymentSats). " +
                   "Edit ~/.lightning-enable/config.json to change limits.";
        }

        if (limits.MaxPerSessionSats.HasValue
            && _sessionSpentSats + amountSats > limits.MaxPerSessionSats.Value)
        {
            var remaining = Math.Max(0, limits.MaxPerSessionSats.Value - _sessionSpentSats);
            return $"Payment of {amountSats:N0} sats would exceed the session limit of " +
                   $"{limits.MaxPerSessionSats.Value:N0} sats (limits.maxPerSessionSats). " +
                   $"Already spent {_sessionSpentSats:N0}; {remaining:N0} sats remain.";
        }

        return null;
    }

    /// <summary>
    /// Approval decided by the satoshi limits alone, because no BTC price is available.
    ///
    /// <para>Only reachable when the operator configured a sats CEILING — that is what makes
    /// the budget enforceable with no conversion. But a ceiling says "never more than this";
    /// it does not say "this much is fine unattended". Those are different statements, and
    /// the USD tier ladder that normally makes the second one cannot be evaluated here.</para>
    ///
    /// <para>So this path FAILS CLOSED on approval:</para>
    /// <list type="bullet">
    /// <item><description><c>limits.autoApproveSats</c> at or below →
    /// <see cref="ApprovalLevel.AutoApprove"/>. This is the operator saying, explicitly and
    /// in satoshis, how much may be spent without a human.</description></item>
    /// <item><description>above it, or when it is unset →
    /// <see cref="ApprovalLevel.FormConfirm"/>, i.e. the normal confirmation flow. The
    /// paying tools request a code exactly as they always do; the auto-pay paths (which
    /// refuse anything needing confirmation — see <see cref="CheckBudgetAsync"/>) refuse.
    /// Neither is touched here: this only decides the level.</description></item>
    /// </list>
    ///
    /// <para><see cref="ApprovalLevel.LogAndApprove"/> is never returned: it proceeds
    /// unattended, which is precisely what must not happen on an unevaluable tier.</para>
    ///
    /// <para><see cref="ApprovalLevel.FormConfirm"/> rather than
    /// <see cref="ApprovalLevel.UrlConfirm"/> because UrlConfirm's stronger check asks the
    /// human to retype the payment's USD amount — the one number that does not exist during
    /// a price outage.</para>
    ///
    /// <para>The ceilings and the cooldown are checked BEFORE any of this, so confirmation
    /// can never buy past a cap.</para>
    /// </summary>
    private ApprovalCheckResult CheckSatsOnly(long amountSats)
    {
        lock (_lock)
        {
            var denial = SatsConfigDenialLocked(amountSats);

            if (denial == null
                && _runtimeMaxPerRequestSats.HasValue
                && amountSats > _runtimeMaxPerRequestSats.Value)
            {
                denial = $"Payment of {amountSats:N0} sats exceeds the runtime per-request cap of " +
                         $"{_runtimeMaxPerRequestSats.Value:N0} sats set via budget action=tighten.";
            }
            else if (denial == null
                && _runtimeMaxPerSessionSats.HasValue
                && _sessionSpentSats + amountSats > _runtimeMaxPerSessionSats.Value)
            {
                denial = $"Payment of {amountSats:N0} sats would exceed the runtime per-session cap of " +
                         $"{_runtimeMaxPerSessionSats.Value:N0} sats (already spent {_sessionSpentSats:N0}) " +
                         "set via budget action=tighten.";
            }

            if (denial != null)
            {
                return new ApprovalCheckResult
                {
                    Level = ApprovalLevel.Deny,
                    AmountSats = amountSats,
                    AmountUsd = 0,
                    DenialReason = denial,
                    RemainingSessionBudgetUsd = 0
                };
            }

            if (!IsCooldownElapsed())
            {
                var cooldownRemaining = _configService.Configuration.Session.CooldownSeconds -
                    (DateTime.UtcNow - _lastPaymentTime).TotalSeconds;
                return new ApprovalCheckResult
                {
                    Level = ApprovalLevel.Deny,
                    AmountSats = amountSats,
                    AmountUsd = 0,
                    DenialReason = $"Cooldown active. Please wait {cooldownRemaining:F1} seconds before next payment.",
                    RemainingSessionBudgetUsd = 0
                };
            }

            // Inside the ceilings. Now: may it proceed WITHOUT a human?
            var autoApproveSats = _configService.Configuration.Limits.AutoApproveSats;
            var firstPaymentNeedsApproval =
                _isFirstPayment && _configService.Configuration.Session.RequireApprovalForFirstPayment;

            if (autoApproveSats.HasValue
                && amountSats <= autoApproveSats.Value
                && !firstPaymentNeedsApproval)
            {
                return new ApprovalCheckResult
                {
                    Level = ApprovalLevel.AutoApprove,
                    AmountSats = amountSats,
                    AmountUsd = 0,
                    RemainingSessionBudgetUsd = 0
                };
            }

            var why = firstPaymentNeedsApproval
                ? "the first payment of the session always requires confirmation"
                : !autoApproveSats.HasValue
                    ? "no limits.autoApproveSats is configured, so nothing may be spent "
                      + "unattended while the price is down"
                    : $"the unattended limit is {autoApproveSats.Value:N0} sats";

            return new ApprovalCheckResult
            {
                Level = ApprovalLevel.FormConfirm,
                AmountSats = amountSats,
                AmountUsd = 0,
                ConfirmationMessage =
                    $"Approve {amountSats:N0} sats? The BTC price is unavailable, so this payment " +
                    $"was checked against your satoshi limits only and {why}.",
                RemainingSessionBudgetUsd = 0
            };
        }
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

    public async Task<SpendReservationResult> TryReserveAsync(
        long amountSats, CancellationToken cancellationToken = default)
    {
        if (amountSats <= 0)
            return SpendReservationResult.Denied("Reservation amount must be greater than zero.");

        // FAIL CLOSED on a price outage — UNLESS the operator configured satoshi limits,
        // which are enforceable with no conversion. That is exactly what they are for:
        // three price sources being down must not stop a sats-budgeted agent. The USD
        // limits are simply absent from the comparison until a source recovers. Refreshing
        // the cached USD->sats caps happens here too, OUTSIDE the lock (it awaits the price
        // service); the gate below then runs fully synchronously, so the lock is never held
        // across an await.
        bool usdAvailable;
        try
        {
            usdAvailable = await RefreshPriceAndThresholdsAsync(cancellationToken);
        }
        catch (PriceUnavailableException)
        {
            return SpendReservationResult.Denied(
                "BTC price is currently unavailable (all price sources failed), so this payment " +
                "cannot be checked against your budget and was refused. Please retry shortly, or " +
                "set limits.maxPerPaymentSats / maxPerSessionSats in ~/.lightning-enable/config.json " +
                "for a budget that needs no price feed.");
        }

        lock (_lock)
        {
            // Effective caps (sats) = most restrictive of the config USD limit (converted,
            // when a price is available), the config sats limit, and any tighten-only
            // runtime cap.
            long effSessionCap = EffectiveSessionCap(usdAvailable).Sats ?? long.MaxValue;
            long effRequestCap = EffectiveRequestCap(usdAvailable).Sats ?? long.MaxValue;

            if (amountSats > effRequestCap)
            {
                return SpendReservationResult.Denied(
                    $"Payment of {amountSats:N0} sats exceeds the per-payment cap of {effRequestCap:N0} sats.");
            }

            // The whole point: settled + ALL active reservations + this amount must fit.
            // Guard against overflow when caps are effectively unlimited (long.MaxValue).
            long committedPlusReserved = _sessionSpentSats + _reservedSats;
            if (effSessionCap != long.MaxValue && committedPlusReserved + amountSats > effSessionCap)
            {
                long remaining = Math.Max(0, effSessionCap - committedPlusReserved);
                return SpendReservationResult.Denied(
                    $"Payment of {amountSats:N0} sats would exceed the session cap of {effSessionCap:N0} sats " +
                    $"(already spent {_sessionSpentSats:N0}, reserved {_reservedSats:N0} by in-flight payments; " +
                    $"{remaining:N0} sats available).");
            }

            var reservationId = Guid.NewGuid().ToString("N");
            _reservations[reservationId] = amountSats;
            _reservedSats += amountSats;
            return SpendReservationResult.Reserved(reservationId, amountSats);
        }
    }

    public void CommitReservation(string reservationId, long actualDebitSats)
    {
        if (actualDebitSats < 0)
            throw new ArgumentOutOfRangeException(nameof(actualDebitSats), "Amount cannot be negative");
        if (string.IsNullOrEmpty(reservationId))
            return;

        lock (_lock)
        {
            // Idempotent: an unknown / already-resolved reservation is a no-op, so a
            // double-commit (e.g. a retried settlement callback) can't double-count.
            if (!_reservations.Remove(reservationId, out var reserved))
                return;

            _reservedSats -= reserved;
            _sessionSpentSats += actualDebitSats;
            _requestCount++;
            _isFirstPayment = false;
        }
    }

    public void ReleaseReservation(string reservationId)
    {
        if (string.IsNullOrEmpty(reservationId))
            return;

        lock (_lock)
        {
            if (_reservations.Remove(reservationId, out var reserved))
                _reservedSats -= reserved;
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

        // Make sure the config-derived sats caps are current before we compare. A price
        // outage does not block tightening: with sats limits configured the comparison is
        // made against those alone, which can only ever be MORE restrictive than including
        // a USD cap would be.
        bool usdAvailable;
        try
        {
            usdAvailable = await RefreshPriceAndThresholdsAsync(cancellationToken);
        }
        catch (PriceUnavailableException)
        {
            return ConfigureBudgetResult.Fail(
                "BTC price is currently unavailable, so the operator's USD limits cannot be " +
                "converted to compare against. Please retry shortly.");
        }

        lock (_lock)
        {
            // Effective cap = most restrictive of the operator's config limits (USD→sats
            // and/or sats) and any existing runtime cap.
            long effReq = EffectiveRequestCap(usdAvailable).Sats ?? long.MaxValue;
            long effSess = EffectiveSessionCap(usdAvailable).Sats ?? long.MaxValue;

            // TIGHTEN-ONLY. An agent may only LOWER its caps. Refusing to raise them
            // above the current effective limit is the whole point: a prompt-injected
            // agent must not be able to loosen its own spending authority and then
            // drain the wallet. To raise limits, the operator edits config.json.
            if (perRequestSats > effReq || perSessionSats > effSess)
            {
                string Fmt(long v) => v == long.MaxValue ? "unlimited" : $"{v:N0} sats";
                return ConfigureBudgetResult.Fail(
                    "budget action=tighten can only LOWER spending limits, not raise them. " +
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
        // the agent itself, because verify_confirmation_code is a model-callable tool; the
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

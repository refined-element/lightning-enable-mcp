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
        await UpdateThresholdsIfNeededAsync(cancellationToken);

        var config = _configService.Configuration;
        var amountUsd = await _priceService.SatsToUsdAsync(amountSats, cancellationToken);

        lock (_lock)
        {
            var sessionSpentUsd = _priceService.SatsToUsdAsync(_sessionSpentSats, cancellationToken).GetAwaiter().GetResult();
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
    }

    public BudgetCheckResult CheckBudget(long amountSats)
    {
        // Synchronous wrapper for backward compatibility
        var result = CheckApprovalLevelAsync(amountSats).GetAwaiter().GetResult();

        return result.CanProceed
            ? BudgetCheckResult.Allow(
                (long)(result.RemainingSessionBudgetUsd * 100), // Rough sats conversion
                _maxPerPaymentSats > 0 ? _maxPerPaymentSats : 100000)
            : BudgetCheckResult.Deny(
                result.DenialReason ?? "Payment denied",
                (long)(result.RemainingSessionBudgetUsd * 100),
                _maxPerPaymentSats > 0 ? _maxPerPaymentSats : 100000);
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
                HardMaxSatsPerSession = _maxPerSessionSats
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

    public PendingConfirmation CreatePendingConfirmation(long amountSats, decimal amountUsd, string toolName, string description)
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

    public PendingConfirmation? ValidateAndConsumeConfirmation(string nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
            return null;

        lock (_lock)
        {
            if (!_pendingConfirmations.TryGetValue(nonce, out var confirmation))
                return null;

            // Always remove - one-time use
            _pendingConfirmations.Remove(nonce);

            if (confirmation.IsExpired)
                return null;

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
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var random = new Random();
        return new string(Enumerable.Range(0, 6).Select(_ => chars[random.Next(chars.Length)]).ToArray());
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

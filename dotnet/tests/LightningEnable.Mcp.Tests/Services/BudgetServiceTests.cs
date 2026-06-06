using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using FluentAssertions;
using Moq;

namespace LightningEnable.Mcp.Tests.Services;

public class BudgetServiceTests
{
    private readonly Mock<IBudgetConfigurationService> _configServiceMock;
    private readonly Mock<IPriceService> _priceServiceMock;

    public BudgetServiceTests()
    {
        _configServiceMock = new Mock<IBudgetConfigurationService>();
        _priceServiceMock = new Mock<IPriceService>();

        // Default configuration
        SetupDefaultConfiguration();

        // Mock price service — a fixed, deterministic conversion of 100,000 sats = $1.00.
        // This is a SIMPLIFIED test rate chosen so the tier math is easy to read; it is
        // NOT a real BTC price. All "$X (mock rate)" comments below refer to this rate.
        _priceServiceMock.Setup(p => p.SatsToUsdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long sats, CancellationToken _) => sats / 100000m);
        _priceServiceMock.Setup(p => p.UsdToSatsAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((decimal usd, CancellationToken _) => (long)(usd * 100000));
        // A live price is required by the fail-closed guard at the top of
        // CheckApprovalLevelAsync; default to "available" so existing tests run.
        _priceServiceMock.Setup(p => p.GetBtcPriceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(100000m);
    }

    private void SetupDefaultConfiguration()
    {
        _configServiceMock.Setup(c => c.Configuration).Returns(new UserBudgetConfiguration
        {
            Currency = "USD",
            Tiers = new TierThresholds
            {
                AutoApprove = 0.10m,
                LogAndApprove = 1.00m,
                FormConfirm = 10.00m,
                UrlConfirm = 100.00m
            },
            Limits = new PaymentLimits
            {
                MaxPerPayment = 500.00m,
                MaxPerSession = 100.00m
            },
            Session = new SessionSettings
            {
                RequireApprovalForFirstPayment = true,
                CooldownSeconds = 2
            }
        });
    }

    private void SetupConfigurationWithLimits(decimal maxPerPayment, decimal maxPerSession)
    {
        _configServiceMock.Setup(c => c.Configuration).Returns(new UserBudgetConfiguration
        {
            Currency = "USD",
            Tiers = new TierThresholds
            {
                AutoApprove = 0.10m,
                LogAndApprove = 1.00m,
                FormConfirm = 10.00m,
                UrlConfirm = 100.00m
            },
            Limits = new PaymentLimits
            {
                MaxPerPayment = maxPerPayment,
                MaxPerSession = maxPerSession
            },
            Session = new SessionSettings
            {
                RequireApprovalForFirstPayment = false, // Disable for testing
                CooldownSeconds = 0 // Disable cooldown for testing
            }
        });
    }

    #region Configuration Tests

    [Fact]
    public void DefaultConfiguration_IsLoadedFromConfigService()
    {
        // Arrange & Act
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);
        var userConfig = service.GetUserConfiguration();

        // Assert
        userConfig.Tiers.AutoApprove.Should().Be(0.10m);
        userConfig.Tiers.LogAndApprove.Should().Be(1.00m);
        userConfig.Limits.MaxPerPayment.Should().Be(500.00m);
        userConfig.Limits.MaxPerSession.Should().Be(100.00m);
    }

    [Fact]
    public void GetConfig_ReturnsRuntimeState()
    {
        // Arrange
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        // Act
        var config = service.GetConfig();

        // Assert
        config.SessionSpent.Should().Be(0);
        config.RequestCount.Should().Be(0);
    }

    #endregion

    #region C-1 — Sync CheckBudget confirmation gate

    [Fact]
    public void CheckBudget_ConfirmationTierAmount_IsDenied_NotSilentlyAllowed()
    {
        // C-1 regression. The synchronous CheckBudget path is used by
        // send_onchain, settle_agent_service, and L402 auto-pay — none of which
        // have a confirmation/nonce flow. A payment the tier logic says
        // "requires confirmation" (FormConfirm/UrlConfirm) must therefore be
        // DENIED here, not silently allowed. Before the fix, CheckBudget mapped
        // any non-Deny result to Allow, so a $5 (FormConfirm-tier) payment was
        // auto-approved with no confirmation at all.
        SetupConfigurationWithLimits(500m, 100m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        // 500,000 sats = $5.00 (mock rate) → FormConfirm tier (>$1, <=$10).
        var result = service.CheckBudget(500_000);

        result.Allowed.Should().BeFalse(
            "a payment requiring confirmation must not be auto-approved on the synchronous budget path (C-1)");
    }

    [Fact]
    public void CheckBudget_AutoApproveTierAmount_StillAllowed()
    {
        // Regression guard for the C-1 fix: auto-approve-tier payments must
        // still proceed on the sync path (we only deny the confirmation tiers).
        SetupConfigurationWithLimits(500m, 100m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        // 5,000 sats = $0.05 (mock rate) → AutoApprove tier (<=$0.10).
        var result = service.CheckBudget(5_000);

        result.Allowed.Should().BeTrue(
            "auto-approve-tier payments still proceed without interactive confirmation");
    }

    [Fact]
    public void CheckBudget_NoSessionLimit_DoesNotOverflow()
    {
        // Regression guard: with no session limit configured, the remaining-session
        // budget is ~decimal.MaxValue, and the old "rough sats" conversion (* 100)
        // overflowed when cast to long (Copilot review of PR #30). The clamp must
        // keep CheckBudget from throwing and still allow an auto-approve-tier spend.
        _configServiceMock.Setup(c => c.Configuration).Returns(new UserBudgetConfiguration
        {
            Currency = "USD",
            Tiers = new TierThresholds
            {
                AutoApprove = 0.10m,
                LogAndApprove = 1.00m,
                FormConfirm = 10.00m,
                UrlConfirm = 100.00m
            },
            Limits = new PaymentLimits { MaxPerPayment = null, MaxPerSession = null },
            Session = new SessionSettings { RequireApprovalForFirstPayment = false, CooldownSeconds = 0 }
        });
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        // 5,000 sats = $0.05 (mock rate) → AutoApprove; must not throw despite no limits.
        BudgetCheckResult result = null!;
        var act = () => result = service.CheckBudget(5_000);

        act.Should().NotThrow();
        result.Allowed.Should().BeTrue();
        result.RemainingSessionBudget.Should().BeGreaterThan(0);
    }

    #endregion

    #region configure_budget — tighten-only runtime caps (decision C)

    [Fact]
    public async Task ConfigureBudget_TightensBelowConfig_Succeeds()
    {
        // Config caps are large ($500/$100 → 50M/10M sats at the mock rate), so
        // tightening to 1000/5000 sats is allowed.
        SetupConfigurationWithLimits(500m, 100m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        var result = await service.ConfigureBudgetAsync(1000, 5000);

        result.Success.Should().BeTrue();
        result.EffectivePerRequestSats.Should().Be(1000);
        result.EffectivePerSessionSats.Should().Be(5000);
        var cfg = service.GetConfig();
        cfg.RuntimeMaxPerRequestSats.Should().Be(1000);
        cfg.RuntimeMaxPerSessionSats.Should().Be(5000);
    }

    [Fact]
    public async Task ConfigureBudget_AttemptToRaiseAboveConfig_IsRejected()
    {
        // Config caps $0.01/$0.01 → 1000 sats each at the mock rate. Asking for 5000
        // tries to RAISE above the operator's limit — must be refused.
        SetupConfigurationWithLimits(0.01m, 0.01m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        var result = await service.ConfigureBudgetAsync(5000, 5000);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("can only LOWER");
    }

    [Fact]
    public async Task ConfigureBudget_CannotRaiseAboveExistingRuntimeCap()
    {
        SetupConfigurationWithLimits(500m, 100m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        (await service.ConfigureBudgetAsync(2000, 4000)).Success.Should().BeTrue();
        // Now try to raise the per-request cap back up to 3000 — must be refused.
        var result = await service.ConfigureBudgetAsync(3000, 4000);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("can only LOWER");
    }

    [Fact]
    public async Task ConfigureBudget_PerRequestCap_IsEnforcedInApprovalCheck()
    {
        SetupConfigurationWithLimits(500m, 100m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);
        await service.ConfigureBudgetAsync(1000, 5000);

        // 2000 sats exceeds the 1000-sat runtime per-request cap → Deny.
        var result = await service.CheckApprovalLevelAsync(2000);

        result.Level.Should().Be(ApprovalLevel.Deny);
        result.DenialReason.Should().Contain("runtime per-request cap");
    }

    [Fact]
    public async Task ConfigureBudget_PerSessionCap_IsEnforcedInApprovalCheck()
    {
        SetupConfigurationWithLimits(500m, 100m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);
        await service.ConfigureBudgetAsync(10000, 10000);
        service.RecordSpend(7000);

        // 7000 already spent + 5000 = 12000 > 10000 runtime session cap → Deny.
        var result = await service.CheckApprovalLevelAsync(5000);

        result.Level.Should().Be(ApprovalLevel.Deny);
        result.DenialReason.Should().Contain("runtime per-session cap");
    }

    [Theory]
    [InlineData(0, 5000)]      // per_request must be positive
    [InlineData(-1, 5000)]     // per_request must be positive
    [InlineData(1000, 0)]      // per_session must be positive
    [InlineData(6000, 5000)]   // per_request cannot exceed per_session
    public async Task ConfigureBudget_InvalidInputs_AreRejected(long perRequest, long perSession)
    {
        SetupConfigurationWithLimits(500m, 100m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        var result = await service.ConfigureBudgetAsync(perRequest, perSession);

        result.Success.Should().BeFalse();
    }

    #endregion

    #region fail-closed when BTC price is unavailable (H-1)

    [Fact]
    public async Task CheckApprovalLevel_PriceUnavailable_FailsClosed_Denies()
    {
        SetupConfigurationWithLimits(500m, 100m);
        _priceServiceMock.Setup(p => p.GetBtcPriceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PriceUnavailableException("all sources failed"));
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        var result = await service.CheckApprovalLevelAsync(5000);

        result.Level.Should().Be(ApprovalLevel.Deny);
        result.CanProceed.Should().BeFalse();
        result.DenialReason.Should().Contain("price");
    }

    [Fact]
    public void CheckBudget_PriceUnavailable_FailsClosed_NotAllowed()
    {
        // The sync path (send_onchain / L402 auto-pay) must also refuse, not throw.
        SetupConfigurationWithLimits(500m, 100m);
        _priceServiceMock.Setup(p => p.GetBtcPriceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PriceUnavailableException("all sources failed"));
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        var result = service.CheckBudget(5000);

        result.Allowed.Should().BeFalse();
    }

    #endregion

    #region Approval Level Tests

    [Fact]
    public async Task CheckApprovalLevel_SmallAmount_ReturnsAutoApprove()
    {
        // Arrange
        SetupConfigurationWithLimits(500m, 100m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        // 5 sats = $0.00005 (mock rate) - well below $0.10 auto-approve
        // Act
        var result = await service.CheckApprovalLevelAsync(5);

        // Assert
        result.Level.Should().Be(ApprovalLevel.AutoApprove);
        result.CanProceed.Should().BeTrue();
    }

    [Fact]
    public async Task CheckApprovalLevel_MediumAmount_ReturnsLogAndApprove()
    {
        // Arrange
        SetupConfigurationWithLimits(500m, 100m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        // 50000 sats = $0.50 (mock rate) - above $0.10, below $1.00
        // Act
        var result = await service.CheckApprovalLevelAsync(50000);

        // Assert
        result.Level.Should().Be(ApprovalLevel.LogAndApprove);
        result.CanProceed.Should().BeTrue();
    }

    [Fact]
    public async Task CheckApprovalLevel_LargeAmount_ReturnsFormConfirm()
    {
        // Arrange
        SetupConfigurationWithLimits(500m, 100m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        // 500000 sats = $5.00 (mock rate) - above $1.00, below $10.00
        // Act
        var result = await service.CheckApprovalLevelAsync(500000);

        // Assert
        result.Level.Should().Be(ApprovalLevel.FormConfirm);
        result.RequiresConfirmation.Should().BeTrue();
    }

    [Fact]
    public async Task CheckApprovalLevel_ExceedsPerPaymentLimit_ReturnsDeny()
    {
        // Arrange - set max per payment to $1.00
        SetupConfigurationWithLimits(1.00m, 100m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        // 200000 sats = $2.00 (mock rate) - exceeds $1.00 limit
        // Act
        var result = await service.CheckApprovalLevelAsync(200000);

        // Assert
        result.Level.Should().Be(ApprovalLevel.Deny);
        result.CanProceed.Should().BeFalse();
        result.DenialReason.Should().Contain("per-payment limit");
    }

    [Fact]
    public async Task CheckApprovalLevel_ExceedsSessionLimit_ReturnsDeny()
    {
        // Arrange - set max per session to $0.05
        SetupConfigurationWithLimits(500m, 0.05m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        // Record some spending first
        service.RecordSpend(3000); // $0.03 (mock rate)

        // Now try to spend 5000 more sats ($0.05) - would exceed $0.05 session limit
        // Act
        var result = await service.CheckApprovalLevelAsync(5000);

        // Assert
        result.Level.Should().Be(ApprovalLevel.Deny);
        result.CanProceed.Should().BeFalse();
        result.DenialReason.Should().Contain("session limit");
    }

    [Fact]
    public async Task CheckApprovalLevel_ZeroAmount_ReturnsAutoApprove()
    {
        // Arrange
        SetupConfigurationWithLimits(500m, 100m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        // Act
        var result = await service.CheckApprovalLevelAsync(0);

        // Assert
        result.Level.Should().Be(ApprovalLevel.AutoApprove);
        result.CanProceed.Should().BeTrue();
    }

    #endregion

    #region Legacy CheckBudget Tests

    [Fact]
    public void CheckBudget_WithinLimits_ReturnsAllowed()
    {
        // Arrange
        SetupConfigurationWithLimits(500m, 100m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        // Act
        var result = service.CheckBudget(5000); // $0.05 (mock rate)

        // Assert
        result.Allowed.Should().BeTrue();
        result.DenialReason.Should().BeNull();
    }

    [Fact]
    public void CheckBudget_ExceedsLimit_ReturnsDenied()
    {
        // Arrange - set max per payment to $0.01
        SetupConfigurationWithLimits(0.01m, 100m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        // Act
        var result = service.CheckBudget(5000); // $0.05 (mock rate) - exceeds $0.01

        // Assert
        result.Allowed.Should().BeFalse();
        result.DenialReason.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Session Tests

    [Fact]
    public void RecordSpend_AccumulatesCorrectly()
    {
        // Arrange
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        // Act
        service.RecordSpend(100);
        service.RecordSpend(200);
        service.RecordSpend(300);

        // Assert
        var config = service.GetConfig();
        config.SessionSpent.Should().Be(600);
        config.RequestCount.Should().Be(3);
    }

    [Fact]
    public void ResetSession_ClearsSpentAmount()
    {
        // Arrange
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);
        service.RecordSpend(500);
        service.RecordSpend(500);

        // Act
        service.ResetSession();
        var config = service.GetConfig();

        // Assert
        config.SessionSpent.Should().Be(0);
        config.RequestCount.Should().Be(0);
    }

    [Fact]
    public void ResetSession_UpdatesSessionStarted()
    {
        // Arrange
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);
        var originalStart = service.GetConfig().SessionStarted;

        // Wait briefly to ensure time difference
        Thread.Sleep(10);

        // Act
        service.ResetSession();
        var newStart = service.GetConfig().SessionStarted;

        // Assert
        newStart.Should().BeAfter(originalStart);
    }

    [Fact]
    public void GetConfig_ReturnsCopy_NotReference()
    {
        // Arrange
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        // Act
        var config1 = service.GetConfig();
        service.RecordSpend(100);
        var config2 = service.GetConfig();

        // Assert - config1 should not be affected by later changes
        config1.SessionSpent.Should().Be(0);
        config2.SessionSpent.Should().Be(100);
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task ConcurrentSpending_IsThreadSafe()
    {
        // Arrange
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);
        const int iterations = 100;
        const int amountPerSpend = 10;

        // Act
        var tasks = Enumerable.Range(0, iterations)
            .Select(_ => Task.Run(() => service.RecordSpend(amountPerSpend)))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert
        var config = service.GetConfig();
        config.SessionSpent.Should().Be(iterations * amountPerSpend);
        config.RequestCount.Should().Be(iterations);
    }

    [Fact]
    public async Task ConcurrentBudgetChecks_AreConsistent()
    {
        // Arrange
        SetupConfigurationWithLimits(500m, 100m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);
        const int iterations = 50;
        var results = new List<bool>();
        var lockObj = new object();

        // Act
        var tasks = Enumerable.Range(0, iterations)
            .Select(_ => Task.Run(() =>
            {
                var result = service.CheckBudget(50);
                lock (lockObj)
                {
                    results.Add(result.Allowed);
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert - all checks should be allowed (no spending happened)
        results.Should().HaveCount(iterations);
        results.Should().AllSatisfy(r => r.Should().BeTrue());
    }

    #endregion

    #region Computed Property Tests

    [Fact]
    public void RemainingSessionBudget_CalculatesCorrectly()
    {
        // Arrange
        SetupConfigurationWithLimits(500m, 100m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);
        service.RecordSpend(1000);
        service.RecordSpend(500);

        // Act
        var config = service.GetConfig();

        // Assert - SessionSpent should accumulate
        config.SessionSpent.Should().Be(1500);
    }

    [Fact]
    public async Task IsBudgetExhausted_WhenExhausted_ReturnsTrue()
    {
        // Arrange - set max session to $0.01 (= 1000 sats at the mock rate)
        SetupConfigurationWithLimits(500m, 0.01m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        // Trigger threshold caching by checking approval level
        await service.CheckApprovalLevelAsync(1);
        service.RecordSpend(1000);

        // Act
        var config = service.GetConfig();

        // Assert
        config.IsBudgetExhausted.Should().BeTrue();
    }

    [Fact]
    public async Task IsBudgetExhausted_WhenNotExhausted_ReturnsFalse()
    {
        // Arrange
        SetupConfigurationWithLimits(500m, 100m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        // Trigger threshold caching by checking approval level
        await service.CheckApprovalLevelAsync(1);
        service.RecordSpend(5000);

        // Act
        var config = service.GetConfig();

        // Assert
        config.IsBudgetExhausted.Should().BeFalse();
    }

    #endregion

    #region Cooldown Tests

    [Fact]
    public void IsCooldownElapsed_InitialState_ReturnsTrue()
    {
        // Arrange
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        // Act
        var elapsed = service.IsCooldownElapsed();

        // Assert
        elapsed.Should().BeTrue();
    }

    [Fact]
    public void RecordPaymentTime_ThenCheck_CooldownNotElapsed()
    {
        // Arrange - use longer cooldown for testing
        _configServiceMock.Setup(c => c.Configuration).Returns(new UserBudgetConfiguration
        {
            Session = new SessionSettings { CooldownSeconds = 60 } // Long cooldown
        });
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        // Act
        service.RecordPaymentTime();
        var elapsed = service.IsCooldownElapsed();

        // Assert
        elapsed.Should().BeFalse();
    }

    #endregion

    #region C-3 — confirmation is bound to the approved amount AND tool

    [Fact]
    public void Confirmation_BoundToAmountAndTool_RejectsMismatches_AndNonceNotConsumed()
    {
        SetupConfigurationWithLimits(500m, 100m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);
        var pending = service.CreatePendingConfirmation(1000, 0.01m, "pay_invoice", "inv...");

        // Wrong AMOUNT (right tool) → rejected; a 1,000-sat approval can't authorize 1,000,000.
        service.ValidateAndConsumeConfirmation(pending.Nonce, 1_000_000, "pay_invoice").Should().BeNull();

        // Wrong TOOL (right amount) → rejected; a pay_invoice code can't authorize send_onchain
        // (no cross-tool replay).
        service.ValidateAndConsumeConfirmation(pending.Nonce, 1000, "send_onchain").Should().BeNull();

        // Neither mismatch consumed the nonce — the correct (amount, tool) still works.
        var ok = service.ValidateAndConsumeConfirmation(pending.Nonce, 1000, "pay_invoice");
        ok.Should().NotBeNull();
        ok!.AmountSats.Should().Be(1000);

        // One-time use: a second consume fails.
        service.ValidateAndConsumeConfirmation(pending.Nonce, 1000, "pay_invoice").Should().BeNull();
    }

    #endregion
}

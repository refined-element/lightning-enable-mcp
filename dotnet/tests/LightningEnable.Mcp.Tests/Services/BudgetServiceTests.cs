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

        // Default price service (100k USD/BTC)
        _priceServiceMock.Setup(p => p.SatsToUsdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long sats, CancellationToken _) => sats / 100000m);
        _priceServiceMock.Setup(p => p.UsdToSatsAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((decimal usd, CancellationToken _) => (long)(usd * 100000));
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

    #region Approval Level Tests

    [Fact]
    public async Task CheckApprovalLevel_SmallAmount_ReturnsAutoApprove()
    {
        // Arrange
        SetupConfigurationWithLimits(500m, 100m);
        var service = new BudgetService(_configServiceMock.Object, _priceServiceMock.Object);

        // 5 sats = $0.00005 at 100k/BTC - well below $0.10 auto-approve
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

        // 50000 sats = $0.50 at 100k/BTC - above $0.10, below $1.00
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

        // 500000 sats = $5.00 at 100k/BTC - above $1.00, below $10.00
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

        // 200000 sats = $2.00 at 100k/BTC - exceeds $1.00 limit
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
        service.RecordSpend(3000); // $0.03 at 100k/BTC

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
        var result = service.CheckBudget(5000); // $0.05 at 100k/BTC

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
        var result = service.CheckBudget(5000); // $0.05 at 100k/BTC - exceeds $0.01

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
        // Arrange - set max session to $0.01 (1000 sats at 100k/BTC)
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
}

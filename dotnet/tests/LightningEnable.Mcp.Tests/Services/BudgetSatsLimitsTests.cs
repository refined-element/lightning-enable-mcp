using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// Sats-denominated budget limits — an alternative to the USD limits.
///
/// <para><c>limits.maxPerPaymentSats</c> / <c>limits.maxPerSessionSats</c> let an operator
/// bound an agent in the unit the agent actually spends. The point is that a sats budget has
/// NO price-feed dependency: three price sources being down must not stop a sats-budgeted
/// agent from paying, the way it (correctly) stops a USD-budgeted one that cannot be
/// evaluated.</para>
///
/// <para>When both denominations are set the STRICTER cap wins per check.</para>
///
/// <para>Mirrors <c>python/lightning-enable-mcp/tests/test_budget_sats_limits.py</c>.</para>
/// </summary>
public class BudgetSatsLimitsTests
{
    // 1 USD <-> 1,000 sats (BTC = $100,000). Not a real rate — a round one.
    private const decimal BtcUsd = 100_000m;

    private sealed class FakePriceService : IPriceService
    {
        private readonly bool _available;

        public FakePriceService(bool available) => _available = available;

        private void Guard()
        {
            if (!_available)
            {
                throw new PriceUnavailableException("all price sources failed");
            }
        }

        public Task<decimal> GetBtcPriceAsync(CancellationToken cancellationToken = default)
        {
            Guard();
            return Task.FromResult(BtcUsd);
        }

        public Task<long> UsdToSatsAsync(decimal usd, CancellationToken cancellationToken = default)
        {
            Guard();
            return Task.FromResult((long)(usd * 1_000m));
        }

        public Task<decimal> SatsToUsdAsync(long sats, CancellationToken cancellationToken = default)
        {
            Guard();
            return Task.FromResult(sats / 1_000m);
        }

        public decimal GetCachedBtcPrice() => _available ? BtcUsd : 0m;

        public PriceSnapshot? GetLastSnapshot() => null;
    }

    private sealed class FakeConfigService : IBudgetConfigurationService
    {
        public FakeConfigService(PaymentLimits limits)
        {
            Configuration = new UserBudgetConfiguration
            {
                Limits = limits,
                Session = new SessionSettings
                {
                    RequireApprovalForFirstPayment = false,
                    CooldownSeconds = 0,
                },
            };
        }

        public UserBudgetConfiguration Configuration { get; }
        public string ConfigFilePath => "/tmp/config.json";
        public bool ConfigFileExists => true;
        public void Reload() { }
    }

    private static BudgetService Service(PaymentLimits limits, bool priceAvailable = true) =>
        new(new FakeConfigService(limits), new FakePriceService(priceAvailable));

    /// <summary>Sats only — no USD limits at all.</summary>
    private static PaymentLimits SatsOnly() => new()
    {
        MaxPerPayment = null,
        MaxPerSession = null,
        MaxPerPaymentSats = 1_000,
        MaxPerSessionSats = 5_000,
    };

    // ── Sats-only budget, price feed down ───────────────────────────────────

    [Fact]
    public async Task SatsOnly_PriceDown_WithinCap_IsAllowed()
    {
        var result = await Service(SatsOnly(), priceAvailable: false).CheckApprovalLevelAsync(900);

        result.Level.Should().NotBe(ApprovalLevel.Deny,
            "a sats budget has no price dependency, so a price outage must not block it");
    }

    [Fact]
    public async Task SatsOnly_PriceDown_OverPerPaymentCap_IsDenied()
    {
        var result = await Service(SatsOnly(), priceAvailable: false).CheckApprovalLevelAsync(1_001);

        result.Level.Should().Be(ApprovalLevel.Deny);
        result.DenialReason.Should().Contain("1,000").And.Contain("sats");
    }

    [Fact]
    public async Task SatsOnly_PriceDown_OverSessionCap_IsDenied()
    {
        var service = Service(SatsOnly(), priceAvailable: false);

        (await service.CheckApprovalLevelAsync(900)).Level.Should().NotBe(ApprovalLevel.Deny);
        service.RecordSpend(4_500);

        var result = await service.CheckApprovalLevelAsync(900);

        result.Level.Should().Be(ApprovalLevel.Deny);
        result.DenialReason.Should().Contain("5,000");
    }

    [Fact]
    public async Task SatsOnly_PriceDown_ReservationIsGrantedAndBounded()
    {
        var service = Service(SatsOnly(), priceAvailable: false);

        (await service.TryReserveAsync(1_001)).DenialReason
            .Should().StartWith("Payment of 1,001 sats exceeds the per-payment cap",
                "the per-payment cap still applies with no price feed");

        // Five 1,000-sat reservations exactly fill the 5,000-sat session cap.
        for (var i = 0; i < 5; i++)
        {
            (await service.TryReserveAsync(1_000)).Success.Should().BeTrue(
                "TryReserveAsync must not fail closed when a sats budget is set");
        }

        var sixth = await service.TryReserveAsync(1_000);
        sixth.Success.Should().BeFalse();
        sixth.DenialReason.Should().Contain("session cap");
    }

    [Fact]
    public async Task NoSatsLimits_PriceDown_StillFailsClosed()
    {
        // Unchanged behaviour: a USD-only budget cannot be evaluated without a price.
        var usdOnly = new PaymentLimits { MaxPerPayment = 500m, MaxPerSession = 100m };
        var service = Service(usdOnly, priceAvailable: false);

        var approval = await service.CheckApprovalLevelAsync(900);
        approval.Level.Should().Be(ApprovalLevel.Deny);
        approval.DenialReason!.ToLowerInvariant().Should().Contain("price");

        (await service.TryReserveAsync(900)).Success.Should().BeFalse();
    }

    // ── Both denominations set: the stricter one wins ────────────────────────

    [Fact]
    public async Task SatsCapStricterThanUsd_SatsWins()
    {
        // $5.00 = 5,000 sats, but the sats cap is 1,000.
        var service = Service(new PaymentLimits
        {
            MaxPerPayment = 5.00m,
            MaxPerSession = 100.00m,
            MaxPerPaymentSats = 1_000,
        });

        (await service.CheckApprovalLevelAsync(1_500)).Level.Should().Be(ApprovalLevel.Deny);
        (await service.CheckApprovalLevelAsync(900)).Level.Should().NotBe(ApprovalLevel.Deny);
        (await service.TryReserveAsync(1_500)).Success.Should().BeFalse();
    }

    [Fact]
    public async Task UsdCapStricterThanSats_UsdWins()
    {
        // $1.00 = 1,000 sats, and the sats cap is a looser 50,000.
        var service = Service(new PaymentLimits
        {
            MaxPerPayment = 1.00m,
            MaxPerSession = 100.00m,
            MaxPerPaymentSats = 50_000,
        });

        (await service.CheckApprovalLevelAsync(1_500)).Level.Should().Be(ApprovalLevel.Deny);
        (await service.TryReserveAsync(1_500)).Success.Should().BeFalse();
        (await service.TryReserveAsync(900)).Success.Should().BeTrue();
    }

    [Fact]
    public async Task SessionCap_StricterOfTheTwoBinds()
    {
        // $10.00 = 10,000 sats session; the sats session cap is 2,000.
        var service = Service(new PaymentLimits
        {
            MaxPerPayment = 500.00m,
            MaxPerSession = 10.00m,
            MaxPerSessionSats = 2_000,
        });

        (await service.TryReserveAsync(1_500)).Success.Should().BeTrue();
        (await service.TryReserveAsync(1_000)).Success.Should().BeFalse(
            "the 2,000-sat session cap binds");
    }

    [Fact]
    public async Task RuntimeTighten_StillWinsWhenItIsTheStrictest()
    {
        var service = Service(new PaymentLimits
        {
            MaxPerPayment = 500.00m,
            MaxPerSession = 100.00m,
            MaxPerPaymentSats = 10_000,
            MaxPerSessionSats = 50_000,
        });

        (await service.ConfigureBudgetAsync(500, 1_000)).Success.Should().BeTrue();

        (await service.CheckApprovalLevelAsync(600)).Level.Should().Be(ApprovalLevel.Deny);
        (await service.TryReserveAsync(600)).Success.Should().BeFalse();
        (await service.TryReserveAsync(500)).Success.Should().BeTrue();
    }

    [Fact]
    public async Task Tighten_CannotRaiseAboveASatsConfigLimit()
    {
        // Tighten-only semantics extend to the sats config caps.
        var result = await Service(SatsOnly()).ConfigureBudgetAsync(999_999, 999_999);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("LOWER");
    }

    // ── Status reporting ────────────────────────────────────────────────────

    [Fact]
    public async Task Status_SatsOnly_ReportsSatsAsBinding()
    {
        var service = Service(SatsOnly(), priceAvailable: false);
        await service.CheckApprovalLevelAsync(100);

        var caps = service.GetEffectiveCaps();

        caps.PerPaymentSats.Should().Be(1_000);
        caps.PerSessionSats.Should().Be(5_000);
        caps.BindingDenomination.Should().Be("sats");
        caps.PerPaymentSource.Should().Contain("sats");
        caps.PriceAvailable.Should().BeFalse();
        caps.Note.ToLowerInvariant().Should().Contain("price");
    }

    [Fact]
    public async Task Status_UsdOnly_ReportsUsdAsBinding()
    {
        var service = Service(new PaymentLimits { MaxPerPayment = 5.00m, MaxPerSession = 100.00m });
        // Prime the USD -> sats cache so the status has converted caps to report.
        await service.CheckApprovalLevelAsync(100);

        var caps = service.GetEffectiveCaps();

        caps.BindingDenomination.Should().Be("usd");
        caps.PerPaymentSats.Should().Be(5_000);
    }

    [Fact]
    public async Task Status_Mixed_ReportsWhicheverBinds()
    {
        var service = Service(new PaymentLimits
        {
            MaxPerPayment = 5.00m,
            MaxPerSession = 100.00m,
            MaxPerPaymentSats = 1_000,
        });
        await service.CheckApprovalLevelAsync(100);

        var caps = service.GetEffectiveCaps();

        caps.BindingDenomination.Should().Be("sats", "the 1,000-sat cap beats $5.00");
        caps.PerPaymentSats.Should().Be(1_000);
    }

    [Fact]
    public async Task Status_RuntimeTighten_IsReportedAsBinding()
    {
        var service = Service(SatsOnly());
        await service.ConfigureBudgetAsync(100, 200);

        var caps = service.GetEffectiveCaps();

        caps.BindingDenomination.Should().Be("runtime");
        caps.PerPaymentSats.Should().Be(100);
    }

    [Fact]
    public void Status_CallerCanStateThePriceItJustObserved()
    {
        // `budget action=status` refreshes the price itself, so it passes what it found.
        var caps = Service(SatsOnly()).GetEffectiveCaps(usdAvailable: false);

        caps.PriceAvailable.Should().BeFalse();
        caps.BindingDenomination.Should().Be("sats");
    }
}

/// <summary>
/// The sats limits' plumbing: config file, env vars, and what an unusable value does.
/// </summary>
public class SatsLimitConfigurationTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "le-mcp-satslimits-" + Guid.NewGuid().ToString("N"));

    private readonly string? _originalPerPayment =
        Environment.GetEnvironmentVariable(BudgetConfigurationService.MaxPerPaymentSatsEnvVar);
    private readonly string? _originalPerSession =
        Environment.GetEnvironmentVariable(BudgetConfigurationService.MaxPerSessionSatsEnvVar);

    public SatsLimitConfigurationTests()
    {
        Directory.CreateDirectory(_dir);
        SetEnv(BudgetConfigurationService.MaxPerPaymentSatsEnvVar, null);
        SetEnv(BudgetConfigurationService.MaxPerSessionSatsEnvVar, null);
    }

    public void Dispose()
    {
        SetEnv(BudgetConfigurationService.MaxPerPaymentSatsEnvVar, _originalPerPayment);
        SetEnv(BudgetConfigurationService.MaxPerSessionSatsEnvVar, _originalPerSession);
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp dir must never fail a test run.
        }
        GC.SuppressFinalize(this);
    }

    private static void SetEnv(string name, string? value) =>
        Environment.SetEnvironmentVariable(name, value);

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void ReadFromTheConfigFile()
    {
        var path = WriteConfig("""{"limits": {"maxPerPaymentSats": 2500, "maxPerSessionSats": 40000}}""");

        var limits = new BudgetConfigurationService(path).Configuration.Limits;

        limits.MaxPerPaymentSats.Should().Be(2_500);
        limits.MaxPerSessionSats.Should().Be(40_000);
    }

    [Fact]
    public void EnvVarsOverrideTheConfigFile()
    {
        var path = WriteConfig("""{"limits": {"maxPerPaymentSats": 2500}}""");
        SetEnv(BudgetConfigurationService.MaxPerPaymentSatsEnvVar, "750");
        SetEnv(BudgetConfigurationService.MaxPerSessionSatsEnvVar, "9000");

        var limits = new BudgetConfigurationService(path).Configuration.Limits;

        limits.MaxPerPaymentSats.Should().Be(750);
        limits.MaxPerSessionSats.Should().Be(9_000);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("")]
    public void AMalformedEnvValueIsIgnoredNotTreatedAsNoLimit(string bad)
    {
        var path = WriteConfig("""{"limits": {"maxPerPaymentSats": 2500}}""");
        SetEnv(BudgetConfigurationService.MaxPerPaymentSatsEnvVar, bad);

        var limits = new BudgetConfigurationService(path).Configuration.Limits;

        limits.MaxPerPaymentSats.Should().Be(2_500,
            "an unusable env value must never silently widen the operator's budget");
    }

    [Fact]
    public void AbsentByDefault()
    {
        var path = WriteConfig("""{"limits": {"maxPerPayment": 5.0}}""");

        var limits = new BudgetConfigurationService(path).Configuration.Limits;

        limits.MaxPerPaymentSats.Should().BeNull();
        limits.MaxPerSessionSats.Should().BeNull();
        limits.HasSatsLimits.Should().BeFalse();
    }

    [Fact]
    public void ADefaultConfigFileDoesNotAdvertiseNullSatsLimits()
    {
        // The sats keys are opt-in; a first-run file should not carry two nulls the
        // operator has to reason about.
        var path = Path.Combine(_dir, "fresh", "config.json");

        _ = new BudgetConfigurationService(path);

        File.ReadAllText(path).Should().NotContain("maxPerPaymentSats");
    }
}

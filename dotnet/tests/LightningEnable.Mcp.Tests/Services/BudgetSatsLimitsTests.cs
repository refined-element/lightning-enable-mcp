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
        public FakeConfigService(PaymentLimits limits, SessionSettings? session = null)
        {
            Configuration = new UserBudgetConfiguration
            {
                Limits = limits,
                Session = session ?? new SessionSettings
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

    private static BudgetService Service(
        PaymentLimits limits, bool priceAvailable = true, SessionSettings? session = null) =>
        new(new FakeConfigService(limits, session), new FakePriceService(priceAvailable));

    /// <summary>Sats only — no USD limits at all.</summary>
    private static PaymentLimits SatsOnly() => new()
    {
        MaxPerPayment = null,
        MaxPerSession = null,
        MaxPerPaymentSats = 1_000,
        MaxPerSessionSats = 5_000,
    };

    /// <summary>Sats ceilings plus an explicit sats auto-approve tier.</summary>
    private static PaymentLimits SatsWithAutoApprove() => new()
    {
        MaxPerPayment = null,
        MaxPerSession = null,
        MaxPerPaymentSats = 1_000,
        MaxPerSessionSats = 5_000,
        AutoApproveSats = 100,
    };

    // ── Sats-only budget, price feed down ───────────────────────────────────

    [Fact]
    public async Task SatsOnly_PriceDown_WithinCap_IsNotDenied()
    {
        var result = await Service(SatsOnly(), priceAvailable: false).CheckApprovalLevelAsync(900);

        result.Level.Should().NotBe(ApprovalLevel.Deny,
            "a sats budget has no price dependency, so a price outage must not block it");
        result.CanProceed.Should().BeTrue();
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

    // ── Tiering during a price outage ───────────────────────────────────────
    //
    // With no BTC price the USD tier ladder cannot be evaluated. The sats CEILINGS still
    // bound the spend, but a ceiling is not an approval tier: it says "never more than
    // this", not "this much is fine unattended". So the only thing that may auto-approve
    // during an outage is an explicit sats tier the operator set.

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(100)]
    public async Task Outage_AtOrBelowAutoApproveSats_IsAutoApproved(long amountSats)
    {
        var result = await Service(SatsWithAutoApprove(), priceAvailable: false)
            .CheckApprovalLevelAsync(amountSats);

        result.Level.Should().Be(ApprovalLevel.AutoApprove);
        result.RequiresConfirmation.Should().BeFalse();
    }

    [Fact]
    public async Task Outage_AboveAutoApproveSats_RequiresConfirmation()
    {
        var result = await Service(SatsWithAutoApprove(), priceAvailable: false)
            .CheckApprovalLevelAsync(101);

        result.RequiresConfirmation.Should().BeTrue();
        result.Level.Should().Be(ApprovalLevel.FormConfirm);
        result.CanProceed.Should().BeTrue("confirmation is a gate, not a denial");
        result.ConfirmationMessage.Should().Contain("100").And.Contain("sats");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    [InlineData(1_000)]
    public async Task Outage_WithoutAutoApproveSats_EveryPaymentRequiresConfirmation(long amountSats)
    {
        // No explicit tier means no unattended spending while the price is down.
        var result = await Service(SatsOnly(), priceAvailable: false)
            .CheckApprovalLevelAsync(amountSats);

        result.RequiresConfirmation.Should().BeTrue();
        result.Level.Should().Be(ApprovalLevel.FormConfirm);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(900)]
    public async Task Outage_NeverLogAndApprove(long amountSats)
    {
        // LogAndApprove proceeds unattended — it is not an option without a price.
        foreach (var limits in new[] { SatsOnly(), SatsWithAutoApprove() })
        {
            var result = await Service(limits, priceAvailable: false)
                .CheckApprovalLevelAsync(amountSats);

            result.Level.Should().NotBe(ApprovalLevel.LogAndApprove);
        }
    }

    [Fact]
    public async Task Outage_TheHardCeilingStillDeniesAboveIt()
    {
        var result = await Service(SatsWithAutoApprove(), priceAvailable: false)
            .CheckApprovalLevelAsync(1_001);

        result.Level.Should().Be(ApprovalLevel.Deny,
            "a ceiling is checked before any tier — confirmation cannot buy past it");
    }

    [Fact]
    public async Task Outage_AnAutoApproveTierAboveTheCeilingCannotWidenIt()
    {
        // A misconfigured tier must not become a way around the ceiling.
        var service = Service(
            new PaymentLimits
            {
                MaxPerPayment = null,
                MaxPerSession = null,
                MaxPerPaymentSats = 1_000,
                AutoApproveSats = 999_999,
            },
            priceAvailable: false);

        (await service.CheckApprovalLevelAsync(1_001)).Level.Should().Be(ApprovalLevel.Deny);
        (await service.TryReserveAsync(1_001)).Success.Should().BeFalse();
    }

    [Fact]
    public async Task Outage_FirstPaymentApprovalSettingIsStillHonoured()
    {
        var service = Service(
            SatsWithAutoApprove(),
            priceAvailable: false,
            session: new SessionSettings
            {
                RequireApprovalForFirstPayment = true,
                CooldownSeconds = 0,
            });

        (await service.CheckApprovalLevelAsync(50)).RequiresConfirmation.Should().BeTrue(
            "the operator asked for the first payment of the session to be confirmed");

        service.RecordSpend(50);
        (await service.CheckApprovalLevelAsync(50)).Level.Should().Be(ApprovalLevel.AutoApprove);
    }

    [Fact]
    public async Task Outage_TheCooldownStillApplies()
    {
        var service = Service(
            SatsWithAutoApprove(),
            priceAvailable: false,
            session: new SessionSettings
            {
                RequireApprovalForFirstPayment = false,
                CooldownSeconds = 60,
            });
        service.RecordPaymentTime();

        var result = await service.CheckApprovalLevelAsync(50);

        result.Level.Should().Be(ApprovalLevel.Deny);
        result.DenialReason.Should().Contain("Cooldown");
    }

    [Fact]
    public async Task AutoApproveSats_IsIgnoredWhenThePriceIsAvailable()
    {
        // It is an outage-only tier: with a price, the USD ladder decides as always.
        var service = Service(new PaymentLimits
        {
            MaxPerPayment = 500.00m,
            MaxPerSession = 100.00m,
            MaxPerPaymentSats = 1_000_000,
            AutoApproveSats = 1,
        });

        // 500 sats = $0.50, under the $1.00 autoApprove USD tier -> auto-approved even
        // though it is far above the 1-sat outage tier.
        (await service.CheckApprovalLevelAsync(500)).Level.Should().Be(ApprovalLevel.AutoApprove);
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
    public async Task Status_OutageModeAndTheSatsAutoApproveTierAreReported()
    {
        var service = Service(SatsWithAutoApprove(), priceAvailable: false);
        await service.CheckApprovalLevelAsync(50);

        var caps = service.GetEffectiveCaps();

        caps.AutoApproveSats.Should().Be(100);
        caps.OutageModeActive.Should().BeTrue();
        caps.Note.ToLowerInvariant().Should().Contain("confirmation");
    }

    [Fact]
    public async Task Status_OutageModeIsOffWhileThePriceIsAvailable()
    {
        var service = Service(SatsWithAutoApprove());
        await service.CheckApprovalLevelAsync(50);

        var caps = service.GetEffectiveCaps();

        caps.OutageModeActive.Should().BeFalse();
        caps.AutoApproveSats.Should().Be(100, "still reported, just not in force");
    }

    [Fact]
    public void Status_OutageModeIsOffWhenNoSatsLimitsCanCarryTheBudget()
    {
        // Without sats limits an outage refuses payments outright — that is not "outage
        // mode", it is the pre-existing fail-closed path.
        var caps = Service(new PaymentLimits { MaxPerPayment = 5.00m, MaxPerSession = 100.00m })
            .GetEffectiveCaps(usdAvailable: false);

        caps.OutageModeActive.Should().BeFalse();
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

    private readonly string? _originalAutoApprove =
        Environment.GetEnvironmentVariable(BudgetConfigurationService.AutoApproveSatsEnvVar);

    public SatsLimitConfigurationTests()
    {
        Directory.CreateDirectory(_dir);
        SetEnv(BudgetConfigurationService.MaxPerPaymentSatsEnvVar, null);
        SetEnv(BudgetConfigurationService.MaxPerSessionSatsEnvVar, null);
        SetEnv(BudgetConfigurationService.AutoApproveSatsEnvVar, null);
    }

    public void Dispose()
    {
        SetEnv(BudgetConfigurationService.MaxPerPaymentSatsEnvVar, _originalPerPayment);
        SetEnv(BudgetConfigurationService.MaxPerSessionSatsEnvVar, _originalPerSession);
        SetEnv(BudgetConfigurationService.AutoApproveSatsEnvVar, _originalAutoApprove);
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
        limits.AutoApproveSats.Should().BeNull();
        limits.HasSatsLimits.Should().BeFalse();
    }

    [Fact]
    public void AutoApproveSatsFromTheConfigFile()
    {
        var path = WriteConfig("""{"limits": {"maxPerPaymentSats": 2500, "autoApproveSats": 250}}""");

        new BudgetConfigurationService(path).Configuration.Limits.AutoApproveSats
            .Should().Be(250);
    }

    [Fact]
    public void AutoApproveSatsEnvVarOverridesTheConfigFile()
    {
        var path = WriteConfig("""{"limits": {"autoApproveSats": 250}}""");
        SetEnv(BudgetConfigurationService.AutoApproveSatsEnvVar, "42");

        new BudgetConfigurationService(path).Configuration.Limits.AutoApproveSats
            .Should().Be(42);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("")]
    public void AMalformedAutoApproveEnvValueIsIgnored(string bad)
    {
        var path = WriteConfig("""{"limits": {"autoApproveSats": 250}}""");
        SetEnv(BudgetConfigurationService.AutoApproveSatsEnvVar, bad);

        new BudgetConfigurationService(path).Configuration.Limits.AutoApproveSats
            .Should().Be(250,
                "an unusable env value must never silently raise the unattended-spend tier");
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

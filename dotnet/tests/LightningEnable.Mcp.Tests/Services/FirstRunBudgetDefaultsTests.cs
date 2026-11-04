using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// Pins the budget defaults (written to <c>~/.lightning-enable/config.json</c> on first run,
/// and used when an existing file omits a key) against
/// <c>shared/first-run-budget-defaults.json</c> at the repo root, and the repair of a partial
/// or out-of-order tier ladder against <c>shared/partial-tier-fixtures.json</c>. The Python
/// port's <c>tests/test_first_run_budget_defaults.py</c> reads the same fixtures, so the two
/// ports cannot drift apart again.
/// </summary>
public class FirstRunBudgetDefaultsTests : IDisposable
{
    private static readonly string[] Sections = ["tiers", "limits", "session"];

    // Top-level keys each port writes that the fixture does not pin (their shapes are
    // port-specific: credentials and the approval channel, all null by default). This port
    // also writes a null top-level lightningEnableApiKey; the Python port does not.
    private static readonly HashSet<string> UnpinnedTopLevel =
        ["wallets", "confirmation", "lightningEnableApiKey"];

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "le-mcp-firstrun-" + Guid.NewGuid().ToString("N"));

    public FirstRunBudgetDefaultsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
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

    private static string FindShared(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "shared", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"shared/{name} not found above the test output directory.");
    }

    private UserBudgetConfiguration LoadFrom(string json)
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, json);
        return new BudgetConfigurationService(path).Configuration;
    }

    // ── First run ────────────────────────────────────────────────────────────

    [Fact]
    public void FirstRunWritesTheSharedDefaultsAndNoOtherKeys()
    {
        var path = Path.Combine(_dir, "config.json");
        _ = new BudgetConfigurationService(path);
        File.Exists(path).Should().BeTrue("first run must write a default config file");

        var written = File.ReadAllText(path);
        AssertMatchesFixture(written);
        AssertKeySets(written);
    }

    // ── Fallbacks for an existing config.json that omits keys ────────────────
    // Owner decision 2026-11-03: a missing key falls back to the first-run set.

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"tiers": {}, "limits": {}, "session": {}}""")]
    public void APartialConfigFileFallsBackToTheSharedDefaults(string partial)
    {
        var json = JsonSerializer.Serialize(LoadFrom(partial), CamelCase);

        AssertMatchesFixture(json);
    }

    [Fact]
    public void ModelDefaultsMatchTheSharedDefaults()
    {
        AssertMatchesFixture(JsonSerializer.Serialize(new UserBudgetConfiguration(), CamelCase));
    }

    [Fact]
    public void ExplicitNullLimitsStillMeanNoLimit()
    {
        var limits = LoadFrom("""{"limits": {"maxPerPayment": null, "maxPerSession": null}}""").Limits;

        limits.MaxPerPayment.Should().BeNull();
        limits.MaxPerSession.Should().BeNull();
    }

    // ── Out-of-order tiers: clamp lower tiers down, never raise one ──────────

    [Theory]
    [InlineData("""{"autoApprove": 2}""", "1", "1", "10", "100")]
    [InlineData("""{"logAndApprove": 20}""", "0.10", "10", "10", "100")]
    [InlineData("""{"formConfirm": 0.5}""", "0.10", "0.5", "0.5", "100")]
    public void ClampRuleLowersTheLowerTier(
        string tiersJson, string auto, string log, string form, string url)
    {
        var tiers = JsonSerializer.Deserialize<TierThresholds>(tiersJson)!;

        var clamped = BudgetConfigurationService.ClampTierLadder(tiers);

        clamped.AutoApprove.Should().Be(decimal.Parse(auto, System.Globalization.CultureInfo.InvariantCulture));
        clamped.LogAndApprove.Should().Be(decimal.Parse(log, System.Globalization.CultureInfo.InvariantCulture));
        clamped.FormConfirm.Should().Be(decimal.Parse(form, System.Globalization.CultureInfo.InvariantCulture));
        clamped.UrlConfirm.Should().Be(decimal.Parse(url, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ClampNeverRaisesATier()
    {
        var original = new TierThresholds
        {
            AutoApprove = 50m, LogAndApprove = 40m, FormConfirm = 30m, UrlConfirm = 20m
        };

        var clamped = BudgetConfigurationService.ClampTierLadder(original);

        clamped.AutoApprove.Should().BeLessThanOrEqualTo(50m);
        clamped.LogAndApprove.Should().BeLessThanOrEqualTo(40m);
        clamped.FormConfirm.Should().BeLessThanOrEqualTo(30m);
        clamped.UrlConfirm.Should().Be(20m);
        new[] { clamped.AutoApprove, clamped.LogAndApprove, clamped.FormConfirm, clamped.UrlConfirm }
            .Should().BeInAscendingOrder();
    }

    public static IEnumerable<object[]> PartialTierCases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FindShared("partial-tier-fixtures.json")));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return
            [
                c.GetProperty("name").GetString()!,
                c.GetProperty("tiers").GetRawText(),
                c.GetProperty("expected").GetRawText()
            ];
        }
    }

    [Theory]
    [MemberData(nameof(PartialTierCases))]
    public void APartialTierFileMatchesTheSharedLadder(string name, string tiersJson, string expectedJson)
    {
        var tiers = LoadFrom($$"""{"tiers": {{tiersJson}}}""").Tiers;

        using var expected = JsonDocument.Parse(expectedJson);
        var e = expected.RootElement;
        tiers.AutoApprove.Should().Be(e.GetProperty("autoApprove").GetDecimal(), name);
        tiers.LogAndApprove.Should().Be(e.GetProperty("logAndApprove").GetDecimal(), name);
        tiers.FormConfirm.Should().Be(e.GetProperty("formConfirm").GetDecimal(), name);
        tiers.UrlConfirm.Should().Be(e.GetProperty("urlConfirm").GetDecimal(), name);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AssertMatchesFixture(string writtenJson)
    {
        using var written = JsonDocument.Parse(writtenJson);
        using var expected = JsonDocument.Parse(File.ReadAllText(FindShared("first-run-budget-defaults.json")));

        written.RootElement.GetProperty("currency").GetString()
            .Should().Be(expected.RootElement.GetProperty("currency").GetString());

        foreach (var section in Sections)
        {
            var actualSection = written.RootElement.GetProperty(section);
            foreach (var field in expected.RootElement.GetProperty(section).EnumerateObject())
            {
                var actual = actualSection.GetProperty(field.Name);
                if (field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    actual.GetBoolean().Should().Be(field.Value.GetBoolean(), $"{section}.{field.Name}");
                }
                else
                {
                    actual.GetDecimal().Should().Be(field.Value.GetDecimal(), $"{section}.{field.Name}");
                }
            }
        }
    }

    /// <summary>No key beyond the fixture's in the pinned sections, and no stray sections.</summary>
    private static void AssertKeySets(string writtenJson)
    {
        using var written = JsonDocument.Parse(writtenJson);
        using var expected = JsonDocument.Parse(File.ReadAllText(FindShared("first-run-budget-defaults.json")));

        var pinned = expected.RootElement.EnumerateObject()
            .Select(p => p.Name).Where(n => !n.StartsWith('_')).ToHashSet();
        written.RootElement.EnumerateObject().Select(p => p.Name)
            .Where(n => !pinned.Contains(n))
            .Should().BeSubsetOf(UnpinnedTopLevel);

        foreach (var section in Sections)
        {
            written.RootElement.GetProperty(section).EnumerateObject().Select(p => p.Name)
                .Should().BeEquivalentTo(
                    expected.RootElement.GetProperty(section).EnumerateObject().Select(p => p.Name),
                    section);
        }
    }
}

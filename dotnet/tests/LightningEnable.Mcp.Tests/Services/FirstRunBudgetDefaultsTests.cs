using System.Text.Json;
using LightningEnable.Mcp.Services;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// Pins the budget defaults written to <c>~/.lightning-enable/config.json</c> on first run
/// against <c>shared/first-run-budget-defaults.json</c> at the repo root. The Python port's
/// <c>tests/test_first_run_budget_defaults.py</c> reads the same fixture, so the two ports
/// cannot drift apart again (they did: Python wrote $1 / $5 / $25 tiers with first-payment
/// approval off while this port wrote $0.10 / $1 / $10 with it on).
/// </summary>
public class FirstRunBudgetDefaultsTests : IDisposable
{
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

    private static string FindFixture()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "shared", "first-run-budget-defaults.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "shared/first-run-budget-defaults.json not found above the test output directory.");
    }

    [Fact]
    public void FirstRunWritesTheSharedCrossPortDefaults()
    {
        var path = Path.Combine(_dir, "config.json");
        _ = new BudgetConfigurationService(path);
        File.Exists(path).Should().BeTrue("first run must write a default config file");

        using var written = JsonDocument.Parse(File.ReadAllText(path));
        using var expected = JsonDocument.Parse(File.ReadAllText(FindFixture()));

        written.RootElement.GetProperty("currency").GetString()
            .Should().Be(expected.RootElement.GetProperty("currency").GetString());

        foreach (var section in new[] { "tiers", "limits", "session" })
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
}

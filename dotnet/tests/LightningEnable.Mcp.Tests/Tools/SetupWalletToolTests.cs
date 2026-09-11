using System.Text.Json;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;

namespace LightningEnable.Mcp.Tests.Tools;

/// <summary>
/// <c>setup_wallet</c> — the first tool an agent calls, and the only one that writes a
/// wallet credential to disk.
///
/// <para>The probe is stubbed: what is under test is the ORDER of the guards (parse, then
/// env-precedence, then live probe, then write) and what each one is allowed to say. A real
/// relay is covered by the NWC wallet tests.</para>
/// </summary>
public class SetupWalletToolTests
{
    private const string ValidNwc =
        "nostr+walletconnect://0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        + "?relay=wss://relay.example.com"
        + "&secret=fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private const string SecretFixture =
        "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    /// <summary>A scriptable onboarding seam: no relay, no home directory.</summary>
    private sealed class StubOnboarding : IWalletOnboardingService
    {
        public WalletSetupState State { get; set; } = new(
            false, null, null, false, Array.Empty<string>(), "/tmp/config.json");

        public NwcProbeResult Probe { get; set; } =
            new(true, null, new[] { "pay_invoice", "get_balance" }, 12_345, "test wallet");

        public Exception? SaveThrows { get; set; }

        public string? Saved { get; private set; }
        public int SaveCalls { get; private set; }
        public string? Probed { get; private set; }

        /// <summary>What the server's own wallet would hand to <c>configure_receive</c>.</summary>
        public string? OwnNwcConnectionString { get; set; }

        public WalletSetupState Describe() => State;

        public string? ResolveOwnNwcConnectionString() => OwnNwcConnectionString;

        public Task<NwcProbeResult> ProbeNwcAsync(string connectionString, CancellationToken ct = default)
        {
            Probed = connectionString;
            return Task.FromResult(Probe);
        }

        public void SaveNwcConnectionString(string connectionString)
        {
            SaveCalls++;
            if (SaveThrows != null)
            {
                throw SaveThrows;
            }
            Saved = connectionString;
        }
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ── No arguments: report ────────────────────────────────────────────────

    [Fact]
    public async Task NoArguments_NoWallet_ReturnsAGuidedPath()
    {
        var onboarding = new StubOnboarding();

        var result = Parse(await SetupWalletTool.SetupWallet(onboarding: onboarding));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("configured").GetBoolean().Should().BeFalse();
        result.GetProperty("configFile").GetString().Should().Be("/tmp/config.json");

        var options = result.GetProperty("options").EnumerateArray()
            .Select(o => o.GetProperty("wallet").GetString() ?? string.Empty)
            .ToList();
        options.Should().Contain(w => w.Contains("NWC"), "NWC is the recommended on-ramp");
        options.Should().Contain(w => w.Contains("LND"));
        options.Should().Contain(w => w.Contains("Strike"));

        result.GetProperty("configFileShape").GetString()
            .Should().Contain("wallets").And.Contain("nwcConnectionString",
                "the report must show the exact config shape a human can write by hand");
    }

    [Fact]
    public async Task NoArguments_WalletConfigured_NamesTheProviderAndSourceButNoCredential()
    {
        var onboarding = new StubOnboarding
        {
            State = new(true, "NWC", "environment (NWC_CONNECTION_STRING)", true,
                new[] { "NWC" }, "/tmp/config.json"),
        };

        var json = await SetupWalletTool.SetupWallet(onboarding: onboarding);
        var result = Parse(json);

        result.GetProperty("configured").GetBoolean().Should().BeTrue();
        result.GetProperty("wallet").GetString().Should().Be("NWC");
        result.GetProperty("source").GetString().Should().Contain("NWC_CONNECTION_STRING");
        json.Should().NotContain(SecretFixture, "the credential is never reported");
    }

    // ── With a connection string ────────────────────────────────────────────

    [Fact]
    public async Task ValidString_ProbeSucceeds_IsSavedAndTheWalletIsDescribed()
    {
        var onboarding = new StubOnboarding();

        var json = await SetupWalletTool.SetupWallet(ValidNwc, onboarding);
        var result = Parse(json);

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("saved").GetBoolean().Should().BeTrue();
        onboarding.Saved.Should().Be(ValidNwc);
        onboarding.Probed.Should().Be(ValidNwc, "the string must be probed before it is written");

        result.GetProperty("balanceSats").GetInt64().Should().Be(12_345);
        result.GetProperty("canPayL402").GetBoolean().Should().BeTrue();
        result.GetProperty("methods").EnumerateArray().Select(m => m.GetString())
            .Should().Contain("pay_invoice");
        result.GetProperty("relay").GetString().Should().Be("wss://relay.example.com");
        result.GetProperty("nextStep").GetString().Should().Contain("test_l402_payment");

        json.Should().NotContain(SecretFixture, "the saved credential is never echoed back");
    }

    [Fact]
    public async Task ValidString_WalletCannotPay_SaysSoInsteadOfClaimingL402Works()
    {
        var onboarding = new StubOnboarding
        {
            Probe = new(true, null, new[] { "get_balance", "make_invoice" }, 0, null),
        };

        var result = Parse(await SetupWalletTool.SetupWallet(ValidNwc, onboarding));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("canPayL402").GetBoolean().Should().BeFalse();
        result.GetProperty("note").GetString().Should().Contain("pay_invoice");
    }

    [Fact]
    public async Task EnvironmentAlreadySelectsAWallet_RefusesAndWritesNothing()
    {
        var onboarding = new StubOnboarding
        {
            State = new(true, "Strike", "environment (STRIKE_API_KEY)", true,
                new[] { "Strike" }, "/tmp/config.json"),
        };

        var result = Parse(await SetupWalletTool.SetupWallet(ValidNwc, onboarding));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("saved").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString()
            .Should().Contain("STRIKE_API_KEY").And.Contain("precedence");
        result.GetProperty("howToProceed").GetString().Should().Contain("Unset");

        onboarding.SaveCalls.Should().Be(0, "env wins, so the config file must not be touched");
        onboarding.Probed.Should().BeNull("a refused setup must not open a relay connection either");
    }

    [Fact]
    public async Task ConfigFileAlreadyHasAWallet_IsOverwritten()
    {
        // Only the ENVIRONMENT blocks a write. Replacing the connection string in the config
        // file is exactly what re-running setup_wallet is for.
        var onboarding = new StubOnboarding
        {
            State = new(true, "NWC", "config file (/tmp/config.json)", false,
                new[] { "NWC" }, "/tmp/config.json"),
        };

        var result = Parse(await SetupWalletTool.SetupWallet(ValidNwc, onboarding));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        onboarding.Saved.Should().Be(ValidNwc);
    }

    [Theory]
    [InlineData("", "no wallet is configured yet, so a blank string is just a report")]
    [InlineData("   ", "whitespace is treated the same as omitted")]
    public async Task BlankString_IsTreatedAsAReportRequest(string blank, string because)
    {
        var onboarding = new StubOnboarding();

        var result = Parse(await SetupWalletTool.SetupWallet(blank, onboarding));

        result.GetProperty("success").GetBoolean().Should().BeTrue(because);
        result.TryGetProperty("options", out _).Should().BeTrue();
        onboarding.SaveCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("https://example.com", "wrong scheme")]
    [InlineData("nostr+walletconnect://tooshort?relay=wss://r.example.com&secret=abc", "bad pubkey")]
    [InlineData(
        "nostr+walletconnect://0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        "no relay or secret")]
    [InlineData(
        "nostr+walletconnect://0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        + "?relay=relay.example.com&secret="
        + "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210",
        "relay has no ws/wss scheme")]
    public async Task InvalidString_FailsWithADescriptiveErrorAndNeverProbesOrSaves(
        string connectionString, string why)
    {
        var onboarding = new StubOnboarding();

        var json = await SetupWalletTool.SetupWallet(connectionString, onboarding);
        var result = Parse(json);

        result.GetProperty("success").GetBoolean().Should().BeFalse(why);
        result.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
        result.GetProperty("expectedShape").GetString().Should().Contain("nostr+walletconnect://");

        onboarding.Probed.Should().BeNull("a malformed string must never reach a relay");
        onboarding.SaveCalls.Should().Be(0);
        json.Should().NotContain(SecretFixture);
    }

    [Fact]
    public async Task ProbeFails_NothingIsSavedAndTheErrorIsActionable()
    {
        var onboarding = new StubOnboarding
        {
            Probe = NwcProbeResult.Failed("the wallet did not answer within 10 seconds"),
        };

        var result = Parse(await SetupWalletTool.SetupWallet(ValidNwc, onboarding));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("saved").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Contain("did not answer");
        result.GetProperty("hint").GetString().Should().Contain("Nothing was saved");

        onboarding.SaveCalls.Should().Be(0,
            "a credential that cannot reach its wallet must not be persisted");
    }

    [Fact]
    public async Task SaveFails_ReportsTheFailureAndTheManualConfigShape()
    {
        var onboarding = new StubOnboarding
        {
            SaveThrows = new IOException("config.json is read-only"),
        };

        var result = Parse(await SetupWalletTool.SetupWallet(ValidNwc, onboarding));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Contain("read-only");
        result.GetProperty("configFileShape").GetString().Should().Contain("nwcConnectionString");
    }

    [Fact]
    public async Task NoOnboardingService_ReportsItRatherThanThrowing()
    {
        var result = Parse(await SetupWalletTool.SetupWallet());

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
    }
}

/// <summary>
/// The onboarding service itself, exercised against a real temp config file. The relay
/// probe is not covered here (it needs a socket) — the tool tests stub it.
/// </summary>
public class WalletOnboardingServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "le-mcp-onboarding-" + Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_dir, "config.json");

    public WalletOnboardingServiceTests() => Directory.CreateDirectory(_dir);

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

    private const string ValidNwc =
        "nostr+walletconnect://0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        + "?relay=wss://relay.example.com"
        + "&secret=fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    [Fact]
    public void Save_CreatesTheFileAndTheWalletsSection()
    {
        var service = new WalletOnboardingService(ConfigPath);

        service.SaveNwcConnectionString(ValidNwc);

        var root = JsonDocument.Parse(File.ReadAllText(ConfigPath)).RootElement;
        root.GetProperty("wallets").GetProperty("nwcConnectionString").GetString()
            .Should().Be(ValidNwc);
    }

    [Fact]
    public void Save_PreservesEveryOtherKeyInTheOperatorsConfig()
    {
        // The file is the operator's: their limits, and any key this build does not know
        // about, must survive an agent-triggered wallet setup untouched.
        File.WriteAllText(ConfigPath, """
            {
              "currency": "USD",
              "limits": { "maxPerPayment": 7.5, "maxPerSession": 25 },
              "somethingThisBuildDoesNotKnow": { "keep": true },
              "wallets": { "strikeApiKey": "fixture-strike-value" }
            }
            """);

        new WalletOnboardingService(ConfigPath).SaveNwcConnectionString(ValidNwc);

        var root = JsonDocument.Parse(File.ReadAllText(ConfigPath)).RootElement;
        root.GetProperty("limits").GetProperty("maxPerPayment").GetDecimal().Should().Be(7.5m);
        root.GetProperty("limits").GetProperty("maxPerSession").GetDecimal().Should().Be(25m);
        root.GetProperty("somethingThisBuildDoesNotKnow").GetProperty("keep").GetBoolean()
            .Should().BeTrue();
        root.GetProperty("wallets").GetProperty("strikeApiKey").GetString()
            .Should().Be("fixture-strike-value");
        root.GetProperty("wallets").GetProperty("nwcConnectionString").GetString()
            .Should().Be(ValidNwc);
    }

    [Fact]
    public void Save_UnparseableConfig_RefusesRatherThanOverwritingTheOperatorsLimits()
    {
        File.WriteAllText(ConfigPath, "{ this is not json");

        var act = () => new WalletOnboardingService(ConfigPath).SaveNwcConnectionString(ValidNwc);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not valid JSON*");
    }

    [Fact]
    public void Save_RestrictsFilePermissionsOnPosix()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Windows uses icacls; asserting an ACL here would test the OS, not us.
        }

        new WalletOnboardingService(ConfigPath).SaveNwcConnectionString(ValidNwc);

        File.GetUnixFileMode(ConfigPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            "the file now holds a live wallet credential in plaintext");
    }
}

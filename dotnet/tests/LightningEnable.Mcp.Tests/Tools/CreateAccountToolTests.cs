using System.Text.Json;
using System.Text.Json.Nodes;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Tools;

/// <summary>
/// Tests for the self-bootstrapping create_lightning_enable_account tool.
/// Mirrors AgentSettleToolTests (L402 fetch) + PayInvoiceToolTests (out-of-band
/// confirmation): asserts it POSTs the email, honors budget/confirmation, writes
/// the API key into config, and returns the key.
/// </summary>
public class CreateAccountToolTests : IDisposable
{
    private readonly Mock<IL402HttpClient> _l402ClientMock = new();
    private readonly Mock<IBudgetService> _budgetServiceMock = new();
    private readonly Mock<IBudgetConfigurationService> _configServiceMock = new();
    private readonly List<string> _tempFiles = new();

    private const string TestEmail = "agent@example.com";

    private const string AccountJson =
        "{\"status\":\"active\",\"merchantId\":\"merch_123\",\"apiKey\":\"le_live_abc123\"," +
        "\"email\":\"agent@example.com\",\"planTier\":\"individual\",\"subscriptionStatus\":\"trialing\"," +
        "\"trialEndsAt\":\"2026-08-05T00:00:00Z\",\"dashboardUrl\":\"https://api.lightningenable.com/dashboard\"}";

    public CreateAccountToolTests()
    {
        // Point config writes at a throwaway temp file by default.
        _configServiceMock.Setup(c => c.ConfigFilePath).Returns(NewTempPath());
    }

    private string NewTempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"le-mcp-test-{Guid.NewGuid():N}.json");
        _tempFiles.Add(path);
        return path;
    }

    private void SetupSuccessfulFetch(long paidSats = 100, string body = AccountJson)
    {
        _l402ClientMock
            .Setup(c => c.FetchWithL402Async(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, string _, string? __, string? ___, long ____, CancellationToken _____) =>
                L402FetchResult.Succeeded(url, body, 200, "application/json", paidAmountSats: paidSats,
                    l402Token: "macaroon:preimage", protocol: "L402"));
    }

    private static ApprovalCheckResult Approval(ApprovalLevel level, long sats = 1000, decimal usd = 0.05m, string? denial = null) =>
        new()
        {
            Level = level,
            AmountSats = sats,
            AmountUsd = usd,
            DenialReason = denial,
            RemainingSessionBudgetUsd = 5.00m
        };

    // ----- Validation -----

    [Fact]
    public async Task CreateAccount_MissingEmail_ReturnsError_DoesNotFetch()
    {
        var result = await CreateAccountTool.CreateLightningEnableAccount(
            email: "", l402Client: _l402ClientMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Email is required");
        _l402ClientMock.Verify(c => c.FetchWithL402Async(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing@domain")]
    [InlineData("@nolocal.com")]
    public async Task CreateAccount_InvalidEmail_ReturnsError(string badEmail)
    {
        var result = await CreateAccountTool.CreateLightningEnableAccount(
            email: badEmail, l402Client: _l402ClientMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("not a valid email");
    }

    [Fact]
    public async Task CreateAccount_NoClient_ReturnsError()
    {
        var result = await CreateAccountTool.CreateLightningEnableAccount(
            email: TestEmail, l402Client: null);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("No wallet configured");
    }

    // ----- Budget gating -----

    [Fact]
    public async Task CreateAccount_BudgetDenied_ReturnsError_DoesNotFetch()
    {
        _budgetServiceMock.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Approval(ApprovalLevel.Deny, denial: "over session limit"));

        var result = await CreateAccountTool.CreateLightningEnableAccount(
            email: TestEmail, l402Client: _l402ClientMock.Object,
            budgetService: _budgetServiceMock.Object, configService: _configServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("denied by budget policy");
        _l402ClientMock.Verify(c => c.FetchWithL402Async(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAccount_RequiresConfirmation_DoesNotLeakCode_DoesNotFetch()
    {
        _budgetServiceMock.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Approval(ApprovalLevel.FormConfirm, usd: 5.00m));
        _budgetServiceMock.Setup(b => b.CreatePendingConfirmation(
                It.IsAny<long>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new PendingConfirmation
            {
                Nonce = "ABC123",
                AmountSats = 1000,
                AmountUsd = 5.00m,
                ToolName = "create_lightning_enable_account",
                Description = "activation for agent@example.com",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(2)
            });

        // No McpServer → elicitation unavailable → out-of-band (stderr) path.
        var result = await CreateAccountTool.CreateLightningEnableAccount(
            email: TestEmail, maxSats: 500, l402Client: _l402ClientMock.Object,
            budgetService: _budgetServiceMock.Object, configService: _configServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("requiresConfirmation").GetBoolean().Should().BeTrue();
        json.RootElement.TryGetProperty("nonce", out _).Should().BeFalse("the code must never be in the result");
        result.Should().NotContain("ABC123", "the confirmation code must not leak into the model-visible result");
        json.RootElement.GetProperty("expiresInSeconds").GetInt32().Should().Be(120);

        _l402ClientMock.Verify(c => c.FetchWithL402Async(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAccount_WithConfirmationNonce_ProceedsAndConsumesBoundToToolAndDestination()
    {
        _budgetServiceMock.Setup(b => b.CheckApprovalLevelAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Approval(ApprovalLevel.FormConfirm, usd: 5.00m));
        _budgetServiceMock.Setup(b => b.ValidateAndConsumeConfirmation(
                "ABC123", 500, "create_lightning_enable_account", It.Is<string>(u => u.EndsWith("/api/signup/l402"))))
            .Returns(new PendingConfirmation { Nonce = "ABC123", AmountSats = 500, ToolName = "create_lightning_enable_account" });
        SetupSuccessfulFetch();

        var result = await CreateAccountTool.CreateLightningEnableAccount(
            email: TestEmail, maxSats: 500, confirmationNonce: "abc123",
            l402Client: _l402ClientMock.Object, budgetService: _budgetServiceMock.Object,
            configService: _configServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("apiKey").GetString().Should().Be("le_live_abc123");

        // Code upcased; bound to amount + tool + signup destination.
        _budgetServiceMock.Verify(b => b.ValidateAndConsumeConfirmation(
            "ABC123", 500, "create_lightning_enable_account",
            It.Is<string>(u => u.EndsWith("/api/signup/l402"))), Times.Once);
        _l402ClientMock.Verify(c => c.FetchWithL402Async(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ----- Signup flow -----

    [Fact]
    public async Task CreateAccount_PostsEmailToSignupEndpoint()
    {
        SetupSuccessfulFetch();

        await CreateAccountTool.CreateLightningEnableAccount(
            email: TestEmail, l402Client: _l402ClientMock.Object, configService: _configServiceMock.Object);

        _l402ClientMock.Verify(c => c.FetchWithL402Async(
            It.Is<string>(u => u.EndsWith("/api/signup/l402")),
            "POST",
            null,
            It.Is<string>(b => b.Contains(TestEmail)),
            1000L,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAccount_Success_ReturnsKeyAndMerchantDetails()
    {
        SetupSuccessfulFetch();

        var result = await CreateAccountTool.CreateLightningEnableAccount(
            email: TestEmail, l402Client: _l402ClientMock.Object, configService: _configServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("apiKey").GetString().Should().Be("le_live_abc123");
        json.RootElement.GetProperty("merchantId").GetString().Should().Be("merch_123");
        json.RootElement.GetProperty("planTier").GetString().Should().Be("individual");
        json.RootElement.GetProperty("subscriptionStatus").GetString().Should().Be("trialing");
        json.RootElement.GetProperty("activation").GetProperty("paid").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("activation").GetProperty("amountSats").GetInt64().Should().Be(100);
    }

    [Fact]
    public async Task CreateAccount_Success_MergesApiKeyIntoConfig()
    {
        var configPath = NewTempPath();
        _configServiceMock.Setup(c => c.ConfigFilePath).Returns(configPath);
        SetupSuccessfulFetch();

        var result = await CreateAccountTool.CreateLightningEnableAccount(
            email: TestEmail, l402Client: _l402ClientMock.Object, configService: _configServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("config").GetProperty("written").GetBoolean().Should().BeTrue();

        File.Exists(configPath).Should().BeTrue();
        var saved = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        saved["lightningEnableApiKey"]!.GetValue<string>().Should().Be("le_live_abc123");
    }

    [Fact]
    public async Task CreateAccount_ConfigMerge_PreservesExistingKeys()
    {
        var configPath = NewTempPath();
        File.WriteAllText(configPath,
            "{\"wallets\":{\"nwcConnectionString\":\"nostr+walletconnect://keep-me\"},\"currency\":\"USD\"}");
        _configServiceMock.Setup(c => c.ConfigFilePath).Returns(configPath);
        SetupSuccessfulFetch();

        await CreateAccountTool.CreateLightningEnableAccount(
            email: TestEmail, l402Client: _l402ClientMock.Object, configService: _configServiceMock.Object);

        var saved = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        saved["lightningEnableApiKey"]!.GetValue<string>().Should().Be("le_live_abc123");
        saved["wallets"]!["nwcConnectionString"]!.GetValue<string>().Should().Be("nostr+walletconnect://keep-me");
        saved["currency"]!.GetValue<string>().Should().Be("USD");
    }

    [Fact]
    public async Task CreateAccount_FetchFails_ReturnsError()
    {
        _l402ClientMock
            .Setup(c => c.FetchWithL402Async(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(L402FetchResult.Failed("https://x/api/signup/l402", "email already registered", 409));

        var result = await CreateAccountTool.CreateLightningEnableAccount(
            email: TestEmail, l402Client: _l402ClientMock.Object, configService: _configServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("already registered");
    }

    [Fact]
    public async Task CreateAccount_NoApiKeyInResponse_ReturnsError()
    {
        SetupSuccessfulFetch(body: "{\"status\":\"active\",\"merchantId\":\"m1\"}");

        var result = await CreateAccountTool.CreateLightningEnableAccount(
            email: TestEmail, l402Client: _l402ClientMock.Object, configService: _configServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("no apiKey");
    }

    [Fact]
    public async Task CreateAccount_NonJsonResponse_ReturnsError()
    {
        SetupSuccessfulFetch(body: "<html>gateway error</html>");

        var result = await CreateAccountTool.CreateLightningEnableAccount(
            email: TestEmail, l402Client: _l402ClientMock.Object, configService: _configServiceMock.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("not valid JSON");
    }

    // ----- Config-merge helper -----

    [Fact]
    public void MergeApiKeyIntoConfig_CreatesFileWhenMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"le-mcp-nested-{Guid.NewGuid():N}", "config.json");
        _tempFiles.Add(path);

        var (ok, err) = CreateAccountTool.MergeApiKeyIntoConfig(path, "le_key");

        ok.Should().BeTrue();
        err.Should().BeNull();
        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        saved["lightningEnableApiKey"]!.GetValue<string>().Should().Be("le_key");
    }

    [Fact]
    public void MergeApiKeyIntoConfig_OverwritesOnlyTheApiKey()
    {
        var path = NewTempPath();
        File.WriteAllText(path, "{\"lightningEnableApiKey\":\"old\",\"currency\":\"USD\"}");

        CreateAccountTool.MergeApiKeyIntoConfig(path, "new_key");

        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        saved["lightningEnableApiKey"]!.GetValue<string>().Should().Be("new_key");
        saved["currency"]!.GetValue<string>().Should().Be("USD");
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try
            {
                if (File.Exists(f)) File.Delete(f);
                var dir = Path.GetDirectoryName(f);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && dir.Contains("le-mcp-nested-"))
                    Directory.Delete(dir, true);
            }
            catch { /* best-effort cleanup */ }
        }
    }
}

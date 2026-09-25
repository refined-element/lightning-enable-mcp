using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// Hardening of the out-of-band confirmation code: a guess limit, a cap on outstanding codes,
/// a configurable TTL, no oracle/echo in verify_confirmation_code, no code in logs, atomic
/// consume, and an end-to-end proof through send_onchain with each delivering channel.
/// </summary>
public class ConfirmationHardeningTests
{
    private const string Address = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";

    private static BudgetService NewService(IConfirmationChannel? channel = null, int? ttlSeconds = null)
    {
        var config = new Mock<IBudgetConfigurationService>();
        config.Setup(c => c.Configuration).Returns(new UserBudgetConfiguration
        {
            Currency = "USD",
            Tiers = new TierThresholds { AutoApprove = 0.10m, LogAndApprove = 1.00m, FormConfirm = 10.00m, UrlConfirm = 100.00m },
            Limits = new PaymentLimits { MaxPerPayment = 500.00m, MaxPerSession = 100.00m },
            Session = new SessionSettings { RequireApprovalForFirstPayment = true, CooldownSeconds = 0 },
            Confirmation = new ConfirmationSettings { TtlSeconds = ttlSeconds }
        });
        var price = new Mock<IPriceService>();
        price.Setup(p => p.SatsToUsdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long sats, CancellationToken _) => sats / 100000m);
        price.Setup(p => p.UsdToSatsAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((decimal usd, CancellationToken _) => (long)(usd * 100000));
        price.Setup(p => p.GetBtcPriceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(100000m);
        return new BudgetService(config.Object, price.Object, channel ?? new StderrConfirmationChannel(TextWriter.Null));
    }

    private static ConfirmationRequest Request(string destination = "dest-1", long sats = 1000) => new()
    {
        AmountSats = sats,
        AmountUsd = 0.01m,
        ToolName = "pay_invoice",
        Description = destination,
        Destination = destination,
        Title = "T",
        Summary = "S"
    };

    private static string WrongCode(string real) => real == "ZZZZZZ" ? "YYYYYY" : "ZZZZZZ";

    // ---- 1. brute force ----------------------------------------------------------------

    [Fact]
    public void GuessLimit_AfterFiveInvalidConsumes_RevokesAllPendingCodes()
    {
        var service = NewService();
        var pending = service.CreatePendingConfirmation(1000, 0.01m, "pay_invoice", "d", "d");

        for (var i = 0; i < BudgetService.MaxFailedConfirmationAttempts; i++)
        {
            service.ValidateAndConsumeConfirmation(WrongCode(pending.Nonce), 1000, "pay_invoice", "d").Should().BeNull();
        }

        // The real code is now revoked: a fresh request is required.
        service.ValidateAndConsumeConfirmation(pending.Nonce, 1000, "pay_invoice", "d").Should().BeNull();
    }

    [Fact]
    public void GuessLimit_VerifyToolFailures_CountToo_SoItIsNotAFreeOracle()
    {
        var service = NewService();
        var pending = service.CreatePendingConfirmation(1000, 0.01m, "pay_invoice", "d", "d");

        for (var i = 0; i < BudgetService.MaxFailedConfirmationAttempts; i++)
        {
            service.ValidateConfirmation(WrongCode(pending.Nonce)).Should().BeNull();
        }

        service.ValidateConfirmation(pending.Nonce).Should().BeNull();
    }

    [Fact]
    public void GuessLimit_BelowThreshold_CorrectCodeStillWorks_AndSuccessResetsTheCounter()
    {
        var service = NewService();
        var first = service.CreatePendingConfirmation(1000, 0.01m, "pay_invoice", "d", "d");
        for (var i = 0; i < BudgetService.MaxFailedConfirmationAttempts - 1; i++)
            service.ValidateAndConsumeConfirmation(WrongCode(first.Nonce), 1000, "pay_invoice", "d");
        service.ValidateAndConsumeConfirmation(first.Nonce, 1000, "pay_invoice", "d").Should().NotBeNull();

        var second = service.CreatePendingConfirmation(1000, 0.01m, "pay_invoice", "d", "d");
        for (var i = 0; i < BudgetService.MaxFailedConfirmationAttempts - 1; i++)
            service.ValidateAndConsumeConfirmation(WrongCode(second.Nonce), 1000, "pay_invoice", "d");
        service.ValidateAndConsumeConfirmation(second.Nonce, 1000, "pay_invoice", "d").Should().NotBeNull();
    }

    // ---- 2. verify tool ---------------------------------------------------------------

    [Fact]
    public void VerifyTool_DoesNotEchoTheCodeOrTheDestination()
    {
        var service = NewService();
        var pending = service.CreatePendingConfirmation(1000, 0.01m, "send_onchain", Address, Address);

        var result = VerifyConfirmationCodeTool.VerifyConfirmationCode(pending.Nonce.ToLowerInvariant(), service);

        JsonDocument.Parse(result).RootElement.GetProperty("valid").GetBoolean().Should().BeTrue();
        result.Should().NotContain(pending.Nonce);
        result.Should().NotContain(Address);
    }

    // ---- 3. TTL -----------------------------------------------------------------------

    [Theory]
    [InlineData(null, null, 120)]
    [InlineData(null, 600, 600)]
    [InlineData("300", 600, 300)]
    [InlineData("junk", 600, 600)]
    [InlineData("100000", null, 900)]
    [InlineData("1", null, 30)]
    [InlineData(null, -5, 30)]
    public void Ttl_ResolvesEnvOverConfig_AndClamps(string? env, int? config, int expected)
    {
        ConfirmationChannelResolver.ResolveTtlSeconds(env, config).Should().Be(expected);
    }

    [Fact]
    public void Ttl_ConfiguredValue_SetsTheExpiryOfMintedCodes()
    {
        var service = NewService(ttlSeconds: 600);
        var pending = service.CreatePendingConfirmation(1000, 0.01m, "pay_invoice", "d", "d");
        (pending.ExpiresAt - pending.CreatedAt).TotalSeconds.Should().BeApproximately(600, 1);
    }

    [Fact]
    public async Task Ttl_StderrChannel_PrintsTheRealExpiry_NotAHardcoded120()
    {
        var writer = new StringWriter();
        var service = NewService(new StderrConfirmationChannel(writer), ttlSeconds: 600);
        var dispatch = await service.RequestConfirmationAsync(Request());
        dispatch.Delivered.Should().BeTrue();
        dispatch.ExpiresInSeconds.Should().Be(600);
        writer.ToString().Should().Contain("Expires in 600s");
    }

    // ---- 5. pending cap ---------------------------------------------------------------

    [Fact]
    public async Task PendingCap_RefusesNewMintsBeyondTheCap_UntilOneIsConsumed()
    {
        var writer = new StringWriter();
        var service = NewService(new StderrConfirmationChannel(writer));
        var delivered = new List<PendingConfirmation>();
        for (var i = 0; i < BudgetService.MaxPendingConfirmations; i++)
        {
            var d = await service.RequestConfirmationAsync(Request($"dest-{i}"));
            d.Delivered.Should().BeTrue();
            delivered.Add(d.Pending!);
        }
        var linesBefore = writer.ToString().Length;

        var refused = await service.RequestConfirmationAsync(Request("dest-x"));
        refused.Delivered.Should().BeFalse();
        refused.Pending.Should().BeNull();
        refused.RefusalReason.Should().Contain("outstanding");
        writer.ToString().Length.Should().Be(linesBefore, "a refused mint must not notify the operator");

        service.ValidateAndConsumeConfirmation(delivered[0].Nonce, 1000, "pay_invoice", "dest-0").Should().NotBeNull();
        (await service.RequestConfirmationAsync(Request("dest-y"))).Delivered.Should().BeTrue();
    }

    // ---- 6. leakage -------------------------------------------------------------------

    [Fact]
    public void Leak_NoToolLogsTheConsumedConfirmationCode()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "LightningEnable.Mcp")))
            dir = dir.Parent;
        var tools = Path.Combine(dir!.FullName, "src", "LightningEnable.Mcp", "Tools");
        var offenders = Directory.EnumerateFiles(tools, "*.cs")
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, i, l)))
            .Where(x => x.l.Contains("Console.Error") && Regex.IsMatch(x.l, @"\{[a-zA-Z]*\.Nonce\}"))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();
        offenders.Should().BeEmpty("the code belongs only on the configured approval channel");
    }

    // ---- 7. concurrency ---------------------------------------------------------------

    [Fact]
    public async Task Concurrency_ParallelConsumesOfOneCode_ExactlyOneSucceeds()
    {
        var service = NewService();
        var pending = service.CreatePendingConfirmation(1000, 0.01m, "pay_invoice", "d", "d");
        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            gate.Wait();
            return service.ValidateAndConsumeConfirmation(pending.Nonce, 1000, "pay_invoice", "d");
        })).ToArray();
        gate.Set();
        var results = await Task.WhenAll(tasks);
        results.Count(r => r != null).Should().Be(1);
    }

    // ---- 8. end to end ----------------------------------------------------------------

    public enum E2EChannel { Stderr, File, Webhook }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(string Body, string? Signature)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            request.Headers.TryGetValues("X-LightningEnable-Signature", out var sig);
            Requests.Add((body, sig?.Single()));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Theory]
    [InlineData(E2EChannel.Stderr)]
    [InlineData(E2EChannel.File)]
    [InlineData(E2EChannel.Webhook)]
    public async Task EndToEnd_SendOnChain_CodeFromChannelOnly_OneSend_NoReplay(E2EChannel kind)
    {
        const string secret = "e2e-fixture-signing";
        var stderr = new StringWriter();
        var filePath = Path.Combine(Path.GetTempPath(), $"le-e2e-{Guid.NewGuid():N}.jsonl");
        var handler = new CapturingHandler();
        IConfirmationChannel channel = kind switch
        {
            E2EChannel.Stderr => new StderrConfirmationChannel(stderr),
            E2EChannel.File => new FileConfirmationChannel(filePath),
            _ => new WebhookConfirmationChannel(() => new HttpClient(handler), "https://ops.example.com/approve", secret)
        };
        var service = NewService(channel);
        var wallet = new Mock<IWalletService>();
        wallet.Setup(w => w.IsConfigured).Returns(true);
        wallet.Setup(w => w.ProviderName).Returns("fake");
        wallet.Setup(w => w.SendOnChainAsync(Address, 5000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OnChainPaymentResult { Success = true, PaymentId = "p1", TxId = "tx1", State = "COMPLETED", AmountSats = 5000, FeeSats = 10 });

        try
        {
            // 1. First call: confirmation required, no code in the result.
            var first = await SendOnChainTool.SendOnChain(Address, 5000, null, walletService: wallet.Object, budgetService: service);
            JsonDocument.Parse(first).RootElement.GetProperty("requiresConfirmation").GetBoolean().Should().BeTrue();

            // 2. Capture the code from the operator channel.
            string code;
            switch (kind)
            {
                case E2EChannel.Stderr:
                    code = Regex.Match(stderr.ToString(), @"Confirmation code: ([A-Z0-9]{6})").Groups[1].Value;
                    break;
                case E2EChannel.File:
                    var line = File.ReadAllLines(filePath).Single();
                    code = JsonDocument.Parse(line).RootElement.GetProperty("nonce").GetString()!;
                    if (!OperatingSystem.IsWindows())
                        File.GetUnixFileMode(filePath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    break;
                default:
                    var (body, signature) = handler.Requests.Single();
                    var parts = signature!.Split(',').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
                    var expected = Convert.ToHexString(HMACSHA256.HashData(
                        Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{parts["t"]}.{body}"))).ToLowerInvariant();
                    parts["v1"].Should().Be(expected, "the webhook signature must verify");
                    code = JsonDocument.Parse(body).RootElement.GetProperty("nonce").GetString()!;
                    break;
            }
            code.Should().MatchRegex("^[A-Z0-9]{6}$");
            first.Should().NotContain(code);

            // 3. Second call with the code: wallet called exactly once.
            var second = await SendOnChainTool.SendOnChain(Address, 5000, code, walletService: wallet.Object, budgetService: service);
            JsonDocument.Parse(second).RootElement.GetProperty("success").GetBoolean().Should().BeTrue(second);
            wallet.Verify(w => w.SendOnChainAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Once);

            // 4. Replay: refused, wallet not called again.
            var third = await SendOnChainTool.SendOnChain(Address, 5000, code, walletService: wallet.Object, budgetService: service);
            JsonDocument.Parse(third).RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
            wallet.Verify(w => w.SendOnChainAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }
}

using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using Moq;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// The approval channel decides WHERE an over-threshold confirmation code goes. Getting this
/// wrong is a funds-safety bug in both directions: a code nobody reads blocks legitimate
/// payments, and a code the agent can read approves illegitimate ones. These tests pin the
/// selection matrix, the four channels' behaviour, and the invariant that binds them all —
/// the code never reaches the model, and a payment is never approved because its notification
/// could not be delivered.
/// </summary>
public class ConfirmationChannelTests
{
    private const string TestSecret = "fixture-string-webhook-signing";
    private const string TestWebhookUrl = "https://ops.example.com/approvals";

    private static ConfirmationRequest SampleRequest(string tool = "pay_invoice") => new()
    {
        AmountSats = 50_000,
        AmountUsd = 12.34m,
        ToolName = tool,
        Description = "lnbc500u1pj9npjpp5...",
        Destination = "lnbc500u1pj9npjpp5abcdefghijklmnopqrstuvwxyz0123456789",
        Title = "PAYMENT CONFIRMATION REQUIRED",
        Summary = "pay_invoice — $12.34 (50,000 sats), invoice lnbc500u1pj9npjpp5..."
    };

    private static PendingConfirmation SamplePending(string nonce = "AB12CD")
    {
        var now = DateTime.UtcNow;
        return new PendingConfirmation
        {
            Nonce = nonce,
            AmountSats = 50_000,
            AmountUsd = 12.34m,
            ToolName = "pay_invoice",
            Description = "lnbc500u1pj9npjpp5...",
            Destination = "lnbc500u1pj9npjpp5abcdefghijklmnopqrstuvwxyz0123456789",
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(2)
        };
    }

    /// <summary>Env lookup seam: only the keys the test sets exist.</summary>
    private static Func<string, string?> Env(params (string Key, string? Value)[] entries)
    {
        var map = entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        return key => map.TryGetValue(key, out var value) ? value : null;
    }

    #region Channel selection / precedence

    [Fact]
    public void Resolve_EnvironmentVariable_BeatsConfigAndAuto()
    {
        var resolution = ConfirmationChannelResolver.Resolve(
            envChannel: "refuse", configChannel: "stderr", stdinIsTty: true, hostedFlag: null);

        resolution.Kind.Should().Be(ConfirmationChannelKind.Refuse);
        resolution.Source.Should().Be("env");
    }

    [Fact]
    public void Resolve_ConfigValue_BeatsAuto()
    {
        var resolution = ConfirmationChannelResolver.Resolve(
            envChannel: null, configChannel: "webhook", stdinIsTty: true, hostedFlag: "1");

        resolution.Kind.Should().Be(ConfirmationChannelKind.Webhook);
        resolution.Source.Should().Be("config");
        resolution.Warning.Should().BeNull();
    }

    [Theory]
    [InlineData("  STDERR ", ConfirmationChannelKind.Stderr)]
    [InlineData("Refuse", ConfirmationChannelKind.Refuse)]
    [InlineData("WEBHOOK", ConfirmationChannelKind.Webhook)]
    [InlineData("file", ConfirmationChannelKind.File)]
    public void Resolve_ChannelNames_AreCaseAndWhitespaceInsensitive(string value, ConfirmationChannelKind expected)
    {
        ConfirmationChannelResolver.Resolve(value, null, true, null).Kind.Should().Be(expected);
    }

    [Fact]
    public void Resolve_InvalidEnvironmentValue_FailsClosedToRefuse()
    {
        var resolution = ConfirmationChannelResolver.Resolve(
            envChannel: "console", configChannel: null, stdinIsTty: true, hostedFlag: null);

        // A typo must not silently restore the posture the operator was moving away from.
        resolution.Kind.Should().Be(ConfirmationChannelKind.Refuse);
        resolution.Source.Should().Be("env-invalid");
        resolution.Warning.Should().Contain(ConfirmationChannelResolver.ChannelEnvironmentVariable);
        resolution.Warning.Should().Contain("console");
    }

    [Fact]
    public void Resolve_InvalidConfigValue_FailsClosedToRefuse()
    {
        var resolution = ConfirmationChannelResolver.Resolve(
            envChannel: null, configChannel: "email", stdinIsTty: true, hostedFlag: null);

        resolution.Kind.Should().Be(ConfirmationChannelKind.Refuse);
        resolution.Source.Should().Be("config-invalid");
        resolution.Warning.Should().Contain("confirmation.channel");
    }

    #endregion

    #region Hosted auto-detection matrix

    [Fact]
    public void Resolve_Tty_DefaultsToStderrWithNoWarning()
    {
        var resolution = ConfirmationChannelResolver.Resolve(null, null, stdinIsTty: true, hostedFlag: null);

        resolution.Kind.Should().Be(ConfirmationChannelKind.Stderr);
        resolution.Source.Should().Be("auto");
        resolution.Warning.Should().BeNull();
    }

    [Fact]
    public void Resolve_Tty_IgnoresHostedFlag()
    {
        // A human IS at the terminal, so stderr is still genuinely out-of-band.
        var resolution = ConfirmationChannelResolver.Resolve(null, null, stdinIsTty: true, hostedFlag: "1");

        resolution.Kind.Should().Be(ConfirmationChannelKind.Stderr);
    }

    [Fact]
    public void Resolve_NonTty_WithoutHostedOptIn_KeepsStderrButWarns()
    {
        var resolution = ConfirmationChannelResolver.Resolve(null, null, stdinIsTty: false, hostedFlag: null);

        resolution.Kind.Should().Be(ConfirmationChannelKind.Stderr);
        resolution.Warning.Should().NotBeNullOrWhiteSpace();
        resolution.Warning.Should().Contain("stdin is not a TTY");
        resolution.Warning.Should().Contain(ConfirmationChannelResolver.HostedEnvironmentVariable);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void Resolve_NonTty_WithHostedOptIn_DefaultsToRefuseAndWarns(string hosted)
    {
        var resolution = ConfirmationChannelResolver.Resolve(null, null, stdinIsTty: false, hostedFlag: hosted);

        resolution.Kind.Should().Be(ConfirmationChannelKind.Refuse);
        resolution.Warning.Should().Contain("REFUSED");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("")]
    public void Resolve_NonTty_WithNonAffirmativeHostedFlag_KeepsStderr(string hosted)
    {
        ConfirmationChannelResolver.Resolve(null, null, stdinIsTty: false, hostedFlag: hosted)
            .Kind.Should().Be(ConfirmationChannelKind.Stderr);
    }

    [Fact]
    public void Resolve_NonTty_WithExplicitChannel_DoesNotWarn()
    {
        // The operator has already answered the question; do not nag on every start.
        ConfirmationChannelResolver.Resolve("stderr", null, stdinIsTty: false, hostedFlag: "1")
            .Warning.Should().BeNull();
    }

    #endregion

    #region Factory wiring

    [Fact]
    public void Factory_HostedNonTty_BuildsRefusingChannel()
    {
        var warnings = new List<string>();
        var channel = ConfirmationChannelFactory.Create(
            new ConfirmationSettings(),
            webhookClientFactory: null,
            warn: warnings.Add,
            stdinIsTty: false,
            environment: Env((ConfirmationChannelResolver.HostedEnvironmentVariable, "1")));

        channel.Should().BeOfType<RefusingConfirmationChannel>();
        channel.RefusalReason.Should().Contain(ConfirmationChannelResolver.HostedEnvironmentVariable);
        warnings.Should().ContainSingle();
    }

    [Fact]
    public void Factory_FileChannel_UsesEnvironmentPathOverConfigPath()
    {
        var channel = ConfirmationChannelFactory.Create(
            new ConfirmationSettings { Channel = "file", FilePath = "/from/config.jsonl" },
            webhookClientFactory: null,
            warn: null,
            stdinIsTty: true,
            environment: Env((ConfirmationChannelResolver.FilePathEnvironmentVariable, "/from/env.jsonl")));

        channel.Should().BeOfType<FileConfirmationChannel>()
            .Which.FilePath.Should().Be("/from/env.jsonl");
    }

    [Fact]
    public void Factory_FileChannel_FallsBackToDefaultPath()
    {
        var channel = ConfirmationChannelFactory.Create(
            new ConfirmationSettings { Channel = "file" },
            webhookClientFactory: null, warn: null, stdinIsTty: true, environment: Env());

        channel.Should().BeOfType<FileConfirmationChannel>()
            .Which.FilePath.Should().Be(FileConfirmationChannel.DefaultPath);
    }

    [Fact]
    public void Factory_WebhookChannel_IsBuiltWhenFullyConfigured()
    {
        var channel = ConfirmationChannelFactory.Create(
            new ConfirmationSettings { Channel = "webhook", WebhookUrl = TestWebhookUrl, WebhookSecret = TestSecret },
            webhookClientFactory: () => new HttpClient(),
            warn: null, stdinIsTty: true, environment: Env());

        channel.Should().BeOfType<WebhookConfirmationChannel>()
            .Which.Url.Should().Be(TestWebhookUrl);
    }

    [Fact]
    public void Factory_WebhookWithoutUrl_RefusesRatherThanFallingBackToStderr()
    {
        var warnings = new List<string>();
        var channel = ConfirmationChannelFactory.Create(
            new ConfirmationSettings { Channel = "webhook", WebhookSecret = TestSecret },
            webhookClientFactory: () => new HttpClient(),
            warn: warnings.Add, stdinIsTty: true, environment: Env());

        channel.Kind.Should().Be(ConfirmationChannelKind.Refuse);
        channel.RefusalReason.Should().Contain("webhookUrl");
        warnings.Should().ContainSingle().Which.Should().Contain("REFUSED");
    }

    [Fact]
    public void Factory_WebhookWithoutSecret_Refuses()
    {
        var channel = ConfirmationChannelFactory.Create(
            new ConfirmationSettings { Channel = "webhook", WebhookUrl = TestWebhookUrl },
            webhookClientFactory: () => new HttpClient(),
            warn: null, stdinIsTty: true, environment: Env());

        channel.Kind.Should().Be(ConfirmationChannelKind.Refuse);
        channel.RefusalReason.Should().Contain("webhookSecret");
    }

    [Theory]
    [InlineData("http://127.0.0.1/approve")]
    [InlineData("http://localhost:9000/approve")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("file:///etc/passwd")]
    public void Factory_WebhookWithNonPublicUrl_IsRefusedBySsrfGuard(string url)
    {
        var channel = ConfirmationChannelFactory.Create(
            new ConfirmationSettings { Channel = "webhook", WebhookUrl = url, WebhookSecret = TestSecret },
            webhookClientFactory: () => new HttpClient(),
            warn: null, stdinIsTty: true, environment: Env());

        channel.Kind.Should().Be(ConfirmationChannelKind.Refuse);
        channel.RefusalReason.Should().Contain("SSRF");
    }

    [Fact]
    public void Factory_EnvironmentChannel_OverridesConfigChannel()
    {
        var channel = ConfirmationChannelFactory.Create(
            new ConfirmationSettings { Channel = "stderr" },
            webhookClientFactory: null, warn: null, stdinIsTty: true,
            environment: Env((ConfirmationChannelResolver.ChannelEnvironmentVariable, "refuse")));

        channel.Kind.Should().Be(ConfirmationChannelKind.Refuse);
    }

    #endregion

    #region stderr channel (unchanged local behaviour)

    [Fact]
    public async Task StderrChannel_WritesTheCodeToTheConsoleInTheHistoricalFormat()
    {
        var writer = new StringWriter();
        var channel = new StderrConfirmationChannel(writer);
        var pending = SamplePending("ZZ9Z9Z");

        var result = await channel.DeliverAsync(pending, SampleRequest(), CancellationToken.None);

        result.Success.Should().BeTrue();
        var output = writer.ToString();
        output.Should().Contain("*** PAYMENT CONFIRMATION REQUIRED ***");
        output.Should().Contain("Confirmation code: ZZ9Z9Z");
        output.Should().Contain("Expires in 120s");
    }

    #endregion

    #region file channel

    [Fact]
    public async Task FileChannel_AppendsOneJsonLinePerConfirmation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"le-confirm-{Guid.NewGuid():N}", "confirmations.jsonl");
        try
        {
            var channel = new FileConfirmationChannel(path);

            (await channel.DeliverAsync(SamplePending("AAA111"), SampleRequest(), CancellationToken.None))
                .Success.Should().BeTrue();
            (await channel.DeliverAsync(SamplePending("BBB222"), SampleRequest(), CancellationToken.None))
                .Success.Should().BeTrue();

            var lines = File.ReadAllLines(path);
            lines.Should().HaveCount(2);

            var first = JsonDocument.Parse(lines[0]).RootElement;
            first.GetProperty("type").GetString().Should().Be("payment.confirmation_required");
            first.GetProperty("nonce").GetString().Should().Be("AAA111");
            first.GetProperty("tool").GetString().Should().Be("pay_invoice");
            first.GetProperty("amountSats").GetInt64().Should().Be(50_000);
            first.GetProperty("amountUsd").GetDecimal().Should().Be(12.34m);
            first.GetProperty("expiresInSeconds").GetInt32().Should().Be(120);
            // Destination is a SUMMARY, not the whole blob.
            first.GetProperty("destination").GetString().Should().StartWith("lnbc500u1pj9npjpp5");
            first.GetProperty("destination").GetString()!.Length.Should()
                .BeLessThanOrEqualTo(ConfirmationPayload.DestinationSummaryLength + 3);

            JsonDocument.Parse(lines[1]).RootElement.GetProperty("nonce").GetString().Should().Be("BBB222");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task FileChannel_RestrictsPermissionsTo0600OnPosix()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // POSIX modes do not exist here; the file inherits the profile ACL.
        }

        var path = Path.Combine(Path.GetTempPath(), $"le-confirm-{Guid.NewGuid():N}.jsonl");
        try
        {
            await new FileConfirmationChannel(path)
                .DeliverAsync(SamplePending(), SampleRequest(), CancellationToken.None);

            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task FileChannel_UnwritablePath_ReportsFailureInsteadOfThrowing()
    {
        // A directory where the file should be: the append must fail, not crash the tool.
        var directory = Path.Combine(Path.GetTempPath(), $"le-confirm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var result = await new FileConfirmationChannel(directory)
                .DeliverAsync(SamplePending(), SampleRequest(), CancellationToken.None);

            result.Success.Should().BeFalse();
            result.Error.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    #endregion

    #region webhook channel

    /// <summary>Stub transport: records every request and replays canned responses.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public List<(HttpMethod Method, Uri? Uri, string Body, string? Signature)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var signature = request.Headers.TryGetValues("X-LightningEnable-Signature", out var values)
                ? string.Join(string.Empty, values)
                : null;
            Requests.Add((request.Method, request.RequestUri, body, signature));
            return _responder(request);
        }
    }

    private static RecordingHandler Responder(HttpStatusCode status, string? location = null) =>
        new(_ =>
        {
            var response = new HttpResponseMessage(status);
            if (location != null)
            {
                response.Headers.Location = new Uri(location);
            }

            return response;
        });

    [Fact]
    public async Task WebhookChannel_PostsSignedPayloadToTheConfiguredUrl()
    {
        var handler = Responder(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var channel = new WebhookConfirmationChannel(() => client, TestWebhookUrl, TestSecret);
        var pending = SamplePending("QQ7Q7Q");

        var result = await channel.DeliverAsync(pending, SampleRequest(), CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
        var (method, uri, body, signature) = handler.Requests[0];
        method.Should().Be(HttpMethod.Post);
        uri!.ToString().Should().Be(TestWebhookUrl);

        var payload = JsonDocument.Parse(body).RootElement;
        payload.GetProperty("nonce").GetString().Should().Be("QQ7Q7Q");
        payload.GetProperty("tool").GetString().Should().Be("pay_invoice");
        payload.GetProperty("amountSats").GetInt64().Should().Be(50_000);
        payload.GetProperty("amountUsd").GetDecimal().Should().Be(12.34m);
        payload.GetProperty("expiresInSeconds").GetInt32().Should().Be(120);
        payload.GetProperty("expiresAt").GetString().Should().NotBeNullOrWhiteSpace();
        payload.GetProperty("destination").GetString().Should().StartWith("lnbc500u1pj9npjpp5");
        // The payload carries nothing that could be used to spend directly.
        body.Should().NotContain("preimage");
        body.Should().NotContain("macaroon");

        signature.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task WebhookChannel_SignatureVerifiesWithTheConfiguredSecret()
    {
        string? capturedSignature = null;
        string? capturedBody = null;
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new HttpClient(handler);

        await new WebhookConfirmationChannel(() => client, TestWebhookUrl, TestSecret)
            .DeliverAsync(SamplePending(), SampleRequest(), CancellationToken.None);

        capturedSignature = handler.Requests[0].Signature;
        capturedBody = handler.Requests[0].Body;

        // Verify exactly as an operator would: split t=/v1=, recompute HMAC over "t.body".
        var parts = capturedSignature!.Split(',');
        var timestamp = long.Parse(parts[0]["t=".Length..]);
        var provided = parts[1]["v1=".Length..];

        var expected = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(TestSecret),
            Encoding.UTF8.GetBytes($"{timestamp}.{capturedBody}"))).ToLowerInvariant();

        provided.Should().Be(expected);

        // A different secret must NOT verify.
        var wrong = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("fixture-string-other"),
            Encoding.UTF8.GetBytes($"{timestamp}.{capturedBody}"))).ToLowerInvariant();
        provided.Should().NotBe(wrong);
    }

    [Fact]
    public async Task WebhookChannel_DoesNotFollowRedirects()
    {
        var handler = Responder(HttpStatusCode.Found, "https://attacker.example.com/collect");
        using var client = new HttpClient(handler);

        var result = await new WebhookConfirmationChannel(() => client, TestWebhookUrl, TestSecret)
            .DeliverAsync(SamplePending(), SampleRequest(), CancellationToken.None);

        // A 3xx is a delivery FAILURE, and exactly one request left the process — the signed
        // approval never chases a Location header to a host the operator did not configure.
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("redirect");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Uri!.ToString().Should().Be(TestWebhookUrl);
    }

    [Fact]
    public async Task WebhookChannel_ServerError_IsADeliveryFailure()
    {
        var handler = Responder(HttpStatusCode.InternalServerError);
        using var client = new HttpClient(handler);

        var result = await new WebhookConfirmationChannel(() => client, TestWebhookUrl, TestSecret)
            .DeliverAsync(SamplePending(), SampleRequest(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("500");
    }

    [Fact]
    public async Task WebhookChannel_TransportException_IsADeliveryFailureNotAThrow()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("connection refused"));
        using var client = new HttpClient(handler);

        var result = await new WebhookConfirmationChannel(() => client, TestWebhookUrl, TestSecret)
            .DeliverAsync(SamplePending(), SampleRequest(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("could not be reached");
    }

    #endregion

    #region BudgetService.RequestConfirmationAsync

    private static BudgetService BuildBudgetService(IConfirmationChannel channel)
    {
        var configService = new Mock<IBudgetConfigurationService>();
        configService.Setup(c => c.Configuration).Returns(new UserBudgetConfiguration());
        var priceService = new Mock<IPriceService>();
        return new BudgetService(configService.Object, priceService.Object, channel);
    }

    /// <summary>White-box: the pending-confirmation store must be EMPTY, not just unreturned.</summary>
    private static int PendingCount(BudgetService service)
    {
        var field = typeof(BudgetService).GetField(
            "_pendingConfirmations", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ((System.Collections.IDictionary)field.GetValue(service)!).Count;
    }

    private sealed class SpyChannel : IConfirmationChannel
    {
        private readonly ConfirmationDeliveryResult _result;

        public SpyChannel(ConfirmationChannelKind kind, ConfirmationDeliveryResult result, string? refusalReason = null)
        {
            Kind = kind;
            _result = result;
            RefusalReason = refusalReason;
        }

        public ConfirmationChannelKind Kind { get; }

        public string OperatorHint => "sent to the test channel";

        public string? RefusalReason { get; }

        public int DeliverCalls { get; private set; }

        public PendingConfirmation? LastPending { get; private set; }

        public Task<ConfirmationDeliveryResult> DeliverAsync(
            PendingConfirmation pending, ConfirmationRequest request, CancellationToken cancellationToken)
        {
            DeliverCalls++;
            LastPending = pending;
            return Task.FromResult(_result);
        }
    }

    [Fact]
    public async Task RequestConfirmation_RefuseChannel_CreatesNoPendingConfirmationAtAll()
    {
        var channel = new SpyChannel(
            ConfirmationChannelKind.Refuse,
            ConfirmationDeliveryResult.Fail("unused"),
            refusalReason: "no approval channel is configured on this server");
        var service = BuildBudgetService(channel);

        var result = await service.RequestConfirmationAsync(SampleRequest());

        result.Delivered.Should().BeFalse();
        result.Pending.Should().BeNull();
        result.ChannelName.Should().Be("refuse");
        result.RefusalReason.Should().Contain("no approval channel");
        channel.DeliverCalls.Should().Be(0, "the refuse channel short-circuits before minting a code");
        PendingCount(service).Should().Be(0, "a refused payment must leave no confirmation code behind");
    }

    [Fact]
    public async Task RequestConfirmation_RefuseChannel_LeavesTheBudgetUntouched()
    {
        var service = BuildBudgetService(new SpyChannel(
            ConfirmationChannelKind.Refuse, ConfirmationDeliveryResult.Fail("unused"), "refused"));

        var before = service.GetConfig();
        await service.RequestConfirmationAsync(SampleRequest());
        var after = service.GetConfig();

        after.SessionSpent.Should().Be(before.SessionSpent).And.Be(0);
        after.RequestCount.Should().Be(before.RequestCount).And.Be(0);
    }

    [Fact]
    public async Task RequestConfirmation_DeliveredChannel_ReturnsAConsumablePending()
    {
        var channel = new SpyChannel(ConfirmationChannelKind.Webhook, ConfirmationDeliveryResult.Ok());
        var service = BuildBudgetService(channel);
        var request = SampleRequest();

        var result = await service.RequestConfirmationAsync(request);

        result.Delivered.Should().BeTrue();
        result.Pending.Should().NotBeNull();
        result.ChannelName.Should().Be("webhook");
        result.OperatorHint.Should().Be("sent to the test channel");
        channel.DeliverCalls.Should().Be(1);

        // The code is real: it consumes for the exact amount + tool + destination.
        service.ValidateAndConsumeConfirmation(
            result.Pending!.Nonce, request.AmountSats, request.ToolName, request.Destination)
            .Should().NotBeNull();
    }

    [Fact]
    public async Task RequestConfirmation_DeliveryFailure_RefusesAndCancelsTheCode()
    {
        var channel = new SpyChannel(
            ConfirmationChannelKind.Webhook, ConfirmationDeliveryResult.Fail("the approval webhook answered HTTP 502"));
        var service = BuildBudgetService(channel);

        var result = await service.RequestConfirmationAsync(SampleRequest());

        result.Delivered.Should().BeFalse();
        result.RefusalReason.Should().Contain("REFUSED");
        result.RefusalReason.Should().Contain("502");
        channel.DeliverCalls.Should().Be(1);

        // The minted code was withdrawn — an undelivered code is not an approval.
        PendingCount(service).Should().Be(0);
        service.ValidateConfirmation(channel.LastPending!.Nonce).Should().BeNull();
    }

    [Fact]
    public async Task RequestConfirmation_ChannelThrowing_IsARefusalNotAnApproval()
    {
        var service = BuildBudgetService(new ThrowingChannel());

        var result = await service.RequestConfirmationAsync(SampleRequest());

        result.Delivered.Should().BeFalse();
        result.RefusalReason.Should().Contain("REFUSED");
        PendingCount(service).Should().Be(0);
    }

    private sealed class ThrowingChannel : IConfirmationChannel
    {
        public ConfirmationChannelKind Kind => ConfirmationChannelKind.File;
        public string OperatorHint => "never";
        public string? RefusalReason => null;

        public Task<ConfirmationDeliveryResult> DeliverAsync(
            PendingConfirmation pending, ConfirmationRequest request, CancellationToken cancellationToken) =>
            throw new IOException("disk on fire");
    }

    [Fact]
    public async Task RequestConfirmation_DefaultsToStderrWhenNoChannelIsInjected()
    {
        var configService = new Mock<IBudgetConfigurationService>();
        configService.Setup(c => c.Configuration).Returns(new UserBudgetConfiguration());
        var service = new BudgetService(configService.Object, new Mock<IPriceService>().Object);

        var result = await service.RequestConfirmationAsync(SampleRequest());

        result.Delivered.Should().BeTrue();
        result.ChannelName.Should().Be("stderr");
    }

    [Fact]
    public void CancelPendingConfirmation_RemovesTheCode()
    {
        var service = BuildBudgetService(new StderrConfirmationChannel(new StringWriter()));
        var pending = service.CreatePendingConfirmation(1000, 0.01m, "pay_invoice", "inv...", "lnbc-destination");

        service.CancelPendingConfirmation(pending.Nonce);

        service.ValidateConfirmation(pending.Nonce).Should().BeNull();
        PendingCount(service).Should().Be(0);
    }

    #endregion
}

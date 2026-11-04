using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// Startup warning: LIGHTNING_ENABLE_HOSTED=1 + no approval channel configured (so the
/// channel is "refuse") + requireApprovalForFirstPayment = no payment can ever be made. The
/// first payment needs approval and is refused, and a session that never pays never leaves
/// its first payment. Owner decision 2026-11-03: say so loudly at startup, only in that state.
/// </summary>
public class HostedFirstPaymentWarningTests
{
    private static List<string> Warnings(
        bool hosted = true,
        string? channel = null,
        bool firstPayment = true,
        bool tty = false,
        string? envChannel = null)
    {
        var env = new Dictionary<string, string?>();
        if (hosted)
        {
            env[ConfirmationChannelResolver.HostedEnvironmentVariable] = "1";
        }
        if (envChannel != null)
        {
            env[ConfirmationChannelResolver.ChannelEnvironmentVariable] = envChannel;
        }

        var warnings = new List<string>();
        ConfirmationChannelFactory.Create(
            new ConfirmationSettings { Channel = channel, FilePath = "/tmp/le-approvals.jsonl" },
            webhookClientFactory: null,
            warn: warnings.Add,
            stdinIsTty: tty,
            environment: name => env.TryGetValue(name, out var v) ? v : null,
            requireApprovalForFirstPayment: firstPayment);
        return warnings;
    }

    [Fact]
    public void EmittedInTheDeadConfiguration()
    {
        Warnings().Should().Contain(ConfirmationChannelFactory.HostedFirstPaymentLockoutWarning);
    }

    [Fact]
    public void SaysNoPaymentCanBeMadeAndNamesTheThreeFixes()
    {
        var message = ConfirmationChannelFactory.HostedFirstPaymentLockoutWarning;
        message.Should().Contain("No payment can be made");
        message.Should().Contain("config volume");
        message.Should().Contain("requireApprovalForFirstPayment").And.Contain("false");
        message.Should().Contain("webhook").And.Contain("file");
    }

    [Fact]
    public void NotEmittedWhenNotHosted() =>
        Warnings(hosted: false).Should().NotContain(ConfirmationChannelFactory.HostedFirstPaymentLockoutWarning);

    [Theory]
    [InlineData("file")]
    [InlineData("stderr")]
    public void NotEmittedWhenAChannelIsConfigured(string channel) =>
        Warnings(channel: channel).Should().NotContain(ConfirmationChannelFactory.HostedFirstPaymentLockoutWarning);

    [Fact]
    public void NotEmittedWhenAChannelIsSetByEnv() =>
        Warnings(envChannel: "file").Should().NotContain(ConfirmationChannelFactory.HostedFirstPaymentLockoutWarning);

    [Fact]
    public void NotEmittedWhenFirstPaymentApprovalIsOff() =>
        Warnings(firstPayment: false).Should().NotContain(ConfirmationChannelFactory.HostedFirstPaymentLockoutWarning);

    [Fact]
    public void NotEmittedWhenAHumanIsAtTheTerminal() =>
        // Hosted with a TTY resolves to stderr: a human can read and relay the code.
        Warnings(tty: true).Should().NotContain(ConfirmationChannelFactory.HostedFirstPaymentLockoutWarning);
}

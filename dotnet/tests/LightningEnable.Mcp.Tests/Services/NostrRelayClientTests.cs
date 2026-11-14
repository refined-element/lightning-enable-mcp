using System.Text.Json.Nodes;
using FluentAssertions;
using LightningEnable.Mcp.Services;
using Xunit;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>Direct relay publish/query against an in-process fake relay (127.0.0.1 only).</summary>
public class NostrRelayClientTests
{
    private static readonly TimeSpan T = TimeSpan.FromSeconds(5);
    private static readonly AttestationSubject Subject = new("u", "https://api.example.com/v1/forecast");

    private static JsonObject Event()
    {
        // BIP-340 test scalar 3 — test-only.
        NostrEvents.TryParseSecretKey("0000000000000000000000000000000000000000000000000000000000000003", out var key);
        var tags = Attestations.BuildTags(Subject, null, new Dictionary<string, bool> { ["responded"] = true }, 4);
        return Attestations.Sign(key!, tags, "", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private static readonly NostrRelayClient Client = new(_ => null);

    [Fact]
    public async Task Publish_Accepted_ThenQueryReturnsIt()
    {
        await using var relay = new FakeNostrRelay("accept").Start();
        var ev = Event();
        var outcome = await Client.PublishAsync(ev, relay.Url, T, CancellationToken.None);
        outcome.Ok.Should().BeTrue();
        outcome.Status.Should().Be(RelayOutcome.Accepted);
        relay.Received.First()[1]!["id"]!.GetValue<string>().Should().Be(ev["id"]!.GetValue<string>());

        var q = await Client.QueryAsync(new JsonObject { ["kinds"] = new JsonArray { 38403 }, ["#u"] = new JsonArray { Subject.Value } },
            relay.Url, T, CancellationToken.None);
        q.Ok.Should().BeTrue();
        q.Status.Should().Be(RelayOutcome.Complete);
        q.Events.Should().ContainSingle();
        NostrEvents.Verify(q.Events[0]!.AsObject()).Should().BeTrue();
    }

    [Fact]
    public async Task Publish_OkFalse_IsRejectedWithRelayReason()
    {
        await using var relay = new FakeNostrRelay("reject").Start();
        var outcome = await Client.PublishAsync(Event(), relay.Url, T, CancellationToken.None);
        outcome.Ok.Should().BeFalse();
        outcome.Status.Should().Be(RelayOutcome.Rejected);
        outcome.Message.Should().Contain("blocked");
    }

    [Fact]
    public async Task Publish_AuthChallenge_IsTypedAuthRequired()
    {
        await using var relay = new FakeNostrRelay("auth").Start();
        var outcome = await Client.PublishAsync(Event(), relay.Url, T, CancellationToken.None);
        outcome.Ok.Should().BeFalse();
        outcome.Status.Should().Be(RelayOutcome.AuthRequired);
        outcome.Message.Should().Contain("NIP-42");
    }

    [Fact]
    public async Task Query_AuthRequired_IsTyped()
    {
        await using var relay = new FakeNostrRelay("auth").Start();
        var q = await Client.QueryAsync(new JsonObject { ["kinds"] = new JsonArray { 38403 } }, relay.Url, T, CancellationToken.None);
        q.Status.Should().Be(RelayOutcome.AuthRequired);
    }

    [Fact]
    public async Task SilentRelay_TimesOut()
    {
        await using var relay = new FakeNostrRelay("silent").Start();
        var outcome = await Client.PublishAsync(Event(), relay.Url, TimeSpan.FromMilliseconds(500), CancellationToken.None);
        var q = await Client.QueryAsync(new JsonObject(), relay.Url, TimeSpan.FromMilliseconds(500), CancellationToken.None);
        outcome.Status.Should().Be(RelayOutcome.Timeout);
        q.Status.Should().Be(RelayOutcome.Timeout);
        q.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task UnreachableRelay_IsConnectFailed()
    {
        var outcome = await Client.PublishAsync(Event(), "ws://127.0.0.1:9/", TimeSpan.FromSeconds(2), CancellationToken.None);
        outcome.Status.Should().Be(RelayOutcome.ConnectFailed);
    }

    [Theory]
    [InlineData("wss://agents.lightningenable.com", true)]
    [InlineData("ws://127.0.0.1:7000", true)]
    [InlineData("ws://localhost:7000", true)]
    [InlineData("ws://agents.lightningenable.com", false)]
    [InlineData("https://agents.lightningenable.com", false)]
    [InlineData("wss://", false)]
    public void RelayUrlRule(string url, bool ok) => (NostrRelayClient.RelayUrlProblem(url) == null).Should().Be(ok);

    [Fact]
    public async Task PlainWsToRemoteHost_IsRefusedBeforeConnecting()
    {
        var outcome = await Client.PublishAsync(Event(), "ws://relay.example.com", T, CancellationToken.None);
        outcome.Status.Should().Be(RelayOutcome.InvalidRelayUrl);
    }

    [Fact]
    public void RelayUrl_DefaultsAndEnvOverride()
    {
        new NostrRelayClient(_ => null).DefaultRelayUrl.Should().Be(NostrRelayClient.DefaultRelay);
        new NostrRelayClient(_ => "${AGENT_RELAY_URL}").DefaultRelayUrl.Should().Be(NostrRelayClient.DefaultRelay);
        new NostrRelayClient(n => n == "AGENT_RELAY_URL" ? "wss://r.example" : null).DefaultRelayUrl.Should().Be("wss://r.example");
    }
}

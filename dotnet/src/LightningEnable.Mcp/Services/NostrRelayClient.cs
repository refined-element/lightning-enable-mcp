using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LightningEnable.Mcp.Services;

/// <summary>What a relay said. <see cref="Ok"/> is true only for an accepted publish or a query that reached EOSE.</summary>
public sealed record RelayOutcome(bool Ok, string Status, string Relay, string Message, IReadOnlyList<JsonNode?> Events)
{
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string AuthRequired = "auth_required";
    public const string Timeout = "timeout";
    public const string ConnectFailed = "connect_failed";
    public const string ProtocolError = "protocol_error";
    public const string InvalidRelayUrl = "invalid_relay_url";
    public const string Complete = "complete";

    internal static RelayOutcome Fail(string status, string relay, string message, IReadOnlyList<JsonNode?>? events = null)
        => new(false, status, relay, message, events ?? Array.Empty<JsonNode?>());
}

/// <summary>Publishes and queries Nostr events on a relay. Abstracted for tests.</summary>
public interface INostrRelayClient
{
    /// <summary>The relay attestations go to: <c>AGENT_RELAY_URL</c>, default <c>wss://agents.lightningenable.com</c>.</summary>
    string DefaultRelayUrl { get; }

    Task<RelayOutcome> PublishAsync(JsonObject ev, string relayUrl, TimeSpan timeout, CancellationToken ct);

    Task<RelayOutcome> QueryAsync(JsonObject filter, string relayUrl, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// A minimal Nostr relay client for attestations: publish one event (EVENT → OK) and run one
/// query (REQ → EVENT* → EOSE), each on its own short-lived websocket. Writing goes straight
/// from this client to the relay; it does not depend on the hosted Lightning Enable API.
/// The relay must be <c>wss://</c>; plain <c>ws://</c> is allowed only for a loopback host.
///
/// NIP-42 relay authentication is NOT implemented: when a relay demands it (an <c>AUTH</c>
/// challenge, or an OK/CLOSED message prefixed <c>auth-required:</c>) the call returns
/// <see cref="RelayOutcome.AuthRequired"/> with an explanation. Mirrors Python's <c>nostr_relay</c>.
/// </summary>
public sealed class NostrRelayClient : INostrRelayClient
{
    public const string RelayEnvVar = "AGENT_RELAY_URL";
    public const string DefaultRelay = "wss://agents.lightningenable.com";
    public const int MaxQueryEvents = 500;

    private static readonly HashSet<string> LoopbackHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "127.0.0.1", "[::1]", "::1",
    };

    public NostrRelayClient() : this(Environment.GetEnvironmentVariable)
    {
    }

    internal NostrRelayClient(Func<string, string?> env)
    {
        var v = (env(RelayEnvVar) ?? string.Empty).Trim();
        DefaultRelayUrl = v.Length == 0 || v.StartsWith("${", StringComparison.Ordinal) ? DefaultRelay : v;
    }

    public string DefaultRelayUrl { get; }

    /// <summary>Null when <paramref name="url"/> is an acceptable relay URL, else why not.</summary>
    public static string? RelayUrlProblem(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return "not a valid URL";
        if (uri.Scheme == "wss") return null;
        if (uri.Scheme == "ws" && LoopbackHosts.Contains(uri.Host)) return null;
        return "must be wss:// (ws:// is allowed only for localhost)";
    }

    public static string AuthRequiredMessage(string relay, string detail = "") =>
        $"The relay {relay} requires NIP-42 authentication before it accepts this. This server "
        + "does not implement relay AUTH yet. Ask the relay operator to accept kind 38403 events "
        + $"without AUTH (or to allowlist your reviewer pubkey), or set {RelayEnvVar} to a relay that does."
        + (detail.Length > 0 ? $" Relay said: {detail}" : "");

    public async Task<RelayOutcome> PublishAsync(JsonObject ev, string relayUrl, TimeSpan timeout, CancellationToken ct)
    {
        if (RelayUrlProblem(relayUrl) is { } problem)
            return RelayOutcome.Fail(RelayOutcome.InvalidRelayUrl, relayUrl, $"Relay URL {problem}.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri(relayUrl), cts.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return RelayOutcome.Fail(RelayOutcome.ConnectFailed, relayUrl, $"Could not connect: {ex.GetType().Name}");
        }

        var id = ev["id"]?.GetValue<string>();
        var sawAuth = false;
        try
        {
            await SendAsync(ws, new JsonArray { "EVENT", ev.DeepClone() }.ToJsonString(), cts.Token);
            while (true)
            {
                var frame = await ReceiveFrameAsync(ws, cts.Token);
                if (frame == null)
                {
                    return sawAuth
                        ? RelayOutcome.Fail(RelayOutcome.AuthRequired, relayUrl, AuthRequiredMessage(relayUrl))
                        : RelayOutcome.Fail(RelayOutcome.ProtocolError, relayUrl, "The relay closed the connection before replying.");
                }
                var kind = Str(frame[0]);
                if (kind == "AUTH")
                {
                    sawAuth = true;
                }
                else if (kind == "OK" && frame.Count >= 3 && Str(frame[1]) == id)
                {
                    var msg = frame.Count >= 4 ? Str(frame[3]) ?? "" : "";
                    var accepted = frame[2] is JsonValue b && b.TryGetValue<bool>(out var flag) && flag;
                    if (accepted) return new RelayOutcome(true, RelayOutcome.Accepted, relayUrl, msg, Array.Empty<JsonNode?>());
                    if (msg.StartsWith("auth-required:", StringComparison.Ordinal))
                        return RelayOutcome.Fail(RelayOutcome.AuthRequired, relayUrl, AuthRequiredMessage(relayUrl, msg));
                    return RelayOutcome.Fail(RelayOutcome.Rejected, relayUrl, msg.Length > 0 ? msg : "The relay refused the event.");
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return sawAuth
                ? RelayOutcome.Fail(RelayOutcome.AuthRequired, relayUrl, AuthRequiredMessage(relayUrl))
                : RelayOutcome.Fail(RelayOutcome.Timeout, relayUrl, $"No OK from the relay within {timeout.TotalSeconds:0.#}s.");
        }
        catch (Exception ex) when (ex is WebSocketException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return sawAuth
                ? RelayOutcome.Fail(RelayOutcome.AuthRequired, relayUrl, AuthRequiredMessage(relayUrl))
                : RelayOutcome.Fail(RelayOutcome.ProtocolError, relayUrl, $"Relay error: {ex.GetType().Name}");
        }
        finally
        {
            await CloseQuietlyAsync(ws);
        }
    }

    public async Task<RelayOutcome> QueryAsync(JsonObject filter, string relayUrl, TimeSpan timeout, CancellationToken ct)
    {
        if (RelayUrlProblem(relayUrl) is { } problem)
            return RelayOutcome.Fail(RelayOutcome.InvalidRelayUrl, relayUrl, $"Relay URL {problem}.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri(relayUrl), cts.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return RelayOutcome.Fail(RelayOutcome.ConnectFailed, relayUrl, $"Could not connect: {ex.GetType().Name}");
        }

        var sub = "att-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var events = new List<JsonNode?>();
        var sawAuth = false;
        try
        {
            await SendAsync(ws, new JsonArray { "REQ", sub, filter.DeepClone() }.ToJsonString(), cts.Token);
            while (true)
            {
                var frame = await ReceiveFrameAsync(ws, cts.Token);
                if (frame == null)
                    return RelayOutcome.Fail(RelayOutcome.ProtocolError, relayUrl, "The relay closed the connection before EOSE.", events);
                var kind = Str(frame[0]);
                if (kind == "AUTH")
                {
                    sawAuth = true;
                }
                else if (kind == "EVENT" && frame.Count >= 3 && Str(frame[1]) == sub)
                {
                    if (frame[2] is JsonObject e && events.Count < MaxQueryEvents) events.Add(e.DeepClone());
                }
                else if (kind == "EOSE" && frame.Count >= 2 && Str(frame[1]) == sub)
                {
                    try { await SendAsync(ws, new JsonArray { "CLOSE", sub }.ToJsonString(), cts.Token); }
                    catch { /* best effort */ }
                    return new RelayOutcome(true, RelayOutcome.Complete, relayUrl, "", events);
                }
                else if (kind == "CLOSED" && frame.Count >= 2 && Str(frame[1]) == sub)
                {
                    var msg = frame.Count >= 3 ? Str(frame[2]) ?? "" : "";
                    if (msg.StartsWith("auth-required:", StringComparison.Ordinal))
                        return RelayOutcome.Fail(RelayOutcome.AuthRequired, relayUrl, AuthRequiredMessage(relayUrl, msg), events);
                    return RelayOutcome.Fail(RelayOutcome.Rejected, relayUrl, msg.Length > 0 ? msg : "The relay closed the query.", events);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return sawAuth && events.Count == 0
                ? RelayOutcome.Fail(RelayOutcome.AuthRequired, relayUrl, AuthRequiredMessage(relayUrl), events)
                : RelayOutcome.Fail(RelayOutcome.Timeout, relayUrl, $"No EOSE from the relay within {timeout.TotalSeconds:0.#}s.", events);
        }
        catch (Exception ex) when (ex is WebSocketException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return RelayOutcome.Fail(RelayOutcome.ProtocolError, relayUrl, $"Relay error: {ex.GetType().Name}", events);
        }
        finally
        {
            await CloseQuietlyAsync(ws);
        }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static Task SendAsync(ClientWebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    /// <summary>The next JSON-array frame, skipping anything unparseable; null when the socket closes.</summary>
    private static async Task<JsonArray?> ReceiveFrameAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buffer, 0, result.Count);
                if (ms.Length > 1024 * 1024) throw new InvalidOperationException("relay frame too large");
            }
            while (!result.EndOfMessage);

            try
            {
                if (JsonNode.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length)) is JsonArray arr
                    && arr.Count > 0 && Str(arr[0]) != null)
                    return arr;
            }
            catch (System.Text.Json.JsonException)
            {
                // skip a malformed frame
            }
        }
    }

    private static async Task CloseQuietlyAsync(ClientWebSocket ws)
    {
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closeCts.Token);
            }
        }
        catch
        {
            // already gone
        }
    }
}

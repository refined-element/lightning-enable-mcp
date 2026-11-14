using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// In-process Nostr relay for attestation tests; listens on 127.0.0.1 only, no real network.
/// Modes: <c>accept</c> (OK true, stores events, serves REQ by kinds and single-letter tag
/// filters), <c>reject</c> (OK false "blocked: ..."), <c>auth</c> (sends an AUTH challenge on
/// connect, then OK false / CLOSED "auth-required: ..."), <c>silent</c> (never answers).
/// Mirrors Python's <c>tests/fake_nostr_relay.py</c>.
/// </summary>
internal sealed class FakeNostrRelay : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public string Mode { get; }
    public string Url { get; }
    public ConcurrentQueue<JsonArray> Received { get; } = new();
    public List<JsonObject> Stored { get; } = new();
    public List<JsonObject> ExtraEvents { get; } = new();

    public FakeNostrRelay(string mode = "accept")
    {
        Mode = mode;
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        Url = $"ws://127.0.0.1:{port}/";
    }

    public FakeNostrRelay Start()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
        return this;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                continue;
            }
            var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            _ = Task.Run(() => HandleAsync(ws));
        }
    }

    private async Task HandleAsync(WebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        try
        {
            if (Mode == "auth") await SendAsync(ws, new JsonArray { "AUTH", "challenge-123" });
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buffer, _cts.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                    break;
                }
                sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                if (!r.EndOfMessage) continue;
                var frame = JsonNode.Parse(sb.ToString())!.AsArray();
                sb.Clear();
                Received.Enqueue(frame);
                if (Mode == "silent") continue;

                var type = frame[0]!.GetValue<string>();
                if (type == "EVENT")
                {
                    var ev = frame[1]!.AsObject();
                    var id = ev["id"]!.GetValue<string>();
                    if (Mode == "reject")
                        await SendAsync(ws, new JsonArray { "OK", id, false, "blocked: not on the allowlist" });
                    else if (Mode == "auth")
                        await SendAsync(ws, new JsonArray { "OK", id, false, "auth-required: sign in first" });
                    else
                    {
                        lock (Stored) Stored.Add(ev.DeepClone().AsObject());
                        await SendAsync(ws, new JsonArray { "OK", id, true, "" });
                    }
                }
                else if (type == "REQ")
                {
                    var sub = frame[1]!.GetValue<string>();
                    if (Mode == "auth")
                    {
                        await SendAsync(ws, new JsonArray { "CLOSED", sub, "auth-required: sign in first" });
                        continue;
                    }
                    var filter = frame.Count > 2 ? frame[2]!.AsObject() : new JsonObject();
                    List<JsonObject> all;
                    lock (Stored) all = ExtraEvents.Concat(Stored).ToList();
                    foreach (var ev in all.Where(e => Matches(e, filter)))
                        await SendAsync(ws, new JsonArray { "EVENT", sub, ev.DeepClone() });
                    await SendAsync(ws, new JsonArray { "EOSE", sub });
                }
            }
        }
        catch
        {
            // connection closed / shutdown
        }
    }

    private static bool Matches(JsonObject ev, JsonObject filter)
    {
        if (filter["kinds"] is JsonArray kinds && !kinds.Any(k => k!.GetValue<int>() == ev["kind"]!.GetValue<int>()))
            return false;
        foreach (var (key, value) in filter)
        {
            if (!key.StartsWith('#') || key.Length != 2 || value is not JsonArray wanted) continue;
            var name = key[1].ToString();
            var have = ev["tags"]!.AsArray()
                .Select(t => t!.AsArray())
                .Where(t => t.Count >= 2 && t[0]!.GetValue<string>() == name)
                .Select(t => t[1]!.GetValue<string>())
                .ToHashSet();
            if (!wanted.Any(w => have.Contains(w!.GetValue<string>()))) return false;
        }
        return true;
    }

    private static Task SendAsync(WebSocket ws, JsonArray frame) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(frame.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
        if (_loop != null) { try { await _loop; } catch { } }
        _cts.Dispose();
    }
}

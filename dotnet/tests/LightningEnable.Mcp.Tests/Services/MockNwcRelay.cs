using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LightningEnable.Mcp.Services;
using NBitcoin.Secp256k1;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// Minimal in-process Nostr relay that plays the NWC wallet side, for end-to-end
/// <see cref="NwcWalletService.PayInvoiceAsync"/> tests without a real relay or wallet.
///
/// Behaviour: accepts a client connection, reads framed Nostr messages, and on the
/// kind-23194 EVENT it decrypts the request (NIP-04/44 auto-detect) using the configured
/// wallet private key, then publishes a signed, encrypted kind-23195 response carrying the
/// supplied preimage. The response is tagged with the request event id (#e) and signed by
/// <see cref="_walletPubkeyHex"/> so it passes the client's F-11 verification gate.
///
/// Ported from the sibling library L402Requests' MockNwcRelay, retargeted at the MCP
/// server's <see cref="NwcWalletService"/> crypto helpers (EncryptNip04 / EncryptNip44 /
/// DecryptContent) and its canonical event-id / signature scheme.
/// </summary>
internal sealed class MockNwcRelay : IAsyncDisposable
{
    // Same relaxed-escaping encoder the production NwcWalletService uses for the canonical
    // Nostr serialisation — guarantees the event id we compute here is byte-identical to the
    // one NwcWalletService.VerifyNostrEventSignature recomputes when it validates our reply.
    private static readonly JsonSerializerOptions NostrJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HttpListener _listener;
    private readonly ECPrivKey _walletPriv;
    private readonly string _walletPubkeyHex;
    private readonly string _preimageHex;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public string Url { get; }

    /// <summary>
    /// When set, a REQ for kind 13194 (the client's NIP-47 INFO-event auto-detect probe) is
    /// answered with a signed kind-13194 event whose <c>encryption</c> tag carries this value
    /// (e.g. <c>"nip04 nip44_v2"</c>), then EOSE. When null, the probe gets EOSE only (an older
    /// wallet that never published a 13194 event → client falls back to NIP-04).
    /// </summary>
    public string? InfoEncryptionTag { get; set; }

    public MockNwcRelay(ECPrivKey walletPriv, string walletPubkeyHex, string preimageHex)
    {
        _walletPriv = walletPriv;
        _walletPubkeyHex = walletPubkeyHex;
        _preimageHex = preimageHex;

        var port = GetFreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        Url = $"ws://127.0.0.1:{port}/";
    }

    public Task StartAsync()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync()
    {
        try
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

                var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
                _ = Task.Run(() => HandleClientAsync(wsCtx.WebSocket));
            }
        }
        catch
        {
            // Listener disposed during shutdown — expected.
        }
    }

    private async Task HandleClientAsync(WebSocket ws)
    {
        var buffer = new byte[16384];
        var sb = new StringBuilder();
        string? subId = null;

        try
        {
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Echo the close so the client's CloseAsync (a full close HANDSHAKE that waits
                    // for the peer's close reply — NwcWalletService closes with CancellationToken.None)
                    // completes instead of hanging. Real relays complete the handshake too.
                    try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                    catch { /* peer already gone */ }
                    break;
                }

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                var message = sb.ToString();
                sb.Clear();

                var arr = JsonNode.Parse(message)?.AsArray();
                if (arr == null || arr.Count < 2) continue;

                var type = arr[0]?.GetValue<string>();
                if (type == "REQ")
                {
                    var reqSubId = arr[1]?.GetValue<string>();
                    var filter = arr.Count > 2 ? arr[2]?.AsObject() : null;
                    var kinds = filter?["kinds"]?.AsArray();
                    var wantsInfo = kinds != null && kinds.Any(k => k?.GetValue<int>() == 13194);

                    if (wantsInfo)
                    {
                        // The client's NIP-47 INFO-event auto-detect probe (its own connection).
                        // Answer with a signed 13194 event when configured, then EOSE.
                        if (InfoEncryptionTag != null)
                        {
                            var infoEv = BuildSignedInfoEvent(InfoEncryptionTag);
                            var infoMsg = new JsonArray { "EVENT", reqSubId, JsonNode.Parse(infoEv.ToJsonString(NostrJsonOptions)) };
                            await SendAsync(ws, infoMsg.ToJsonString(NostrJsonOptions));
                        }
                        await SendAsync(ws, new JsonArray { "EOSE", reqSubId }.ToJsonString());
                    }
                    else
                    {
                        // The pay-response subscription (kind 23195). Remember its id for the reply.
                        subId = reqSubId;
                        await SendAsync(ws, new JsonArray { "EOSE", reqSubId }.ToJsonString());
                    }
                }
                else if (type == "EVENT" && arr.Count >= 2)
                {
                    var ev = arr[1]?.AsObject();
                    if (ev == null) continue;
                    if (ev["kind"]?.GetValue<int>() != 23194) continue;

                    var reqEventId = ev["id"]?.GetValue<string>() ?? "";
                    var clientPubHex = ev["pubkey"]?.GetValue<string>() ?? "";
                    var clientPubBytes = Convert.FromHexString(clientPubHex);
                    var encryptedReq = ev["content"]?.GetValue<string>() ?? "";

                    // Acknowledge the published event (relay-level OK).
                    await SendAsync(ws, new JsonArray { "OK", reqEventId, true, "" }.ToJsonString());

                    // Decrypt the request to confirm it's a real pay_invoice (NIP-04 has "?iv=";
                    // NIP-44 v2 is a single base64 blob — auto-detected by DecryptContent).
                    var isNip44Request = !encryptedReq.Contains("?iv=");
                    try
                    {
                        var decryptedReq = NwcWalletService.DecryptContent(encryptedReq, clientPubBytes, _walletPriv);
                        using var reqDoc = JsonDocument.Parse(decryptedReq);
                        var method = reqDoc.RootElement.GetProperty("method").GetString();
                        if (method != "pay_invoice") continue;
                    }
                    catch
                    {
                        // Couldn't decrypt/parse — don't reply.
                        continue;
                    }

                    // Reply as a signed kind-23195 event carrying the preimage. NwcWalletService
                    // filters replies by result_type == "pay_invoice", so the envelope must match.
                    var responsePayload = new JsonObject
                    {
                        ["result_type"] = "pay_invoice",
                        ["result"] = new JsonObject { ["preimage"] = _preimageHex }
                    }.ToJsonString();

                    // Reply using the same scheme the client used, so a NIP-44 request gets a
                    // NIP-44 reply (the client auto-detects inbound regardless).
                    var encryptedResp = isNip44Request
                        ? NwcWalletService.EncryptNip44(responsePayload, clientPubBytes, _walletPriv)
                        : NwcWalletService.EncryptNip04(responsePayload, clientPubBytes, _walletPriv);

                    var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var tags = new JsonArray
                    {
                        new JsonArray { "p", clientPubHex },
                        new JsonArray { "e", reqEventId }
                    };
                    var respId = ComputeEventId(_walletPubkeyHex, createdAt, 23195, tags, encryptedResp);
                    _walletPriv.TrySignBIP340(Convert.FromHexString(respId), null, out var sig);

                    var respEvent = new JsonObject
                    {
                        ["id"] = respId,
                        ["pubkey"] = _walletPubkeyHex,
                        ["created_at"] = createdAt,
                        ["kind"] = 23195,
                        ["tags"] = JsonNode.Parse(tags.ToJsonString(NostrJsonOptions)),
                        ["content"] = encryptedResp,
                        ["sig"] = Convert.ToHexString(sig!.ToBytes()).ToLowerInvariant()
                    };

                    var outMsg = new JsonArray { "EVENT", subId, JsonNode.Parse(respEvent.ToJsonString(NostrJsonOptions)) };
                    await SendAsync(ws, outMsg.ToJsonString(NostrJsonOptions));
                }
            }
        }
        catch
        {
            // Connection closed / cancelled during shutdown — expected.
        }
    }

    /// <summary>
    /// Builds a signed kind-13194 (NIP-47 INFO) event advertising the given encryption
    /// schemes, so the client's auto-detect probe verifies it and reads the tag.
    /// </summary>
    private JsonObject BuildSignedInfoEvent(string encryptionTag)
    {
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var tags = new JsonArray { new JsonArray { "encryption", encryptionTag } };
        const string content = "Wallet capabilities: pay_invoice get_balance make_invoice";
        var id = ComputeEventId(_walletPubkeyHex, createdAt, 13194, tags, content);
        _walletPriv.TrySignBIP340(Convert.FromHexString(id), null, out var sig);

        return new JsonObject
        {
            ["id"] = id,
            ["pubkey"] = _walletPubkeyHex,
            ["created_at"] = createdAt,
            ["kind"] = 13194,
            ["tags"] = JsonNode.Parse(tags.ToJsonString(NostrJsonOptions)),
            ["content"] = content,
            ["sig"] = Convert.ToHexString(sig!.ToBytes()).ToLowerInvariant()
        };
    }

    /// <summary>
    /// Computes the Nostr event id exactly as NwcWalletService does: SHA256 over the canonical
    /// serialised array [0, pubkey, created_at, kind, tags, content] using relaxed JSON escaping.
    /// Replicated here (NwcWalletService.ComputeEventId is private) so the mock's replies verify.
    /// </summary>
    private static string ComputeEventId(string pubkey, long createdAt, int kind, JsonArray tags, string content)
    {
        var eventArray = new JsonArray
        {
            0, pubkey, createdAt, kind, JsonNode.Parse(tags.ToJsonString(NostrJsonOptions)), content
        };
        var serialized = eventArray.ToJsonString(NostrJsonOptions);
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task SendAsync(WebSocket ws, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
        if (_acceptLoop != null)
        {
            try { await _acceptLoop; } catch { }
        }
        _cts.Dispose();
    }
}

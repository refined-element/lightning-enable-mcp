using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LightningEnable.Mcp.Models;
using NBitcoin.Secp256k1;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Service for interacting with a Lightning wallet via Nostr Wallet Connect (NWC).
/// Implements NIP-47 wallet connect protocol with proper secp256k1 cryptography.
/// </summary>
public class NwcWalletService : IWalletService, IDisposable
{
    // JSON options that don't escape special characters (needed for correct Nostr event ID)
    private static readonly JsonSerializerOptions NostrJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Track recently used preimages to detect stale relay responses.
    // Some NWC relays (e.g. Coinos) ignore #e tag filters and return cached
    // responses from previous payments. We reject any preimage already used.
    // Capped at MaxUsedPreimages to prevent unbounded memory growth.
    private const int MaxUsedPreimages = 10000;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _usedPreimages = new();

    private static void PruneUsedPreimagesIfNeeded()
    {
        if (_usedPreimages.Count <= MaxUsedPreimages) return;

        // Remove oldest entries (by timestamp) to get back to 75% capacity
        var toRemove = _usedPreimages
            .OrderBy(kvp => kvp.Value)
            .Take(_usedPreimages.Count - (MaxUsedPreimages * 3 / 4))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            _usedPreimages.TryRemove(key, out _);
        }
    }

    private readonly NwcConfig? _config;
    private readonly ECPrivKey? _privateKey;
    private readonly ECXOnlyPubKey? _publicKey;
    private readonly string? _myPubkeyHex;
    private readonly HttpClient _httpClient;
    private bool _disposed;

    // Auto-detect cache. When _config.Encryption == "auto", the first call to
    // SendNwcRequestAsync triggers a one-time fetch of the wallet's NIP-47 INFO
    // event (kind 13194); the resolved scheme ("nip04" or "nip44_v2") is stored
    // here and reused for the lifetime of the service instance. The lock
    // serialises concurrent first-request fetches so we don't open N relay
    // connections at startup.
    private string? _resolvedAutoEncryption;
    private readonly SemaphoreSlim _autoResolveLock = new(1, 1);

    /// <summary>
    /// How long to wait for the NIP-47 INFO event before falling back to NIP-04.
    /// Made internal so tests can override with reflection. Kept short so a missing
    /// or stale relay never delays a real request by more than a few seconds.
    /// </summary>
    internal static TimeSpan AutoResolveTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Test-only instrumentation. Incremented every time
    /// <see cref="FetchEncryptionFromInfoEventAsync"/> is actually invoked
    /// (i.e., NOT when the cache short-circuits). Tests assert this stays at 1
    /// across multiple <see cref="ResolveAutoEncryptionAsync"/> calls to verify
    /// the cache is working — preferred over wall-clock timing thresholds which
    /// flake on busy CI agents.
    /// </summary>
    internal int InfoEventFetchCount;

    public NwcWalletService(HttpClient httpClient, IBudgetConfigurationService? budgetConfigService = null)
    {
        _httpClient = httpClient;

        // Try environment variable first, then config file
        var connectionString = Environment.GetEnvironmentVariable("NWC_CONNECTION_STRING");
        if (string.IsNullOrEmpty(connectionString) || connectionString.StartsWith("${"))
        {
            // Env var not set or not expanded - try config file
            connectionString = budgetConfigService?.Configuration?.Wallets?.NwcConnectionString;
            if (!string.IsNullOrEmpty(connectionString))
            {
                Console.Error.WriteLine("[NWC] Using connection string from config file");
            }
        }
        _config = NwcConfig.TryParse(connectionString);

        if (_config != null)
        {
            // Outbound encryption override. Default is "auto" (per NwcEncryption.Default)
            // which fetches the wallet's NIP-47 INFO event and picks the strongest
            // advertised scheme. NWC_ENCRYPTION lets the operator pin to "auto",
            // "nip04", or "nip44_v2" — useful when the INFO fetch is unreliable on
            // a particular relay or when the wallet doesn't publish kind 13194 and
            // the operator already knows which scheme it accepts. Invalid values are
            // ignored with a warning so a typo doesn't silently break requests on a
            // previously-working wallet.
            var encOverride = Environment.GetEnvironmentVariable("NWC_ENCRYPTION");
            if (!string.IsNullOrWhiteSpace(encOverride))
            {
                var normalized = encOverride.Trim().ToLowerInvariant();
                if (NwcEncryption.IsValid(normalized))
                {
                    _config = _config with { Encryption = normalized };
                    Console.Error.WriteLine($"[NWC] Outbound encryption overridden via NWC_ENCRYPTION: {normalized}");
                }
                else
                {
                    Console.Error.WriteLine($"[NWC] Ignoring invalid NWC_ENCRYPTION='{encOverride}' (allowed: {NwcEncryption.AllowedValuesCsv}). Falling back to default '{NwcEncryption.Default}'.");
                }
            }

            // Derive secp256k1 key pair from secret
            var secretBytes = Convert.FromHexString(_config.Secret);
            if (ECPrivKey.TryCreate(secretBytes, out var privKey))
            {
                _privateKey = privKey;
                _publicKey = privKey.CreateXOnlyPubKey();
                _myPubkeyHex = Convert.ToHexString(_publicKey.ToBytes()).ToLowerInvariant();
            }
        }
    }

    public bool IsConfigured => _config != null && _privateKey != null;

    public string ProviderName => "NWC";

    public NwcConfig? GetConfig() => _config;

    public async Task<NwcPaymentResult> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken = default)
    {
        void DebugLog(string msg)
        {
            Console.Error.WriteLine($"[NWC] {msg}");
        }

        DebugLog("=== PayInvoiceAsync called ===");
        DebugLog($"Invoice: {bolt11[..Math.Min(30, bolt11.Length)]}...");

        if (_config == null || _privateKey == null)
        {
            DebugLog("ERROR: NWC not configured");
            return NwcPaymentResult.Failed("NOT_CONFIGURED", "NWC connection string not configured. Set NWC_CONNECTION_STRING environment variable.");
        }

        try
        {
            // Create NIP-47 pay_invoice request
            var request = new JsonObject
            {
                ["method"] = "pay_invoice",
                ["params"] = new JsonObject
                {
                    ["invoice"] = bolt11
                }
            };

            // Extract payment hash from BOLT11 for preimage verification
            var expectedPaymentHash = ExtractPaymentHashFromBolt11(bolt11);
            DebugLog($"Payment hash from BOLT11: {(expectedPaymentHash != null ? Convert.ToHexString(expectedPaymentHash).ToLowerInvariant() : "PARSE_FAILED")}");

            DebugLog("Sending NWC request...");
            var outcome = await SendNwcRequestAsync(request, cancellationToken, expectedPaymentHash);

            if (!outcome.Success)
            {
                DebugLog($"ERROR: {outcome.FormatFailure()}");
                // Map structured failure kind to a stable error code so callers can
                // discriminate (e.g. retry on no_response, abort on connect_failed).
                var code = outcome.FailureKind switch
                {
                    FailKindConnect => "CONNECTION_ERROR",
                    FailKindNoResponse => "NO_RESPONSE",
                    FailKindCancelled => "CANCELLED",
                    FailKindProtocol => "PROTOCOL_ERROR",
                    _ => "ERROR"
                };
                return NwcPaymentResult.Failed(code, outcome.FormatFailure() ?? "Unknown NWC failure");
            }

            var response = outcome.Response!;
            DebugLog($"Got response (result_type: {response["result_type"]?.GetValue<string>() ?? "unknown"})");

            // Check for error response
            var error = response["error"]?.AsObject();
            if (error != null)
            {
                var code = error["code"]?.GetValue<string>() ?? "UNKNOWN";
                var message = error["message"]?.GetValue<string>() ?? "Unknown error";
                DebugLog($"NWC Error: {code} - {message}");
                return NwcPaymentResult.Failed(code, message);
            }

            // Extract preimage from result
            var result = response["result"]?.AsObject();
            var preimage = result?["preimage"]?.GetValue<string>();

            DebugLog($"Preimage present: {!string.IsNullOrEmpty(preimage)}, length: {preimage?.Length ?? 0}");

            if (string.IsNullOrEmpty(preimage))
            {
                DebugLog("ERROR: No preimage in response!");
                DebugLog("No preimage in result object");
                return NwcPaymentResult.Failed("NO_PREIMAGE", "Payment succeeded but no preimage returned");
            }

            // Validate preimage format (should be 64 hex chars = 32 bytes)
            var isValidHex = preimage.Length == 64 && preimage.All(c => "0123456789abcdefABCDEF".Contains(c));
            DebugLog($"Is valid 64-char hex: {isValidHex}");

            if (!isValidHex)
            {
                DebugLog("WARNING: Preimage is not valid 64 hex chars");
                // Check if it looks like a UUID
                if (preimage.Contains('-') && preimage.Length == 36)
                {
                    DebugLog("DETECTED: Preimage looks like a UUID - Coinos internal transfer bug");
                }
            }

            DebugLog("Returning preimage");
            return NwcPaymentResult.Succeeded(preimage);
        }
        catch (Exception ex)
        {
            DebugLog($"EXCEPTION: {ex.Message}");
            DebugLog($"Stack: {ex.StackTrace}");
            return NwcPaymentResult.Failed("EXCEPTION", ex.Message);
        }
    }

    public async Task<NwcBalanceInfo> GetBalanceAsync(CancellationToken cancellationToken = default)
    {
        if (_config == null || _privateKey == null)
        {
            throw new InvalidOperationException("NWC connection string not configured");
        }

        // Create NIP-47 get_balance request
        var request = new JsonObject
        {
            ["method"] = "get_balance",
            ["params"] = new JsonObject()
        };

        var outcome = await SendNwcRequestAsync(request, cancellationToken);

        if (!outcome.Success)
        {
            // Bubble the actual failure reason instead of collapsing every failure
            // into a generic "Failed to connect" — that string was misleading because
            // SendNwcRequestAsync reaches this code path on connect failure, no-response
            // timeout, encryption mismatch, AND unexpected exceptions.
            throw new InvalidOperationException(outcome.FormatFailure() ?? "Unknown NWC failure");
        }

        var response = outcome.Response!;

        // Check for error
        var error = response["error"]?.AsObject();
        if (error != null)
        {
            var message = error["message"]?.GetValue<string>() ?? "Unknown error";
            throw new InvalidOperationException($"Get balance failed: {message}");
        }

        // Extract balance
        var result = response["result"]?.AsObject();
        var balanceMsat = result?["balance"]?.GetValue<long>() ?? 0;

        return new NwcBalanceInfo { BalanceMsat = balanceMsat };
    }

    /// <summary>
    /// Creates a Lightning invoice using NIP-47 make_invoice method.
    /// Note: Not all NWC wallets support this method.
    /// </summary>
    public async Task<WalletInvoiceResult> CreateInvoiceAsync(
        long amountSats,
        string? memo = null,
        int expirySecs = 3600,
        CancellationToken cancellationToken = default)
    {
        if (_config == null || _privateKey == null)
        {
            return WalletInvoiceResult.Failed("NOT_CONFIGURED",
                "NWC connection string not configured. Set NWC_CONNECTION_STRING environment variable.");
        }

        try
        {
            // Create NIP-47 make_invoice request
            // Amount is in millisatoshis for NWC
            var request = new JsonObject
            {
                ["method"] = "make_invoice",
                ["params"] = new JsonObject
                {
                    ["amount"] = amountSats * 1000, // Convert to millisatoshis
                    ["description"] = memo ?? "Lightning payment",
                    ["expiry"] = expirySecs
                }
            };

            Console.Error.WriteLine($"[NWC] Creating invoice for {amountSats} sats...");

            var outcome = await SendNwcRequestAsync(request, cancellationToken);

            if (!outcome.Success)
            {
                var code = outcome.FailureKind switch
                {
                    FailKindConnect => "CONNECTION_ERROR",
                    FailKindNoResponse => "NO_RESPONSE",
                    FailKindCancelled => "CANCELLED",
                    FailKindProtocol => "PROTOCOL_ERROR",
                    _ => "ERROR"
                };
                return WalletInvoiceResult.Failed(code, outcome.FormatFailure() ?? "Unknown NWC failure");
            }

            var response = outcome.Response!;

            var error = response["error"]?.AsObject();
            if (error != null)
            {
                var code = error["code"]?.GetValue<string>() ?? "UNKNOWN";
                var message = error["message"]?.GetValue<string>() ?? "Unknown error";
                return WalletInvoiceResult.Failed(code, message);
            }

            var result = response["result"]?.AsObject();
            var bolt11 = result?["invoice"]?.GetValue<string>();
            var paymentHash = result?["payment_hash"]?.GetValue<string>();

            if (string.IsNullOrEmpty(bolt11))
            {
                return WalletInvoiceResult.Failed("NO_INVOICE", "No invoice returned from wallet");
            }

            Console.Error.WriteLine($"[NWC] Invoice created");

            return WalletInvoiceResult.Succeeded(
                paymentHash ?? "",
                bolt11,
                amountSats,
                DateTime.UtcNow.AddSeconds(expirySecs));
        }
        catch (Exception ex)
        {
            return WalletInvoiceResult.Failed("EXCEPTION", ex.Message);
        }
    }

    /// <summary>
    /// Checks invoice status - NIP-47 doesn't have a standard method for this.
    /// </summary>
    public Task<WalletInvoiceStatus> GetInvoiceStatusAsync(string invoiceId, CancellationToken cancellationToken = default)
    {
        // NIP-47 doesn't have a standard invoice lookup method
        return Task.FromResult(WalletInvoiceStatus.Failed("NOT_SUPPORTED",
            "NWC does not support invoice status lookup. Check your wallet app directly."));
    }

    /// <summary>
    /// Gets BTC price ticker - not supported by NWC.
    /// </summary>
    public Task<WalletTickerResult> GetTickerAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(WalletTickerResult.NotSupported());
    }

    /// <summary>
    /// Sends an on-chain Bitcoin payment - not supported by NWC protocol.
    /// </summary>
    public Task<OnChainPaymentResult> SendOnChainAsync(
        string address,
        long amountSats,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(OnChainPaymentResult.NotSupported());
    }

    /// <summary>
    /// Exchanges currency - not supported by NWC protocol.
    /// </summary>
    public Task<CurrencyExchangeResult> ExchangeCurrencyAsync(
        string sourceCurrency,
        string targetCurrency,
        decimal amount,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(CurrencyExchangeResult.NotSupported());
    }

    /// <summary>
    /// Gets all currency balances - NWC is Lightning-only (no fiat).
    /// </summary>
    public async Task<MultiCurrencyBalance> GetAllBalancesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var balance = await GetBalanceAsync(cancellationToken);
            var sats = balance.BalanceSats;

            // Convert to BTC for consistency
            return MultiCurrencyBalance.Succeeded(new List<CurrencyBalance>
            {
                new CurrencyBalance
                {
                    Currency = "BTC",
                    Available = sats / 100_000_000m,
                    Total = sats / 100_000_000m,
                    Pending = 0
                }
            });
        }
        catch (Exception ex)
        {
            return MultiCurrencyBalance.Failed("ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Outcome of an NWC request round-trip. Carries either a successful response or a
    /// structured failure reason (kind + detail) so callers can surface the actual cause
    /// instead of collapsing every failure into a generic "connect failed" message.
    /// </summary>
    internal sealed record NwcSendOutcome(JsonObject? Response, string? FailureKind, string? FailureDetail)
    {
        public bool Success => Response != null;
        public static NwcSendOutcome Ok(JsonObject response) => new(response, null, null);
        public static NwcSendOutcome Fail(string kind, string detail) => new(null, kind, detail);

        /// <summary>
        /// Renders this outcome as a user-facing message suitable for an error string.
        /// Returns null if the outcome was successful.
        /// </summary>
        public string? FormatFailure() =>
            Success ? null : $"NWC request failed ({FailureKind}): {FailureDetail}";
    }

    // Failure-kind constants — used by FormatFailure() and asserted by tests so we
    // don't drift the user-facing error contract.
    internal const string FailKindConnect = "connect_failed";
    internal const string FailKindNoResponse = "no_response";
    internal const string FailKindCancelled = "cancelled";
    internal const string FailKindProtocol = "protocol_error";
    internal const string FailKindUnknown = "unknown";

    /// <summary>
    /// Picks the strongest encryption scheme advertised in <paramref name="encryptionTagValue"/>,
    /// which is the value of the NIP-47 INFO event's <c>encryption</c> tag (a space-separated
    /// list of supported schemes per the spec, e.g. <c>"nip04 nip44_v2"</c>).
    /// Prefers <see cref="NwcEncryption.Nip44V2"/> when both are listed; otherwise picks
    /// <see cref="NwcEncryption.Nip04"/>; falls back to <see cref="NwcEncryption.Nip04"/>
    /// when the tag is empty/missing/unknown (NIP-04 is the original NIP-47 default so it's
    /// the safest fallback for spec-pre-13194 wallets).
    /// Pulled out as a static method so it can be unit-tested without a relay.
    /// </summary>
    internal static string PickEncryptionFromInfoTag(string? encryptionTagValue)
    {
        if (string.IsNullOrWhiteSpace(encryptionTagValue))
            return NwcEncryption.Nip04;

        var schemes = encryptionTagValue
            .Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToHashSet();

        if (schemes.Contains(NwcEncryption.Nip44V2)) return NwcEncryption.Nip44V2;
        if (schemes.Contains(NwcEncryption.Nip04)) return NwcEncryption.Nip04;
        return NwcEncryption.Nip04;
    }

    /// <summary>
    /// Resolves the outbound encryption scheme by fetching the wallet's NIP-47 INFO
    /// event (kind 13194). Caches on the service instance — subsequent calls return
    /// the cached choice without a relay round trip. On any failure (relay unreachable,
    /// timeout, malformed event) falls back to <see cref="NwcEncryption.Nip04"/>, which
    /// is the original NIP-47 default and what every spec-pre-13194 wallet expects.
    /// </summary>
    /// <remarks>
    /// Concurrent first calls are serialised by <see cref="_autoResolveLock"/> so we
    /// don't open N relay connections for N parallel first-requests at startup.
    /// </remarks>
    internal async Task<string> ResolveAutoEncryptionAsync(CancellationToken cancellationToken)
    {
        // Fast-path cache check (volatile read is fine — single-writer once initialised)
        if (_resolvedAutoEncryption != null)
            return _resolvedAutoEncryption;

        await _autoResolveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring the lock — another caller may have populated it.
            if (_resolvedAutoEncryption != null)
                return _resolvedAutoEncryption;

            var resolved = await FetchEncryptionFromInfoEventAsync(cancellationToken).ConfigureAwait(false);
            _resolvedAutoEncryption = resolved;
            Console.Error.WriteLine($"[NWC] Auto-detect resolved outbound encryption: {resolved}");
            return resolved;
        }
        finally
        {
            _autoResolveLock.Release();
        }
    }

    /// <summary>
    /// One-shot WebSocket REQ for the wallet's kind 13194 (NIP-47 INFO) event,
    /// reads the <c>encryption</c> tag, and returns the picked scheme. Always returns
    /// a value — exceptions and timeouts are translated to the NIP-04 fallback.
    /// </summary>
    private async Task<string> FetchEncryptionFromInfoEventAsync(CancellationToken cancellationToken)
    {
        if (_config == null) return NwcEncryption.Nip04;

        // Test-only: count actual fetcher invocations so cache-hit tests can
        // assert this stays at 1 instead of relying on wall-clock thresholds.
        System.Threading.Interlocked.Increment(ref InfoEventFetchCount);

        using var ws = new ClientWebSocket();
        try
        {
            using var timeout = new CancellationTokenSource(AutoResolveTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var relayUri = new Uri(_config.RelayUrl);
            await ws.ConnectAsync(relayUri, linked.Token).ConfigureAwait(false);

            // Subscribe to the wallet's INFO event. NIP-47 says the wallet service
            // SHOULD publish kind 13194 — relays usually have it stored, so this
            // returns quickly with EVENT then EOSE. If the wallet hasn't published
            // one (older implementation), we hit EOSE without an EVENT and fall back.
            var subId = Guid.NewGuid().ToString("N")[..8];
            var reqMessage = new JsonArray
            {
                "REQ",
                subId,
                new JsonObject
                {
                    ["kinds"] = new JsonArray { 13194 },
                    ["authors"] = new JsonArray { _config.WalletPubkey },
                    ["limit"] = 1
                }
            };
            var reqBytes = Encoding.UTF8.GetBytes(reqMessage.ToJsonString(NostrJsonOptions));
            await ws.SendAsync(reqBytes, WebSocketMessageType.Text, true, linked.Token).ConfigureAwait(false);

            var buffer = new byte[8192];
            var sb = new StringBuilder();
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, linked.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                var message = sb.ToString();
                sb.Clear();

                var parsed = JsonNode.Parse(message)?.AsArray();
                if (parsed == null || parsed.Count < 2) continue;

                var msgType = parsed[0]?.GetValue<string>();
                if (msgType == "EVENT" && parsed.Count >= 3)
                {
                    // Validate the subscription id matches the one we just generated.
                    // A relay (or hostile peer) could otherwise inject an unsolicited
                    // EVENT we'd treat as the wallet's INFO event and silently downgrade
                    // (or upgrade) the encryption scheme used for real NIP-47 calls.
                    var rcvSubId = parsed[1]?.GetValue<string>();
                    if (rcvSubId != subId) continue;

                    var ev = parsed[2]?.AsObject();
                    if (ev?["kind"]?.GetValue<int>() != 13194) continue;

                    // Defence in depth: verify the event was published by the wallet
                    // pubkey we're talking to.
                    var pubkeyHex = ev["pubkey"]?.GetValue<string>();
                    if (!string.Equals(pubkeyHex, _config.WalletPubkey, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Cryptographic verification of the event signature. Without this,
                    // a malicious relay could forge a kind 13194 event attributed to
                    // the wallet pubkey and force an encryption downgrade or DoS.
                    // VerifyNostrEventSignature recomputes the event id from the
                    // canonical serialisation and verifies the BIP340 Schnorr signature
                    // against the claimed pubkey — so any tampered tag (including the
                    // encryption tag we're about to read) breaks verification.
                    if (!VerifyNostrEventSignature(ev))
                    {
                        Console.Error.WriteLine("[NWC] INFO event signature verification failed; ignoring");
                        continue;
                    }

                    var encTagValue = ev["tags"]?.AsArray()
                        .Select(t => t?.AsArray())
                        .Where(t => t != null && t.Count >= 2 && t[0]?.GetValue<string>() == "encryption")
                        .Select(t => t![1]?.GetValue<string>())
                        .FirstOrDefault();
                    return PickEncryptionFromInfoTag(encTagValue);
                }
                else if (msgType == "EOSE")
                {
                    // EOSE is sub-id-scoped too — ignore EOSEs for other subscriptions.
                    var rcvSubId = parsed[1]?.GetValue<string>();
                    if (rcvSubId != subId) continue;
                    // No INFO event in stored history — older wallet that never
                    // published 13194. Fall back to NIP-04.
                    Console.Error.WriteLine("[NWC] No NIP-47 INFO event found; falling back to NIP-04");
                    return NwcEncryption.Nip04;
                }
            }

            Console.Error.WriteLine("[NWC] WS closed before INFO event arrived; falling back to NIP-04");
            return NwcEncryption.Nip04;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancellation must propagate untouched so awaiting tasks can shut
            // down cleanly. The outer SendNwcRequestAsync wrapper translates this to
            // a cancelled outcome.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Internal AutoResolveTimeout fired; safe fallback.
            Console.Error.WriteLine($"[NWC] INFO-event fetch timed out after {AutoResolveTimeout.TotalSeconds}s; falling back to NIP-04");
            return NwcEncryption.Nip04;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NWC] INFO-event fetch failed ({ex.GetType().Name}: {ex.Message}); falling back to NIP-04");
            return NwcEncryption.Nip04;
        }
        finally
        {
            if (ws.State == WebSocketState.Open)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None).ConfigureAwait(false); }
                catch { /* best-effort close */ }
            }
        }
    }

    private async Task<NwcSendOutcome> SendNwcRequestAsync(JsonObject request, CancellationToken cancellationToken, byte[]? expectedPaymentHash = null)
    {
        if (_config == null || _privateKey == null || _publicKey == null || _myPubkeyHex == null)
            return NwcSendOutcome.Fail(FailKindUnknown, "NWC connection string not configured");

        // Extract the method from the request so we can filter responses by result_type
        var expectedResultType = request["method"]?.GetValue<string>();
        Console.Error.WriteLine($"[NWC] Sending request, method: {expectedResultType}, encryption: {_config.Encryption}");

        using var ws = new ClientWebSocket();
        Uri relayUri;

        // Connect first; report a specific failure kind if the WS handshake itself fails.
        // Separating connect from send/receive lets the caller distinguish "couldn't reach
        // the relay" from "relay accepted us but the wallet never replied" — the latter
        // is the most common silent-encryption-mismatch symptom.
        // Uri construction is included in the try because a malformed RelayUrl would
        // throw UriFormatException; that has to flow through the structured outcome
        // rather than escape as an unhandled exception to the caller.
        try
        {
            relayUri = new Uri(_config.RelayUrl);
            Console.Error.WriteLine($"[NWC] Connecting to: {relayUri}");
            await ws.ConnectAsync(relayUri, cancellationToken);
            Console.Error.WriteLine($"[NWC] Connected, state: {ws.State}");
        }
        catch (UriFormatException ex)
        {
            return NwcSendOutcome.Fail(FailKindConnect, $"Configured NWC relay URL '{_config.RelayUrl}' is not a valid URI: {ex.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancelled — preserve cancelled semantics. (At this point
            // the only token in flight is the caller's; no timeout is armed yet.)
            return NwcSendOutcome.Fail(FailKindCancelled, $"Cancelled before WebSocket connection to {_config.RelayUrl} completed");
        }
        catch (Exception ex)
        {
            return NwcSendOutcome.Fail(FailKindConnect, $"WebSocket connection to {_config.RelayUrl} failed: {ex.Message}");
        }

        // Resolve the effective encryption scheme up front so both the send path and
        // the no-response error path can reference it (the latter quotes it back to
        // the user with a swap hint). When config is "auto" (the default) we fetch
        // the wallet's NIP-47 INFO event once and cache the choice. Explicit
        // "nip04"/"nip44_v2" skip the fetch entirely.
        string effectiveEncryption;
        try
        {
            effectiveEncryption = _config.Encryption == NwcEncryption.Auto
                ? await ResolveAutoEncryptionAsync(cancellationToken).ConfigureAwait(false)
                : _config.Encryption;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NwcSendOutcome.Fail(FailKindCancelled, "Cancelled while resolving wallet's NIP-47 capabilities");
        }

        try
        {
            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var requestJson = request.ToJsonString();

            // Inbound auto-detects, so the effectiveEncryption above only affects outbound.
            var walletPubkeyBytes = Convert.FromHexString(_config.WalletPubkey);
            string content;
            JsonArray tags;
            if (effectiveEncryption == NwcEncryption.Nip44V2)
            {
                content = EncryptNip44(requestJson, walletPubkeyBytes, _privateKey);
                tags = new JsonArray { new JsonArray { "p", _config.WalletPubkey }, new JsonArray { "encryption", "nip44_v2" } };
            }
            else
            {
                content = EncryptNip04(requestJson, walletPubkeyBytes, _privateKey);
                // No "encryption" tag for NIP-04 — that's the original NIP-47 default.
                tags = new JsonArray { new JsonArray { "p", _config.WalletPubkey } };
            }

            // Compute proper event ID as SHA256 of serialized event data
            var eventId = ComputeEventId(_myPubkeyHex, createdAt, 23194, tags, content);

            // Sign with BIP340 Schnorr signature
            var eventIdBytes = Convert.FromHexString(eventId);
            _privateKey.TrySignBIP340(eventIdBytes, null, out var sig);
            var sigHex = sig != null ? Convert.ToHexString(sig.ToBytes()).ToLowerInvariant() : "";

            var nostrEvent = new JsonObject
            {
                ["id"] = eventId,
                ["pubkey"] = _myPubkeyHex,
                ["created_at"] = createdAt,
                ["kind"] = 23194, // NIP-47 request
                ["tags"] = tags,
                ["content"] = content,
                ["sig"] = sigHex
            };

            // Send REQ FIRST to subscribe before publishing the payment event.
            // This ensures our subscription is active before the wallet processes
            // the payment, so the response arrives as a real-time event (after EOSE).
            var subId = Guid.NewGuid().ToString("N")[..8];
            var reqMessage = new JsonArray
            {
                "REQ",
                subId,
                new JsonObject
                {
                    ["kinds"] = new JsonArray { 23195 }, // NIP-47 response
                    ["authors"] = new JsonArray { _config.WalletPubkey },
                    ["#p"] = new JsonArray { _myPubkeyHex },
                    ["#e"] = new JsonArray { eventId },
                    ["since"] = createdAt
                }
            };
            var reqBytes = Encoding.UTF8.GetBytes(reqMessage.ToJsonString(NostrJsonOptions));
            await ws.SendAsync(reqBytes, WebSocketMessageType.Text, true, cancellationToken);
            Console.Error.WriteLine($"[NWC] Sent REQ, subId: {subId}");

            // Now send EVENT (payment request) after subscription is active
            var eventMessage = new JsonArray { "EVENT", nostrEvent };
            var messageBytes = Encoding.UTF8.GetBytes(eventMessage.ToJsonString(NostrJsonOptions));
            await ws.SendAsync(messageBytes, WebSocketMessageType.Text, true, cancellationToken);
            Console.Error.WriteLine($"[NWC] Sent EVENT, id: {eventId}");

            // Wait for response. Stale cached responses from previous payments
            // are filtered by preimage deduplication (layer 3 defense below).
            var buffer = new byte[8192];
            var responseBuilder = new StringBuilder();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, linked.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                responseBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    var message = responseBuilder.ToString();
                    responseBuilder.Clear();
                    Console.Error.WriteLine($"[NWC] Received: {message[..Math.Min(200, message.Length)]}...");

                    // Parse Nostr message
                    var parsed = JsonNode.Parse(message)?.AsArray();
                    if (parsed == null || parsed.Count < 2)
                        continue;

                    var msgType = parsed[0]?.GetValue<string>();
                    Console.Error.WriteLine($"[NWC] Message type: {msgType}");

                    if (msgType == "OK" && parsed.Count >= 3)
                    {
                        var accepted = parsed[2]?.GetValue<bool>() ?? false;
                        Console.Error.WriteLine($"[NWC] Event accepted: {accepted}");
                        if (!accepted && parsed.Count >= 4)
                        {
                            var reason = parsed[3]?.GetValue<string>();
                            Console.Error.WriteLine($"[NWC] Rejection reason: {reason}");
                        }
                    }
                    else if (msgType == "EVENT" && parsed.Count >= 3)
                    {
                        var responseEvent = parsed[2]?.AsObject();
                        if (responseEvent != null)
                        {
                            var kind = responseEvent["kind"]?.GetValue<int>();
                            Console.Error.WriteLine($"[NWC] Event kind: {kind}");
                            if (kind == 23195) // NIP-47 response
                            {
                                // Verify this response is for our specific request via e tag
                                var responseTags = responseEvent["tags"]?.AsArray();
                                var responseETag = responseTags?
                                    .Where(t => t?.AsArray()?.Count >= 2 && t![0]?.GetValue<string>() == "e")
                                    .Select(t => t![1]?.GetValue<string>())
                                    .FirstOrDefault();

                                if (responseETag != null && responseETag != eventId)
                                {
                                    Console.Error.WriteLine($"[NWC] Ignoring response for different request (e={responseETag[..16]}..., expected={eventId[..16]}...)");
                                    continue;
                                }

                                var encryptedContent = responseEvent["content"]?.GetValue<string>();
                                if (!string.IsNullOrEmpty(encryptedContent))
                                {
                                    Console.Error.WriteLine($"[NWC] Decrypting response...");
                                    var senderPubkeyHex = responseEvent["pubkey"]?.GetValue<string>() ?? _config.WalletPubkey;
                                    var senderPubkeyBytes = Convert.FromHexString(senderPubkeyHex);
                                    var decrypted = DecryptContent(encryptedContent, senderPubkeyBytes, _privateKey);
                                    Console.Error.WriteLine($"[NWC] Decrypted: {decrypted}");

                                    var responseObj = JsonNode.Parse(decrypted)?.AsObject();
                                    var resultType = responseObj?["result_type"]?.GetValue<string>();
                                    Console.Error.WriteLine($"[NWC] Response result_type: {resultType}, expected: {expectedResultType}");

                                    // Only return if result_type matches what we're waiting for
                                    // (ignore cached responses from previous requests)
                                    if (resultType == expectedResultType)
                                    {
                                        // For pay_invoice responses, verify preimage matches payment hash.
                                        // SHA256(preimage) must equal the invoice's payment hash.
                                        // This catches stale relay responses from previous payments.
                                        if (resultType == "pay_invoice" && expectedPaymentHash != null)
                                        {
                                            var preimageVal = responseObj?["result"]?["preimage"]?.GetValue<string>();
                                            if (!string.IsNullOrEmpty(preimageVal) && preimageVal.Length == 64)
                                            {
                                                var preimageBytes = Convert.FromHexString(preimageVal);
                                                var computedHash = System.Security.Cryptography.SHA256.HashData(preimageBytes);
                                                if (!computedHash.SequenceEqual(expectedPaymentHash))
                                                {
                                                    var expected = Convert.ToHexString(expectedPaymentHash).ToLowerInvariant();
                                                    var got = Convert.ToHexString(computedHash).ToLowerInvariant();
                                                    Console.Error.WriteLine($"[NWC] Preimage mismatch! SHA256(preimage)={got[..16]}... expected={expected[..16]}... — stale relay response, continuing...");
                                                    continue;
                                                }
                                                Console.Error.WriteLine("[NWC] Preimage verified: SHA256(preimage) matches payment hash");
                                            }
                                        }
                                        return NwcSendOutcome.Ok(responseObj!);
                                    }
                                    else
                                    {
                                        Console.Error.WriteLine($"[NWC] Ignoring cached response with wrong result_type, continuing to wait...");
                                        continue;
                                    }
                                }
                            }
                        }
                    }
                    else if (msgType == "EOSE")
                    {
                        Console.Error.WriteLine("[NWC] End of stored events, waiting for real-time events...");
                        continue;
                    }
                    else if (msgType == "NOTICE")
                    {
                        var notice = parsed.Count > 1 ? parsed[1]?.GetValue<string>() : null;
                        Console.Error.WriteLine($"[NWC] NOTICE: {notice}");
                    }
                }
            }

            Console.Error.WriteLine("[NWC] Loop ended without response");
            // Connect succeeded but the wallet never sent a matching reply within 30s.
            // Most common cause is encryption mismatch — Primal/CoinOS silently drop
            // events tagged "encryption=nip44_v2"; Alby Hub silently drops NIP-04.
            // We quote effectiveEncryption (the actually-used scheme, post-auto-resolve)
            // so the hint points at the *real* mismatch direction.
            var altScheme = effectiveEncryption == NwcEncryption.Nip44V2 ? NwcEncryption.Nip04 : NwcEncryption.Nip44V2;
            return NwcSendOutcome.Fail(FailKindNoResponse,
                $"Wallet did not respond within 30s using {effectiveEncryption} encryption. " +
                $"Most common cause: encryption mismatch — try setting NWC_ENCRYPTION={altScheme} " +
                $"if your wallet (e.g. Alby Hub for nip44_v2; Primal/CoinOS for nip04) requires the other scheme.");
        }
        catch (OperationCanceledException)
        {
            // Both the caller's token and the 30s timeout token feed the linked CTS
            // used for ReceiveAsync. Distinguish so that a silent-encryption-mismatch
            // timeout (the common Primal/CoinOS/Alby case) reports as no_response
            // with the encryption-swap hint, and an actual caller cancellation reports
            // as cancelled. Callers can also discriminate on FailKind to decide
            // retry/abort behavior.
            if (cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine("[NWC] Caller cancellation observed");
                return NwcSendOutcome.Fail(FailKindCancelled,
                    $"Operation cancelled by caller after sending request (encryption={effectiveEncryption}).");
            }

            Console.Error.WriteLine("[NWC] 30s receive-loop timeout — no matching response from wallet");
            var altScheme = effectiveEncryption == NwcEncryption.Nip44V2 ? NwcEncryption.Nip04 : NwcEncryption.Nip44V2;
            return NwcSendOutcome.Fail(FailKindNoResponse,
                $"Wallet did not respond within 30s using {effectiveEncryption} encryption. " +
                $"Most common cause: encryption mismatch — try setting NWC_ENCRYPTION={altScheme} " +
                $"if your wallet (e.g. Alby Hub for nip44_v2; Primal/CoinOS for nip04) requires the other scheme.");
        }
        catch (Exception ex)
        {
            // Unknown bucket — protocol_error is reserved for confirmed parse/decrypt
            // issues, which the receive loop catches and `continue`s rather than
            // letting bubble. Anything that escapes to here (socket send failures,
            // unexpected errors, etc.) is genuinely unclassified.
            Console.Error.WriteLine($"[NWC] Exception: {ex.Message}");
            return NwcSendOutcome.Fail(FailKindUnknown,
                $"Unexpected error after WebSocket connect (encryption={effectiveEncryption}): {ex.Message}");
        }
        finally
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Extracts the payment hash from a BOLT11 Lightning invoice.
    /// Parses bech32 data to find the tagged field with type 1 (payment hash).
    /// </summary>
    private static byte[]? ExtractPaymentHashFromBolt11(string bolt11)
    {
        const string bech32Chars = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

        // Find the bech32 data separator '1' (last occurrence)
        var sepIndex = bolt11.ToLowerInvariant().LastIndexOf('1');
        if (sepIndex < 0) return null;

        var data = bolt11[(sepIndex + 1)..].ToLowerInvariant();
        // Need at least: 7 (timestamp) + 3 (tag header) + 52 (payment hash) + 104 (signature)
        if (data.Length < 166) return null;

        // Skip timestamp (7 bech32 chars = 35 bits)
        var pos = 7;

        // Parse tagged fields until we find payment hash (type 1)
        // Stop before signature (last 104 chars)
        while (pos + 3 <= data.Length - 104)
        {
            var type = bech32Chars.IndexOf(data[pos]);
            if (type < 0) return null;

            var len1 = bech32Chars.IndexOf(data[pos + 1]);
            var len2 = bech32Chars.IndexOf(data[pos + 2]);
            if (len1 < 0 || len2 < 0) return null;
            var dataLen = (len1 << 5) | len2; // length in 5-bit groups

            pos += 3; // skip type + 2 length chars

            if (type == 1 && dataLen == 52) // payment hash: type 1, exactly 256 bits
            {
                if (pos + dataLen > data.Length) return null;

                // Convert 52 bech32 chars (5-bit groups) to 32 bytes (8-bit)
                var acc = 0;
                var bits = 0;
                var result = new List<byte>();
                for (int i = 0; i < dataLen; i++)
                {
                    var val = bech32Chars.IndexOf(data[pos + i]);
                    if (val < 0) return null;
                    acc = (acc << 5) | val;
                    bits += 5;
                    while (bits >= 8)
                    {
                        bits -= 8;
                        result.Add((byte)((acc >> bits) & 0xff));
                    }
                }
                return result.Count == 32 ? result.ToArray() : null;
            }

            pos += dataLen;
        }

        return null;
    }

    /// <summary>
    /// Computes Nostr event ID as SHA256 of the serialized event array.
    /// Uses UnsafeRelaxedJsonEscaping to avoid escaping characters like + as \u002B.
    /// </summary>
    private static string ComputeEventId(string pubkey, long createdAt, int kind, JsonArray tags, string content)
    {
        var eventArray = new JsonArray { 0, pubkey, createdAt, kind, JsonNode.Parse(tags.ToJsonString(NostrJsonOptions)), content };
        var serialized = eventArray.ToJsonString(NostrJsonOptions);
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Verifies a Nostr event's BIP340 Schnorr signature against its claimed pubkey.
    /// Returns true only if the recomputed event id matches the event's <c>id</c> field
    /// AND the <c>sig</c> field is a valid BIP340 signature of that id under the
    /// claimed <c>pubkey</c>. Used by the INFO-event auto-detect path so a malicious
    /// relay can't forge an INFO event attributed to the wallet pubkey and force an
    /// encryption downgrade. Returns false on any malformed input.
    /// </summary>
    internal static bool VerifyNostrEventSignature(JsonObject ev)
    {
        try
        {
            var idHex = ev["id"]?.GetValue<string>();
            var pubkeyHex = ev["pubkey"]?.GetValue<string>();
            var sigHex = ev["sig"]?.GetValue<string>();
            var createdAt = ev["created_at"]?.GetValue<long>();
            var kind = ev["kind"]?.GetValue<int>();
            var tags = ev["tags"]?.AsArray();
            var content = ev["content"]?.GetValue<string>();

            if (idHex == null || pubkeyHex == null || sigHex == null
                || createdAt == null || kind == null || tags == null || content == null)
                return false;
            if (idHex.Length != 64 || pubkeyHex.Length != 64 || sigHex.Length != 128)
                return false;

            // Recompute the event id from the canonical serialisation. If the event
            // was tampered with (e.g. relay swapped the encryption tag), the recomputed
            // id won't match the claimed id.
            var recomputedId = ComputeEventId(pubkeyHex, createdAt.Value, kind.Value, tags, content);
            if (!string.Equals(recomputedId, idHex, StringComparison.OrdinalIgnoreCase))
                return false;

            var pubkeyBytes = Convert.FromHexString(pubkeyHex);
            var sigBytes = Convert.FromHexString(sigHex);
            var idBytes = Convert.FromHexString(idHex);

            if (!ECXOnlyPubKey.TryCreate(pubkeyBytes, out var pubkey) || pubkey == null)
                return false;
            if (!SecpSchnorrSignature.TryCreate(sigBytes, out var sig) || sig == null)
                return false;

            // BIP340 Schnorr verify. Returns false on signature mismatch.
            return pubkey.SigVerifyBIP340(sig, idBytes);
        }
        catch
        {
            // Defensive: any parsing/crypto exception → treat as unverified.
            return false;
        }
    }

    /// <summary>
    /// Encrypts content using NIP-04 (ECDH + AES-256-CBC).
    /// </summary>
    internal static string EncryptNip04(string plaintext, byte[] recipientPubkeyBytes, ECPrivKey senderPrivKey)
    {
        // Get recipient's public key as ECPubKey for ECDH
        if (!ECXOnlyPubKey.TryCreate(recipientPubkeyBytes, out var recipientXOnlyPubKey))
            throw new ArgumentException("Invalid recipient public key");

        // Convert x-only pubkey to full pubkey (assume even y-coordinate)
        var fullPubkeyBytes = new byte[33];
        fullPubkeyBytes[0] = 0x02; // Even y-coordinate
        recipientPubkeyBytes.CopyTo(fullPubkeyBytes, 1);

        if (!ECPubKey.TryCreate(fullPubkeyBytes, Context.Instance, out _, out var recipientPubKey))
            throw new ArgumentException("Failed to create ECPubKey");

        // Compute ECDH shared secret
        var sharedPoint = recipientPubKey.GetSharedPubkey(senderPrivKey);
        var sharedX = sharedPoint.ToBytes()[1..33]; // Take x-coordinate only

        // Encrypt with AES-256-CBC.
        // Per NIP-04 spec the AES key is sha256(shared_x), NOT raw shared_x.
        // The raw-shared_x form previously used here was internally consistent
        // (encrypt + decrypt agreed) but incompatible with every other NIP-04
        // implementation (Python port in this repo, Primal, CoinOS, Mutiny, etc.)
        // which would mean ciphertext we send under nip04 wasn't decryptable by
        // any real wallet. Fixed before flipping the default to nip04.
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var iv = new byte[16];
        RandomNumberGenerator.Fill(iv);

        using var aes = Aes.Create();
        aes.Key = System.Security.Cryptography.SHA256.HashData(sharedX);
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

        return Convert.ToBase64String(encrypted) + "?iv=" + Convert.ToBase64String(iv);
    }

    /// <summary>
    /// Encrypts content using NIP-44 v2 (ECDH + HKDF + ChaCha20 + HMAC-SHA256).
    /// </summary>
    internal static string EncryptNip44(string plaintext, byte[] recipientPubkeyBytes, ECPrivKey senderPrivKey)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        if (plaintextBytes.Length < 1 || plaintextBytes.Length > 65535)
            throw new ArgumentException($"Plaintext length {plaintextBytes.Length} out of range (1-65535)");

        // Compute ECDH shared x-coordinate
        if (!ECXOnlyPubKey.TryCreate(recipientPubkeyBytes, out _))
            throw new ArgumentException("Invalid recipient public key");

        var fullPubkeyBytes = new byte[33];
        fullPubkeyBytes[0] = 0x02;
        recipientPubkeyBytes.CopyTo(fullPubkeyBytes, 1);

        if (!ECPubKey.TryCreate(fullPubkeyBytes, Context.Instance, out _, out var recipientPubKey))
            throw new ArgumentException("Failed to create ECPubKey");

        var sharedPoint = recipientPubKey.GetSharedPubkey(senderPrivKey);
        var sharedX = sharedPoint.ToBytes()[1..33];

        // conversation_key = HKDF-Extract(salt="nip44-v2", ikm=sharedX)
        var salt = Encoding.UTF8.GetBytes("nip44-v2");
        var conversationKey = HKDF.Extract(HashAlgorithmName.SHA256, sharedX, salt);

        // Generate random 32-byte nonce
        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);

        // Derive message keys via HKDF-Expand
        var messageKeys = new byte[76];
        HKDF.Expand(HashAlgorithmName.SHA256, conversationKey, messageKeys, nonce);

        var chachaKey = messageKeys[0..32];
        var chachaNonce = messageKeys[32..44];
        var hmacKey = messageKeys[44..76];

        // Pad plaintext: 2-byte big-endian length + plaintext + zero padding
        var paddedLen = CalcPaddedLen(plaintextBytes.Length);
        var padded = new byte[2 + paddedLen];
        padded[0] = (byte)(plaintextBytes.Length >> 8);
        padded[1] = (byte)(plaintextBytes.Length & 0xFF);
        plaintextBytes.CopyTo(padded, 2);
        // Remaining bytes are already zero

        // Encrypt with ChaCha20 (stream cipher — encrypt and decrypt are the same XOR)
        var ciphertext = ChaCha20Decrypt(padded, chachaKey, chachaNonce);

        // Compute HMAC over nonce + ciphertext
        var hmacInput = new byte[nonce.Length + ciphertext.Length];
        nonce.CopyTo(hmacInput, 0);
        ciphertext.CopyTo(hmacInput, nonce.Length);
        using var hmac = new System.Security.Cryptography.HMACSHA256(hmacKey);
        var mac = hmac.ComputeHash(hmacInput);

        // Assemble: version(0x02) + nonce + ciphertext + mac
        var payload = new byte[1 + nonce.Length + ciphertext.Length + mac.Length];
        payload[0] = 0x02;
        nonce.CopyTo(payload, 1);
        ciphertext.CopyTo(payload, 33);
        mac.CopyTo(payload, 33 + ciphertext.Length);

        return Convert.ToBase64String(payload);
    }

    /// <summary>
    /// Calculates NIP-44 padded length for plaintext.
    /// </summary>
    internal static int CalcPaddedLen(int unpaddedLen)
    {
        if (unpaddedLen <= 0) throw new ArgumentException("Length must be > 0");
        if (unpaddedLen <= 32) return 32;

        // Next power of 2
        var nextPower = 1;
        var temp = unpaddedLen - 1;
        while (temp > 0) { nextPower <<= 1; temp >>= 1; }

        var chunk = Math.Max(32, nextPower >> 3);
        return chunk * ((unpaddedLen + chunk - 1) / chunk);
    }

    /// <summary>
    /// Detects NIP-04 vs NIP-44 format and dispatches to the correct decryption method.
    /// NIP-04: base64(ciphertext)?iv=base64(iv)
    /// NIP-44: single base64 blob (version_byte + nonce + ciphertext + mac)
    /// </summary>
    internal static string DecryptContent(string encryptedContent, byte[] senderPubkeyBytes, ECPrivKey recipientPrivKey)
    {
        if (encryptedContent.Contains("?iv="))
        {
            return DecryptNip04(encryptedContent, senderPubkeyBytes, recipientPrivKey);
        }
        else
        {
            return DecryptNip44(encryptedContent, senderPubkeyBytes, recipientPrivKey);
        }
    }

    /// <summary>
    /// Decrypts content using NIP-04 (ECDH + AES-256-CBC).
    /// </summary>
    private static string DecryptNip04(string ciphertext, byte[] senderPubkeyBytes, ECPrivKey recipientPrivKey)
    {
        var parts = ciphertext.Split("?iv=");
        if (parts.Length != 2)
            throw new InvalidOperationException("Invalid NIP-04 ciphertext format");

        var encryptedBytes = Convert.FromBase64String(parts[0]);
        var iv = Convert.FromBase64String(parts[1]);

        // Get sender's public key as ECPubKey for ECDH
        if (!ECXOnlyPubKey.TryCreate(senderPubkeyBytes, out var senderXOnlyPubKey))
            throw new ArgumentException("Invalid sender public key");

        // Convert x-only pubkey to full pubkey (assume even y-coordinate)
        var fullPubkeyBytes = new byte[33];
        fullPubkeyBytes[0] = 0x02; // Even y-coordinate
        senderPubkeyBytes.CopyTo(fullPubkeyBytes, 1);

        if (!ECPubKey.TryCreate(fullPubkeyBytes, Context.Instance, out _, out var senderPubKey))
            throw new ArgumentException("Failed to create ECPubKey");

        // Compute ECDH shared secret
        var sharedPoint = senderPubKey.GetSharedPubkey(recipientPrivKey);
        var sharedX = sharedPoint.ToBytes()[1..33]; // Take x-coordinate only

        // Decrypt with AES-256-CBC. NIP-04 derives the key as sha256(shared_x);
        // see EncryptNip04 for context on why the previous raw-shared_x form was
        // wrong and why this is symmetric with the encrypt side.
        using var aes = Aes.Create();
        aes.Key = System.Security.Cryptography.SHA256.HashData(sharedX);
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);

        return Encoding.UTF8.GetString(decrypted);
    }

    /// <summary>
    /// Decrypts content using NIP-44 v2 (ECDH + HKDF + ChaCha20 + HMAC-SHA256).
    /// Format: base64(version_byte(1) + nonce(32) + ciphertext(N) + mac(32))
    /// </summary>
    internal static string DecryptNip44(string content, byte[] senderPubkeyBytes, ECPrivKey recipientPrivKey)
    {
        var data = Convert.FromBase64String(content);
        if (data.Length < 99) // 1 (version) + 32 (nonce) + 32 (min ciphertext with 2-byte length + 1 byte padded to 32) + 32 (mac) + 2 (length prefix)
            throw new InvalidOperationException("NIP-44 ciphertext too short");

        // 1. Check version byte
        if (data[0] != 0x02)
            throw new InvalidOperationException($"Unsupported NIP-44 version: {data[0]}");

        // 2. Extract components
        var nonce = data[1..33];
        var ciphertext = data[33..^32];
        var mac = data[^32..];

        // 3. Compute ECDH shared secret (same as NIP-04)
        if (!ECXOnlyPubKey.TryCreate(senderPubkeyBytes, out _))
            throw new ArgumentException("Invalid sender public key");

        var fullPubkeyBytes = new byte[33];
        fullPubkeyBytes[0] = 0x02; // Even y-coordinate
        senderPubkeyBytes.CopyTo(fullPubkeyBytes, 1);

        if (!ECPubKey.TryCreate(fullPubkeyBytes, Context.Instance, out _, out var senderPubKey))
            throw new ArgumentException("Failed to create ECPubKey");

        var sharedPoint = senderPubKey.GetSharedPubkey(recipientPrivKey);
        var sharedX = sharedPoint.ToBytes()[1..33]; // x-coordinate only

        // 4. Derive conversation_key via HKDF-extract
        var salt = Encoding.UTF8.GetBytes("nip44-v2");
        var conversationKey = HKDF.Extract(HashAlgorithmName.SHA256, sharedX, salt);

        // 5. Derive message keys via HKDF-expand
        var messageKeys = new byte[76];
        HKDF.Expand(HashAlgorithmName.SHA256, conversationKey, messageKeys, nonce);

        var chachaKey = messageKeys[0..32];
        var chachaNonce = messageKeys[32..44]; // 12 bytes
        var hmacKey = messageKeys[44..76];

        // 6. Verify HMAC over nonce + ciphertext
        using var hmac = new System.Security.Cryptography.HMACSHA256(hmacKey);
        var hmacInput = new byte[nonce.Length + ciphertext.Length];
        nonce.CopyTo(hmacInput, 0);
        ciphertext.CopyTo(hmacInput, nonce.Length);
        var computedMac = hmac.ComputeHash(hmacInput);

        if (!CryptographicOperations.FixedTimeEquals(computedMac, mac))
            throw new InvalidOperationException("NIP-44 HMAC verification failed");

        // 7. Decrypt with ChaCha20
        var decrypted = ChaCha20Decrypt(ciphertext, chachaKey, chachaNonce);

        // 8. Extract plaintext: first 2 bytes are big-endian length
        if (decrypted.Length < 2)
            throw new InvalidOperationException("NIP-44 decrypted data too short");

        var plaintextLength = (decrypted[0] << 8) | decrypted[1];
        if (plaintextLength <= 0 || 2 + plaintextLength > decrypted.Length)
            throw new InvalidOperationException($"NIP-44 invalid plaintext length: {plaintextLength}");

        return Encoding.UTF8.GetString(decrypted, 2, plaintextLength);
    }

    /// <summary>
    /// ChaCha20 stream cipher (IETF variant, RFC 8439).
    /// Raw stream cipher (NOT AEAD). 32-byte key, 12-byte nonce, counter starts at 0.
    /// </summary>
    internal static byte[] ChaCha20Decrypt(byte[] ciphertext, byte[] key, byte[] nonce)
    {
        if (key.Length != 32) throw new ArgumentException("Key must be 32 bytes", nameof(key));
        if (nonce.Length != 12) throw new ArgumentException("Nonce must be 12 bytes", nameof(nonce));

        var output = new byte[ciphertext.Length];
        var state = new uint[16];
        var keyStream = new byte[64];
        uint counter = 0;

        // Parse key as 8 little-endian uint32s
        var keyWords = new uint[8];
        for (int i = 0; i < 8; i++)
            keyWords[i] = BitConverter.ToUInt32(key, i * 4);

        // Parse nonce as 3 little-endian uint32s
        var nonceWords = new uint[3];
        for (int i = 0; i < 3; i++)
            nonceWords[i] = BitConverter.ToUInt32(nonce, i * 4);

        for (int offset = 0; offset < ciphertext.Length; offset += 64)
        {
            // Initialize state
            // "expand 32-byte k" constants
            state[0] = 0x61707865;
            state[1] = 0x3320646e;
            state[2] = 0x79622d32;
            state[3] = 0x6b206574;

            // Key
            for (int i = 0; i < 8; i++)
                state[4 + i] = keyWords[i];

            // Counter + nonce
            state[12] = counter;
            state[13] = nonceWords[0];
            state[14] = nonceWords[1];
            state[15] = nonceWords[2];

            // Copy initial state for addition after rounds
            var initialState = (uint[])state.Clone();

            // 20 rounds (10 double rounds)
            for (int i = 0; i < 10; i++)
            {
                // Column rounds
                QuarterRound(state, 0, 4, 8, 12);
                QuarterRound(state, 1, 5, 9, 13);
                QuarterRound(state, 2, 6, 10, 14);
                QuarterRound(state, 3, 7, 11, 15);

                // Diagonal rounds
                QuarterRound(state, 0, 5, 10, 15);
                QuarterRound(state, 1, 6, 11, 12);
                QuarterRound(state, 2, 7, 8, 13);
                QuarterRound(state, 3, 4, 9, 14);
            }

            // Add initial state
            for (int i = 0; i < 16; i++)
                state[i] += initialState[i];

            // Serialize state to keystream bytes (little-endian)
            for (int i = 0; i < 16; i++)
            {
                keyStream[i * 4] = (byte)(state[i]);
                keyStream[i * 4 + 1] = (byte)(state[i] >> 8);
                keyStream[i * 4 + 2] = (byte)(state[i] >> 16);
                keyStream[i * 4 + 3] = (byte)(state[i] >> 24);
            }

            // XOR with ciphertext
            var blockLen = Math.Min(64, ciphertext.Length - offset);
            for (int i = 0; i < blockLen; i++)
                output[offset + i] = (byte)(ciphertext[offset + i] ^ keyStream[i]);

            counter++;
        }

        return output;
    }

    private static uint RotateLeft(uint v, int n) => (v << n) | (v >> (32 - n));

    private static void QuarterRound(uint[] s, int a, int b, int c, int d)
    {
        s[a] += s[b]; s[d] ^= s[a]; s[d] = RotateLeft(s[d], 16);
        s[c] += s[d]; s[b] ^= s[c]; s[b] = RotateLeft(s[b], 12);
        s[a] += s[b]; s[d] ^= s[a]; s[d] = RotateLeft(s[d], 8);
        s[c] += s[d]; s[b] ^= s[c]; s[b] = RotateLeft(s[b], 7);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            // SemaphoreSlim is IDisposable — its underlying wait handle leaks if
            // we don't release it explicitly. Matters for long-running processes
            // that recreate the wallet service (e.g. config reload).
            // Direct call (not null-conditional) since the field is
            // non-nullable and eagerly initialised at construction.
            _autoResolveLock.Dispose();
            _disposed = true;
        }
    }
}

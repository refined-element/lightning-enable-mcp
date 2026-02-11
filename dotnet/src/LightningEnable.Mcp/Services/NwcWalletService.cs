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

    private readonly NwcConfig? _config;
    private readonly ECPrivKey? _privateKey;
    private readonly ECXOnlyPubKey? _publicKey;
    private readonly string? _myPubkeyHex;
    private readonly HttpClient _httpClient;
    private bool _disposed;

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
        // Debug log to file for Mac debugging
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".lightning-enable",
            "nwc-debug.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        void DebugLog(string msg)
        {
            var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] {msg}";
            Console.Error.WriteLine(line);
            try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
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

            DebugLog("Sending NWC request...");
            var response = await SendNwcRequestAsync(request, cancellationToken);

            if (response == null)
            {
                DebugLog("ERROR: No response from NWC");
                return NwcPaymentResult.Failed("CONNECTION_ERROR", "Failed to connect to NWC relay");
            }

            DebugLog($"Got response: {response.ToJsonString()}");

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

            DebugLog($"Raw preimage from response: '{preimage}'");
            DebugLog($"Preimage length: {preimage?.Length ?? 0}");

            if (string.IsNullOrEmpty(preimage))
            {
                DebugLog("ERROR: No preimage in response!");
                DebugLog($"Full result object: {result?.ToJsonString()}");
                return NwcPaymentResult.Failed("NO_PREIMAGE", "Payment succeeded but no preimage returned");
            }

            // Validate preimage format (should be 64 hex chars = 32 bytes)
            var isValidHex = preimage.Length == 64 && preimage.All(c => "0123456789abcdefABCDEF".Contains(c));
            DebugLog($"Is valid 64-char hex: {isValidHex}");

            if (!isValidHex)
            {
                DebugLog($"WARNING: Preimage is not 64 hex chars! Got: {preimage}");
                // Check if it looks like a UUID
                if (preimage.Contains('-') && preimage.Length == 36)
                {
                    DebugLog("DETECTED: Preimage looks like a UUID - Coinos internal transfer bug");
                }
            }

            DebugLog($"Returning preimage: {preimage}");
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

        var response = await SendNwcRequestAsync(request, cancellationToken);

        if (response == null)
        {
            throw new InvalidOperationException($"Failed to connect to NWC relay: {_config.RelayUrl}");
        }

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

            var response = await SendNwcRequestAsync(request, cancellationToken);

            if (response == null)
            {
                return WalletInvoiceResult.Failed("CONNECTION_ERROR", "Failed to connect to NWC relay");
            }

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

    private async Task<JsonObject?> SendNwcRequestAsync(JsonObject request, CancellationToken cancellationToken)
    {
        if (_config == null || _privateKey == null || _publicKey == null || _myPubkeyHex == null)
            return null;

        // Extract the method from the request so we can filter responses by result_type
        var expectedResultType = request["method"]?.GetValue<string>();
        Console.Error.WriteLine($"[NWC] Sending request, method: {expectedResultType}");

        using var ws = new ClientWebSocket();

        try
        {
            // Connect to relay
            var relayUri = new Uri(_config.RelayUrl);
            Console.Error.WriteLine($"[NWC] Connecting to: {relayUri}");
            await ws.ConnectAsync(relayUri, cancellationToken);
            Console.Error.WriteLine($"[NWC] Connected, state: {ws.State}");

            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var requestJson = request.ToJsonString();
            var tags = new JsonArray { new JsonArray { "p", _config.WalletPubkey } };

            // Encrypt using NIP-04 with proper ECDH
            var walletPubkeyBytes = Convert.FromHexString(_config.WalletPubkey);
            var content = EncryptNip04(requestJson, walletPubkeyBytes, _privateKey);

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

            // Send EVENT message (use NostrJsonOptions to avoid escaping + as \u002B)
            var eventMessage = new JsonArray { "EVENT", nostrEvent };
            var messageBytes = Encoding.UTF8.GetBytes(eventMessage.ToJsonString(NostrJsonOptions));
            await ws.SendAsync(messageBytes, WebSocketMessageType.Text, true, cancellationToken);
            Console.Error.WriteLine($"[NWC] Sent EVENT, id: {eventId}");

            // Also send REQ to listen for response
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
                    ["since"] = createdAt - 10
                }
            };
            var reqBytes = Encoding.UTF8.GetBytes(reqMessage.ToJsonString(NostrJsonOptions));
            await ws.SendAsync(reqBytes, WebSocketMessageType.Text, true, cancellationToken);
            Console.Error.WriteLine($"[NWC] Sent REQ, subId: {subId}");

            // Wait for response
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
                                var encryptedContent = responseEvent["content"]?.GetValue<string>();
                                if (!string.IsNullOrEmpty(encryptedContent))
                                {
                                    Console.Error.WriteLine($"[NWC] Decrypting response...");
                                    var senderPubkeyHex = responseEvent["pubkey"]?.GetValue<string>() ?? _config.WalletPubkey;
                                    var senderPubkeyBytes = Convert.FromHexString(senderPubkeyHex);
                                    var decrypted = DecryptNip04(encryptedContent, senderPubkeyBytes, _privateKey);
                                    Console.Error.WriteLine($"[NWC] Decrypted: {decrypted}");

                                    var responseObj = JsonNode.Parse(decrypted)?.AsObject();
                                    var resultType = responseObj?["result_type"]?.GetValue<string>();
                                    Console.Error.WriteLine($"[NWC] Response result_type: {resultType}, expected: {expectedResultType}");

                                    // Only return if result_type matches what we're waiting for
                                    // (ignore cached responses from previous requests)
                                    if (resultType == expectedResultType)
                                    {
                                        return responseObj;
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
                        Console.Error.WriteLine("[NWC] End of stored events, waiting...");
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
            return null;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[NWC] Operation cancelled/timeout");
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NWC] Exception: {ex.Message}");
            return null;
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
    /// Encrypts content using NIP-04 (ECDH + AES-256-CBC).
    /// </summary>
    private static string EncryptNip04(string plaintext, byte[] recipientPubkeyBytes, ECPrivKey senderPrivKey)
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

        // Encrypt with AES-256-CBC
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var iv = new byte[16];
        RandomNumberGenerator.Fill(iv);

        using var aes = Aes.Create();
        aes.Key = sharedX; // Use raw x-coordinate as key (NIP-04 spec)
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

        return Convert.ToBase64String(encrypted) + "?iv=" + Convert.ToBase64String(iv);
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

        // Decrypt with AES-256-CBC
        using var aes = Aes.Create();
        aes.Key = sharedX; // Use raw x-coordinate as key (NIP-04 spec)
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);

        return Encoding.UTF8.GetString(decrypted);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}

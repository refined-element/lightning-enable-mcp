using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Wallet service using LND's REST API.
/// Connects directly to user's own Lightning node - ALWAYS returns preimage.
///
/// This is the recommended wallet for L402 because:
/// 1. User controls their own node (non-custodial)
/// 2. LND always returns preimage for payments
/// 3. No third-party custody = no money transmission concerns
///
/// Configuration (environment variables or config file):
/// - LND_REST_HOST: LND REST API host (e.g., "localhost:8080" or "127.0.0.1:8080")
/// - LND_MACAROON_HEX: Admin macaroon in hex format (required for payments)
/// - LND_TLS_CERT_PATH: Path to tls.cert file (optional, for self-signed certs)
/// - LND_SKIP_TLS_VERIFY: Set to "true" to skip TLS verification (dev only)
///
/// To get your macaroon in hex format:
/// - Linux/Mac: xxd -ps -c 1000 ~/.lnd/data/chain/bitcoin/mainnet/admin.macaroon
/// - Windows PowerShell: [System.BitConverter]::ToString([System.IO.File]::ReadAllBytes("$env:USERPROFILE\AppData\Local\Lnd\data\chain\bitcoin\mainnet\admin.macaroon")) -replace '-',''
/// </summary>
public class LndWalletService : IWalletService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string? _restHost;
    private readonly string? _macaroonHex;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public LndWalletService(HttpClient httpClient, IBudgetConfigurationService? budgetConfigService = null)
    {
        _httpClient = httpClient;

        // Try environment variables first, then config file
        _restHost = Environment.GetEnvironmentVariable("LND_REST_HOST");
        _macaroonHex = Environment.GetEnvironmentVariable("LND_MACAROON_HEX");

        if (string.IsNullOrEmpty(_restHost) || _restHost.StartsWith("${"))
        {
            _restHost = budgetConfigService?.Configuration?.Wallets?.LndRestHost;
        }
        if (string.IsNullOrEmpty(_macaroonHex) || _macaroonHex.StartsWith("${"))
        {
            _macaroonHex = budgetConfigService?.Configuration?.Wallets?.LndMacaroonHex;
        }

        if (IsConfigured)
        {
            // Configure base address
            var scheme = _restHost!.StartsWith("https://") || _restHost.StartsWith("http://")
                ? ""
                : "https://";
            _httpClient.BaseAddress = new Uri($"{scheme}{_restHost}/v1/");

            // Add macaroon header
            _httpClient.DefaultRequestHeaders.Add("Grpc-Metadata-macaroon", _macaroonHex);

            Console.Error.WriteLine($"[LND] Initialized REST client for {_restHost}");
        }
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_restHost) && !string.IsNullOrEmpty(_macaroonHex);

    public string ProviderName => "LND";

    public NwcConfig? GetConfig()
    {
        if (!IsConfigured)
            return null;

        return new NwcConfig
        {
            WalletPubkey = "lnd-local",
            RelayUrl = _restHost ?? "",
            Secret = "lnd"
        };
    }

    /// <summary>
    /// Pays a BOLT11 Lightning invoice using LND.
    /// LND ALWAYS returns the preimage - this is why it's ideal for L402.
    /// </summary>
    public async Task<NwcPaymentResult> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return NwcPaymentResult.Failed("NOT_CONFIGURED",
                "LND not configured. Set LND_REST_HOST and LND_MACAROON_HEX environment variables.");
        }

        try
        {
            Console.Error.WriteLine($"[LND] Paying invoice: {bolt11[..Math.Min(30, bolt11.Length)]}...");

            var request = new { payment_request = bolt11 };
            var response = await _httpClient.PostAsJsonAsync("channels/transactions", request, JsonOptions, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.Error.WriteLine($"[LND] Payment failed: {errorBody}");
                return NwcPaymentResult.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    $"LND payment failed: {errorBody}");
            }

            var result = await response.Content.ReadFromJsonAsync<LndPaymentResponse>(JsonOptions, cancellationToken);

            if (result == null)
            {
                return NwcPaymentResult.Failed("INVALID_RESPONSE", "Empty response from LND");
            }

            if (!string.IsNullOrEmpty(result.PaymentError))
            {
                Console.Error.WriteLine($"[LND] Payment error: {result.PaymentError}");
                return NwcPaymentResult.Failed("PAYMENT_ERROR", result.PaymentError);
            }

            // LND returns preimage as base64 - convert to hex
            if (!string.IsNullOrEmpty(result.PaymentPreimage))
            {
                var preimageBytes = Convert.FromBase64String(result.PaymentPreimage);
                var preimageHex = Convert.ToHexString(preimageBytes).ToLowerInvariant();
                Console.Error.WriteLine($"[LND] Payment succeeded! Preimage: {preimageHex[..Math.Min(16, preimageHex.Length)]}...");
                return NwcPaymentResult.Succeeded(preimageHex);
            }

            return NwcPaymentResult.Failed("NO_PREIMAGE", "Payment succeeded but no preimage returned");
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"[LND] HTTP error: {ex.Message}");
            return NwcPaymentResult.Failed("HTTP_ERROR", $"Failed to connect to LND: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LND] Exception: {ex.Message}");
            return NwcPaymentResult.Failed("EXCEPTION", ex.Message);
        }
    }

    public async Task<NwcBalanceInfo> GetBalanceAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("LND not configured");
        }

        try
        {
            var response = await _httpClient.GetAsync("balance/channels", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Failed to get balance: {response.StatusCode}");
            }

            var result = await response.Content.ReadFromJsonAsync<LndChannelBalance>(JsonOptions, cancellationToken);

            // local_balance.sat is spendable Lightning balance
            var balanceSats = result?.LocalBalance?.Sat ?? 0;
            Console.Error.WriteLine($"[LND] Balance: {balanceSats} sats");

            return new NwcBalanceInfo { BalanceMsat = balanceSats * 1000 };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to get LND balance: {ex.Message}", ex);
        }
    }

    public async Task<WalletInvoiceResult> CreateInvoiceAsync(
        long amountSats,
        string? memo = null,
        int expirySecs = 3600,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return WalletInvoiceResult.Failed("NOT_CONFIGURED",
                "LND not configured. Set LND_REST_HOST and LND_MACAROON_HEX environment variables.");
        }

        try
        {
            Console.Error.WriteLine($"[LND] Creating invoice for {amountSats} sats...");

            var request = new
            {
                value = amountSats,
                memo = memo ?? "Lightning payment",
                expiry = expirySecs
            };

            var response = await _httpClient.PostAsJsonAsync("invoices", request, JsonOptions, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                return WalletInvoiceResult.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    $"Failed to create invoice: {errorBody}");
            }

            var result = await response.Content.ReadFromJsonAsync<LndInvoiceResponse>(JsonOptions, cancellationToken);

            if (result == null || string.IsNullOrEmpty(result.PaymentRequest))
            {
                return WalletInvoiceResult.Failed("INVALID_RESPONSE", "No invoice returned");
            }

            // Convert r_hash from base64 to hex for invoice ID
            var invoiceId = !string.IsNullOrEmpty(result.RHash)
                ? Convert.ToHexString(Convert.FromBase64String(result.RHash)).ToLowerInvariant()
                : "";

            Console.Error.WriteLine($"[LND] Invoice created: {invoiceId[..Math.Min(16, invoiceId.Length)]}...");

            return WalletInvoiceResult.Succeeded(
                invoiceId,
                result.PaymentRequest,
                amountSats,
                DateTime.UtcNow.AddSeconds(expirySecs));
        }
        catch (HttpRequestException ex)
        {
            return WalletInvoiceResult.Failed("HTTP_ERROR", ex.Message);
        }
        catch (Exception ex)
        {
            return WalletInvoiceResult.Failed("EXCEPTION", ex.Message);
        }
    }

    public async Task<WalletInvoiceStatus> GetInvoiceStatusAsync(string invoiceId, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return WalletInvoiceStatus.Failed("NOT_CONFIGURED", "LND not configured");
        }

        try
        {
            // LND uses r_hash (payment hash) to lookup invoices
            var response = await _httpClient.GetAsync($"invoice/{invoiceId}", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return WalletInvoiceStatus.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    "Failed to get invoice status");
            }

            var result = await response.Content.ReadFromJsonAsync<LndInvoiceLookup>(JsonOptions, cancellationToken);

            if (result == null)
            {
                return WalletInvoiceStatus.Failed("INVALID_RESPONSE", "Empty response");
            }

            var state = result.State?.ToUpperInvariant() switch
            {
                "OPEN" => "PENDING",
                "SETTLED" => "PAID",
                "CANCELED" => "CANCELLED",
                "ACCEPTED" => "PENDING",
                _ => result.State ?? "UNKNOWN"
            };

            return WalletInvoiceStatus.Succeeded(
                invoiceId,
                state,
                long.TryParse(result.Value, out var amt) ? amt : 0,
                result.SettleDate != null && long.TryParse(result.SettleDate, out var settleTs) && settleTs > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(settleTs).UtcDateTime
                    : null);
        }
        catch (Exception ex)
        {
            return WalletInvoiceStatus.Failed("EXCEPTION", ex.Message);
        }
    }

    /// <summary>
    /// Gets BTC price - not directly supported by LND.
    /// </summary>
    public Task<WalletTickerResult> GetTickerAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(WalletTickerResult.NotSupported());
    }

    /// <summary>
    /// Sends an on-chain Bitcoin payment using LND.
    /// </summary>
    public async Task<OnChainPaymentResult> SendOnChainAsync(
        string address,
        long amountSats,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return OnChainPaymentResult.Failed("NOT_CONFIGURED", "LND not configured");
        }

        try
        {
            Console.Error.WriteLine($"[LND] Sending {amountSats} sats on-chain to {address}...");

            var request = new
            {
                addr = address,
                amount = amountSats,
                target_conf = 6 // Target 6 confirmations (~1 hour)
            };

            var response = await _httpClient.PostAsJsonAsync("transactions", request, JsonOptions, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                return OnChainPaymentResult.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    $"On-chain payment failed: {errorBody}");
            }

            var result = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
            var txid = result?["txid"]?.GetValue<string>();

            Console.Error.WriteLine($"[LND] On-chain tx sent: {txid}");

            return OnChainPaymentResult.Succeeded(
                txid ?? "",
                txid,
                "PENDING",
                amountSats,
                0); // Fee will be in the tx details
        }
        catch (Exception ex)
        {
            return OnChainPaymentResult.Failed("EXCEPTION", ex.Message);
        }
    }

    /// <summary>
    /// Currency exchange - not supported by LND (Lightning-only).
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
    /// Gets all balances - LND is BTC-only.
    /// </summary>
    public async Task<MultiCurrencyBalance> GetAllBalancesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var balance = await GetBalanceAsync(cancellationToken);
            var sats = balance.BalanceSats;

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

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }

    #region LND Response Models

    private class LndPaymentResponse
    {
        [JsonPropertyName("payment_error")]
        public string? PaymentError { get; set; }

        [JsonPropertyName("payment_preimage")]
        public string? PaymentPreimage { get; set; }

        [JsonPropertyName("payment_hash")]
        public string? PaymentHash { get; set; }

        [JsonPropertyName("payment_route")]
        public JsonObject? PaymentRoute { get; set; }
    }

    private class LndChannelBalance
    {
        [JsonPropertyName("local_balance")]
        public LndAmount? LocalBalance { get; set; }

        [JsonPropertyName("remote_balance")]
        public LndAmount? RemoteBalance { get; set; }
    }

    private class LndAmount
    {
        [JsonPropertyName("sat")]
        public long Sat { get; set; }

        [JsonPropertyName("msat")]
        public long Msat { get; set; }
    }

    private class LndInvoiceResponse
    {
        [JsonPropertyName("r_hash")]
        public string? RHash { get; set; }

        [JsonPropertyName("payment_request")]
        public string? PaymentRequest { get; set; }

        [JsonPropertyName("add_index")]
        public string? AddIndex { get; set; }
    }

    private class LndInvoiceLookup
    {
        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("settled")]
        public bool Settled { get; set; }

        [JsonPropertyName("settle_date")]
        public string? SettleDate { get; set; }
    }

    #endregion
}

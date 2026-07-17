using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Service for interacting with a Lightning wallet via OpenNode API.
/// Alternative to NWC for environments where OpenNode is the primary payment infrastructure.
/// </summary>
public class OpenNodeWalletService : IWalletService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private readonly string _environment;
    private bool _disposed;

    private const string ProductionBaseUrl = "https://api.opennode.com/v2/";
    private const string DevelopmentBaseUrl = "https://dev-api.opennode.com/v2/";

    public OpenNodeWalletService(HttpClient httpClient, IBudgetConfigurationService? budgetConfigService = null)
    {
        _httpClient = httpClient;
        _apiKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        if (string.IsNullOrEmpty(_apiKey) || _apiKey.StartsWith("${"))
        {
            _apiKey = budgetConfigService?.Configuration?.Wallets?.OpenNodeApiKey;
            if (!string.IsNullOrEmpty(_apiKey))
            {
                Console.Error.WriteLine("[OpenNode] Using API key from config file");
            }
        }
        _environment = Environment.GetEnvironmentVariable("OPENNODE_ENVIRONMENT");
        if (string.IsNullOrEmpty(_environment) || _environment.StartsWith("${"))
        {
            _environment = budgetConfigService?.Configuration?.Wallets?.OpenNodeEnvironment ?? "production";
        }

        if (!string.IsNullOrEmpty(_apiKey) && !_apiKey.StartsWith("${"))
        {
            var baseUrl = _environment.ToLowerInvariant() is "dev" or "development" or "testnet"
                ? DevelopmentBaseUrl
                : ProductionBaseUrl;

            _httpClient.BaseAddress = new Uri(baseUrl);
            _httpClient.DefaultRequestHeaders.Add("Authorization", _apiKey);
        }
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    public string ProviderName => "OpenNode";

    public NwcConfig? GetConfig()
    {
        // OpenNode doesn't use NwcConfig, but we return a placeholder for compatibility
        if (!IsConfigured)
            return null;

        return new NwcConfig
        {
            WalletPubkey = "opennode",
            RelayUrl = _httpClient.BaseAddress?.ToString() ?? ProductionBaseUrl,
            Secret = "opennode"
        };
    }

    public async Task<NwcPaymentResult> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return NwcPaymentResult.Failed("NOT_CONFIGURED",
                "OpenNode API key not configured. Set OPENNODE_API_KEY environment variable.");
        }

        try
        {
            // OpenNode expects just the invoice for Lightning withdrawals
            var payload = new
            {
                type = "ln",
                address = bolt11
            };

            var response = await _httpClient.PostAsJsonAsync("withdrawals", payload, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Don't leak full error content which might contain sensitive details
                var statusCode = (int)response.StatusCode;
                var errorMessage = statusCode switch
                {
                    401 => "Authentication failed. Check your OPENNODE_API_KEY.",
                    403 => "Access forbidden. Your API key may not have withdrawal permissions.",
                    404 => "OpenNode endpoint not found. Check OPENNODE_ENVIRONMENT setting.",
                    429 => "Rate limited by OpenNode. Please wait before retrying.",
                    >= 500 => "OpenNode service error. Please try again later.",
                    _ => $"OpenNode API error (status {statusCode}). Check API configuration."
                };
                return NwcPaymentResult.Failed("API_ERROR", errorMessage);
            }

            var responseJson = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
            var data = responseJson?["data"]?.AsObject();

            if (data == null)
            {
                return NwcPaymentResult.Failed("INVALID_RESPONSE", "OpenNode API returned empty response");
            }

            var status = data["status"]?.GetValue<string>()?.ToLowerInvariant();
            var withdrawalId = data["id"]?.GetValue<string>() ?? "unknown";

            switch (status)
            {
                case "paid":
                case "confirmed":
                case "completed":
                    // Payment SETTLED — the funds are gone. Check for a real preimage.
                    // NOTE: OpenNode does NOT return preimages for Lightning payments,
                    // so this practically always takes the no-preimage branch and L402
                    // verification will NOT work with OpenNode.
                    //
                    // The withdrawal ID is NOT a substitute: it is an internal OpenNode
                    // identifier, and handing it back as a preimage would publish it in
                    // the field L402 treats as proof of payment. Same for anything else
                    // that isn't preimage-shaped (OpenNode has been seen echoing the
                    // invoice back in this field).
                    var preimage = data["preimage"]?.GetValue<string>();
                    if (!Preimage.IsValid(preimage))
                    {
                        // Settled but unprovable — success WITHOUT a preimage. Not a
                        // failure: reporting it as one invites a retry and a double-spend.
                        Console.Error.WriteLine($"[OpenNode] Payment succeeded (withdrawal ID: {withdrawalId})");
                        Console.Error.WriteLine("[OpenNode] WARNING: No usable preimage returned - L402 verification will NOT work");
                        Console.Error.WriteLine("[OpenNode] For L402 support, use NWC or LND wallet instead");
                        return NwcPaymentResult.SucceededWithoutPreimage(
                            withdrawalId,
                            "OpenNode does not return a usable preimage. L402 requires a preimage as proof " +
                            "of payment - use an NWC or LND wallet. The funds have left your wallet.");
                    }
                    return NwcPaymentResult.Succeeded(preimage!);

                case "pending":
                case "processing":
                    // IN FLIGHT: no preimage exists yet and the payment may still FAIL.
                    // This must not collapse into terminal success — the caller would
                    // proceed believing it paid. It is not a hard failure either, so the
                    // caller must not retry it: the funds are already committed.
                    Console.Error.WriteLine($"[OpenNode] Payment pending/processing (withdrawal ID: {withdrawalId})");
                    return NwcPaymentResult.Pending(
                        withdrawalId,
                        $"Payment is {status} and has not settled — it may still fail. Do not treat it as " +
                        "paid and do not retry it; check the withdrawal status with OpenNode instead.");

                default:
                    return NwcPaymentResult.Failed("PAYMENT_FAILED",
                        $"Payment failed with status: {status}");
            }
        }
        catch (HttpRequestException)
        {
            // Don't leak HTTP error details that might contain sensitive info
            return NwcPaymentResult.Failed("HTTP_ERROR", "HTTP request failed. Check network connectivity and OpenNode API status.");
        }
        catch (JsonException)
        {
            return NwcPaymentResult.Failed("JSON_ERROR", "Failed to parse OpenNode response. The API may be experiencing issues.");
        }
        catch (Exception)
        {
            // Don't leak exception details that might contain API keys or other sensitive info
            return NwcPaymentResult.Failed("EXCEPTION", "Payment request failed. Check wallet configuration.");
        }
    }

    public async Task<NwcBalanceInfo> GetBalanceAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("OpenNode API key not configured");
        }

        try
        {
            // OpenNode doesn't have a direct balance endpoint for merchant accounts
            // Try to get account info instead
            var response = await _httpClient.GetAsync("account/balance", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
                var data = responseJson?["data"]?.AsObject();
                var balanceBtc = data?["balance"]?["BTC"]?.GetValue<decimal>() ?? 0m;

                // Convert BTC to millisatoshis
                var balanceMsat = (long)(balanceBtc * 100_000_000_000m);

                return new NwcBalanceInfo { BalanceMsat = balanceMsat };
            }

            // Balance endpoint may not be available, return -1 to indicate unknown
            return new NwcBalanceInfo { BalanceMsat = -1000 }; // -1 sats
        }
        catch
        {
            // Balance not available via OpenNode API
            return new NwcBalanceInfo { BalanceMsat = -1000 };
        }
    }

    /// <summary>
    /// Creates a Lightning invoice to receive payment.
    /// </summary>
    public async Task<WalletInvoiceResult> CreateInvoiceAsync(
        long amountSats,
        string? memo = null,
        int expirySecs = 3600,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return WalletInvoiceResult.Failed("NOT_CONFIGURED",
                "OpenNode not configured. Set OPENNODE_API_KEY environment variable.");
        }

        try
        {
            var payload = new
            {
                amount = amountSats,
                description = memo ?? "Lightning payment",
                expiry = expirySecs,
                currency = "btc"
            };

            Console.Error.WriteLine($"[OpenNode] Creating invoice for {amountSats} sats...");

            var response = await _httpClient.PostAsJsonAsync("charges", payload, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                return WalletInvoiceResult.Failed(
                    $"HTTP_{statusCode}",
                    $"Failed to create invoice (status {statusCode})");
            }

            var responseJson = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
            var data = responseJson?["data"]?.AsObject();

            if (data == null)
            {
                return WalletInvoiceResult.Failed("INVALID_RESPONSE", "Empty response from OpenNode");
            }

            var invoiceId = data["id"]?.GetValue<string>();
            var bolt11 = data["lightning_invoice"]?["payreq"]?.GetValue<string>();
            var expiresAt = data["lightning_invoice"]?["expires_at"]?.GetValue<long>();

            if (string.IsNullOrEmpty(invoiceId) || string.IsNullOrEmpty(bolt11))
            {
                return WalletInvoiceResult.Failed("INVALID_RESPONSE", "No invoice returned");
            }

            DateTime? expiryTime = expiresAt.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(expiresAt.Value).UtcDateTime
                : null;

            Console.Error.WriteLine($"[OpenNode] Invoice created: {invoiceId}");

            return WalletInvoiceResult.Succeeded(invoiceId, bolt11, amountSats, expiryTime);
        }
        catch (HttpRequestException ex)
        {
            return WalletInvoiceResult.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return WalletInvoiceResult.Failed("JSON_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Checks the status of a previously created invoice.
    /// </summary>
    public async Task<WalletInvoiceStatus> GetInvoiceStatusAsync(string invoiceId, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return WalletInvoiceStatus.Failed("NOT_CONFIGURED",
                "OpenNode not configured. Set OPENNODE_API_KEY environment variable.");
        }

        try
        {
            Console.Error.WriteLine($"[OpenNode] Checking invoice status: {invoiceId}");

            var response = await _httpClient.GetAsync($"charge/{invoiceId}", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return WalletInvoiceStatus.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    "Failed to get invoice status");
            }

            var responseJson = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
            var data = responseJson?["data"]?.AsObject();

            if (data == null)
            {
                return WalletInvoiceStatus.Failed("INVALID_RESPONSE", "Empty response");
            }

            var status = data["status"]?.GetValue<string>()?.ToUpperInvariant();
            var amount = data["amount"]?.GetValue<long>() ?? 0;

            // Map OpenNode status to our standard states
            var state = status switch
            {
                "PAID" => "PAID",
                "PROCESSING" => "PENDING",
                "UNPAID" => "PENDING",
                "UNDERPAID" => "PENDING",
                "EXPIRED" => "EXPIRED",
                "REFUNDED" => "CANCELLED",
                _ => status ?? "UNKNOWN"
            };

            Console.Error.WriteLine($"[OpenNode] Invoice {invoiceId} state: {state}");

            return WalletInvoiceStatus.Succeeded(invoiceId, state, amount);
        }
        catch (HttpRequestException ex)
        {
            return WalletInvoiceStatus.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return WalletInvoiceStatus.Failed("JSON_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Gets BTC price ticker - not directly supported by OpenNode wallet API.
    /// </summary>
    public Task<WalletTickerResult> GetTickerAsync(CancellationToken cancellationToken = default)
    {
        // OpenNode doesn't expose ticker in their wallet API
        return Task.FromResult(WalletTickerResult.NotSupported());
    }

    /// <summary>
    /// Sends an on-chain Bitcoin payment - not supported by OpenNode wallet API.
    /// </summary>
    public Task<OnChainPaymentResult> SendOnChainAsync(
        string address,
        long amountSats,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(OnChainPaymentResult.NotSupported());
    }

    /// <summary>
    /// Exchanges currency - not supported by OpenNode wallet API.
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
    /// Gets all currency balances - OpenNode is BTC-only.
    /// </summary>
    public async Task<MultiCurrencyBalance> GetAllBalancesAsync(CancellationToken cancellationToken = default)
    {
        var btcBalance = await GetBalanceAsync(cancellationToken);
        var sats = btcBalance.BalanceSats;

        // If -1, balance is unknown
        if (sats < 0)
        {
            return MultiCurrencyBalance.Failed("UNKNOWN", "Balance not available via OpenNode API");
        }

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

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}

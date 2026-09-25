using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Wallet service using Strike's REST API.
/// Allows users to pay Lightning invoices from their Strike balance.
///
/// Strike now returns preimage for outgoing Lightning payments via the
/// lightning.preImage property on the execute-payment-quote response.
/// This enables full L402 authentication support.
///
/// Configuration: Set STRIKE_API_KEY environment variable.
/// Get your API key from: https://dashboard.strike.me/
/// </summary>
public class StrikeWalletService : IWalletService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private bool _disposed;

    private const string BaseUrl = "https://api.strike.me/v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // On-chain completion polling window. Production: 120 s at 2 s intervals.
    private readonly TimeSpan _onChainPollTimeout = TimeSpan.FromSeconds(120);
    private readonly TimeSpan _onChainPollInterval = TimeSpan.FromSeconds(2);

    // Test-only: explicit API key (no env lookup) and a short polling window.
    internal StrikeWalletService(HttpClient httpClient, string apiKey, TimeSpan onChainPollTimeout, TimeSpan onChainPollInterval)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
        _onChainPollTimeout = onChainPollTimeout;
        _onChainPollInterval = onChainPollInterval;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    public StrikeWalletService(HttpClient httpClient, IBudgetConfigurationService? budgetConfigService = null)
    {
        _httpClient = httpClient;

        // Try process env first, then User-level, then config file
        _apiKey = Environment.GetEnvironmentVariable("STRIKE_API_KEY");
        if (string.IsNullOrEmpty(_apiKey) || _apiKey.StartsWith("${"))
        {
            _apiKey = Environment.GetEnvironmentVariable("STRIKE_API_KEY", EnvironmentVariableTarget.User);
        }
        if (string.IsNullOrEmpty(_apiKey) || _apiKey.StartsWith("${"))
        {
            _apiKey = budgetConfigService?.Configuration?.Wallets?.StrikeApiKey;
            if (!string.IsNullOrEmpty(_apiKey))
            {
                Console.Error.WriteLine("[Strike] Using API key from config file");
            }
        }

        Console.Error.WriteLine($"[Strike] Initializing service. API key configured: {IsConfigured}");
        if (IsConfigured)
        {
            Console.Error.WriteLine("[Strike] API key configured");
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _apiKey);
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            Console.Error.WriteLine("[Strike] Authorization header set");
        }
        else
        {
            Console.Error.WriteLine("[Strike] WARNING: STRIKE_API_KEY not found in environment!");
        }
    }

    /// <summary>
    /// Whether the Strike API key is configured.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// Provider name for identification.
    /// </summary>
    public string ProviderName => "Strike";

    /// <summary>
    /// Pays a BOLT11 Lightning invoice using Strike.
    /// Returns preimage from the lightning.preImage response field for L402 support.
    /// </summary>
    public async Task<NwcPaymentResult> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return NwcPaymentResult.Failed("NOT_CONFIGURED",
                "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }

        try
        {
            // Step 1: Create payment quote
            var quoteRequest = new StrikePaymentQuoteRequest
            {
                LnInvoice = bolt11,
                SourceCurrency = "BTC"
            };

            Console.Error.WriteLine("[Strike] Creating payment quote...");

            var quoteResponse = await _httpClient.PostAsJsonAsync(
                $"{BaseUrl}/payment-quotes/lightning",
                quoteRequest,
                JsonOptions,
                cancellationToken);

            if (!quoteResponse.IsSuccessStatusCode)
            {
                var errorBody = await quoteResponse.Content.ReadAsStringAsync(cancellationToken);
                Console.Error.WriteLine($"[Strike] Quote failed: {errorBody}");
                return NwcPaymentResult.Failed(
                    $"HTTP_{(int)quoteResponse.StatusCode}",
                    $"Failed to create payment quote: {errorBody}");
            }

            var quote = await quoteResponse.Content.ReadFromJsonAsync<StrikePaymentQuote>(
                JsonOptions, cancellationToken);

            if (quote == null || string.IsNullOrEmpty(quote.PaymentQuoteId))
            {
                return NwcPaymentResult.Failed("INVALID_QUOTE", "No payment quote ID returned");
            }

            Console.Error.WriteLine($"[Strike] Quote created: {quote.PaymentQuoteId}");

            // Step 2: Execute the payment quote
            var executeResponse = await _httpClient.PatchAsync(
                $"{BaseUrl}/payment-quotes/{quote.PaymentQuoteId}/execute",
                null,
                cancellationToken);

            if (!executeResponse.IsSuccessStatusCode)
            {
                var errorBody = await executeResponse.Content.ReadAsStringAsync(cancellationToken);
                Console.Error.WriteLine($"[Strike] Execute failed: {errorBody}");
                return NwcPaymentResult.Failed(
                    $"HTTP_{(int)executeResponse.StatusCode}",
                    $"Failed to execute payment: {errorBody}");
            }

            var payment = await executeResponse.Content.ReadFromJsonAsync<StrikePayment>(
                JsonOptions, cancellationToken);

            if (payment == null)
            {
                return NwcPaymentResult.Failed("INVALID_PAYMENT", "No payment returned");
            }

            Console.Error.WriteLine($"[Strike] Payment executed: {payment.PaymentId}, state: {payment.State}");

            // Step 3: If pending, poll for completion (up to 60 seconds)
            if (payment.State == "PENDING")
            {
                payment = await WaitForPaymentCompletion(payment.PaymentId, 60, cancellationToken);
            }

            if (payment.State == "COMPLETED")
            {
                Console.Error.WriteLine($"[Strike] Payment completed: {payment.PaymentId}");

                // Strike now returns preimage on the lightning.preImage property
                var preimage = payment.Lightning?.PreImage;
                if (Preimage.IsValid(preimage))
                {
                    Console.Error.WriteLine("[Strike] Preimage received, L402 fully supported");
                    return NwcPaymentResult.Succeeded(preimage!);
                }

                // No usable preimage (older API, non-Lightning payment, or a value that
                // isn't preimage-shaped). The payment COMPLETED — the funds are gone —
                // but the Strike payment ID is an internal identifier, not proof of
                // payment, and must never stand in for the preimage.
                Console.Error.WriteLine("[Strike] WARNING: No usable preimage in response - L402 will NOT work for this payment");
                return NwcPaymentResult.SucceededWithoutPreimage(
                    payment.PaymentId,
                    "Strike did not return a usable preimage for this payment. L402 requires a preimage " +
                    "as proof of payment. The funds have left your wallet.");
            }

            return NwcPaymentResult.Failed(payment.State ?? "UNKNOWN", "Payment did not complete");
        }
        catch (TaskCanceledException)
        {
            return NwcPaymentResult.Failed("TIMEOUT", "Payment request timed out");
        }
        catch (HttpRequestException ex)
        {
            return NwcPaymentResult.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return NwcPaymentResult.Failed("JSON_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Gets the Strike account balance.
    /// </summary>
    public async Task<NwcBalanceInfo> GetBalanceAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            Console.Error.WriteLine("[Strike] GetBalanceAsync: Not configured");
            return new NwcBalanceInfo { BalanceMsat = 0 };
        }

        try
        {
            Console.Error.WriteLine($"[Strike] GetBalanceAsync: Calling {BaseUrl}/balances");
            Console.Error.WriteLine($"[Strike] Auth header present: {_httpClient.DefaultRequestHeaders.Authorization != null}");

            var response = await _httpClient.GetAsync($"{BaseUrl}/balances", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.Error.WriteLine($"[Strike] Balance check failed: {response.StatusCode}");
                Console.Error.WriteLine($"[Strike] Error response: {errorBody}");

                throw new HttpRequestException($"Strike API error ({response.StatusCode}): {errorBody}");
            }

            var balances = await response.Content.ReadFromJsonAsync<List<StrikeBalance>>(
                JsonOptions, cancellationToken);

            if (balances == null || balances.Count == 0)
            {
                return new NwcBalanceInfo { BalanceMsat = 0 };
            }

            // Find BTC balance and convert to millisatoshis
            long totalMsat = 0;
            foreach (var balance in balances)
            {
                if (balance.Currency?.ToUpperInvariant() == "BTC" && balance.Current != null)
                {
                    var sats = ConvertToSats(balance.Current, "BTC");
                    totalMsat = sats * 1000;
                    break;
                }
            }

            Console.Error.WriteLine($"[Strike] Balance: {totalMsat / 1000} sats");
            return new NwcBalanceInfo { BalanceMsat = totalMsat };
        }
        catch (HttpRequestException)
        {
            // Rethrow HTTP errors so caller knows there was an API failure
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Strike] Balance error: {ex.Message}");
            return new NwcBalanceInfo { BalanceMsat = 0 };
        }
    }

    /// <summary>
    /// Gets the current configuration (not applicable for Strike).
    /// </summary>
    public NwcConfig? GetConfig() => null;

    /// <summary>
    /// Creates a Lightning invoice to receive payment.
    /// Uses Strike's receive-requests endpoint.
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
                "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }

        try
        {
            // Convert sats to BTC for Strike API
            var amountBtc = amountSats / 100_000_000m;

            var request = new
            {
                bolt11 = new
                {
                    amount = new { amount = amountBtc.ToString("F8"), currency = "BTC" },
                    description = memo ?? "Lightning payment",
                    expiryInSeconds = expirySecs
                },
                targetCurrency = "BTC"
            };

            Console.Error.WriteLine($"[Strike] Creating invoice for {amountSats} sats...");

            var response = await _httpClient.PostAsJsonAsync(
                $"{BaseUrl}/receive-requests",
                request,
                JsonOptions,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.Error.WriteLine($"[Strike] Create invoice failed: {errorBody}");
                return WalletInvoiceResult.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    $"Failed to create invoice: {errorBody}");
            }

            var receiveRequest = await response.Content.ReadFromJsonAsync<StrikeReceiveResponse>(
                JsonOptions, cancellationToken);

            if (receiveRequest?.Bolt11?.Invoice == null)
            {
                return WalletInvoiceResult.Failed("INVALID_RESPONSE", "No invoice returned");
            }

            Console.Error.WriteLine($"[Strike] Invoice created: {receiveRequest.ReceiveRequestId}");

            return WalletInvoiceResult.Succeeded(
                receiveRequest.ReceiveRequestId ?? "",
                receiveRequest.Bolt11.Invoice,
                amountSats,
                receiveRequest.Bolt11.ExpiresAt);
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
                "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }

        try
        {
            Console.Error.WriteLine($"[Strike] Checking invoice status: {invoiceId}");

            var response = await _httpClient.GetAsync(
                $"{BaseUrl}/receive-requests/{invoiceId}",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                return WalletInvoiceStatus.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    $"Failed to get invoice status: {errorBody}");
            }

            var receiveRequest = await response.Content.ReadFromJsonAsync<StrikeReceiveResponse>(
                JsonOptions, cancellationToken);

            if (receiveRequest == null)
            {
                return WalletInvoiceStatus.Failed("INVALID_RESPONSE", "No response returned");
            }

            // Parse amount from the invoice
            long amountSats = 0;
            if (receiveRequest.Bolt11?.BtcAmount != null)
            {
                amountSats = ConvertToSats(receiveRequest.Bolt11.BtcAmount, "BTC");
            }

            Console.Error.WriteLine($"[Strike] Invoice {invoiceId} state: {receiveRequest.State}");

            return WalletInvoiceStatus.Succeeded(
                invoiceId,
                receiveRequest.State ?? "UNKNOWN",
                amountSats,
                receiveRequest.CompletedAt);
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
    /// Gets the current BTC/USD price from Strike.
    /// </summary>
    public async Task<WalletTickerResult> GetTickerAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return WalletTickerResult.Failed("NOT_CONFIGURED",
                "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }

        try
        {
            var response = await _httpClient.GetAsync($"{BaseUrl}/rates/ticker", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return WalletTickerResult.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    "Failed to get ticker");
            }

            var tickers = await response.Content.ReadFromJsonAsync<List<StrikeTicker>>(
                JsonOptions, cancellationToken);

            // Find BTCUSD rate
            var btcUsd = tickers?.FirstOrDefault(t =>
                t.SourceCurrency?.ToUpperInvariant() == "BTC" &&
                t.TargetCurrency?.ToUpperInvariant() == "USD");

            if (btcUsd?.Amount == null || !decimal.TryParse(btcUsd.Amount, out var rate))
            {
                return WalletTickerResult.Failed("NO_RATE", "BTC/USD rate not found");
            }

            Console.Error.WriteLine($"[Strike] BTC/USD: ${rate:N2}");

            return WalletTickerResult.Succeeded(rate);
        }
        catch (HttpRequestException ex)
        {
            return WalletTickerResult.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return WalletTickerResult.Failed("JSON_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Sends an on-chain Bitcoin payment.
    /// Uses Strike's payment-quotes/onchain endpoint.
    /// </summary>
    public async Task<OnChainPaymentResult> SendOnChainAsync(
        string address,
        long amountSats,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return OnChainPaymentResult.Failed("NOT_CONFIGURED",
                "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            return OnChainPaymentResult.Failed("INVALID_ADDRESS", "Bitcoin address is required");
        }

        if (amountSats <= 0)
        {
            return OnChainPaymentResult.Failed("INVALID_AMOUNT", "Amount must be greater than 0");
        }

        // Phase 1 — quote. Nothing has been executed yet, so ANY failure here (timeout,
        // cancellation, transport, bad JSON) proves no funds moved: Submitted = false.
        StrikeOnChainQuote? quote;
        try
        {
            // Convert sats to BTC for Strike API
            var amountBtc = amountSats / 100_000_000m;

            var quoteRequest = new
            {
                btcAddress = address,
                sourceCurrency = "USD",
                sourceAmount = new { currency = "BTC", amount = amountBtc.ToString("F8") }
            };

            Console.Error.WriteLine($"[Strike] Creating on-chain quote for {amountSats} sats to {address}...");

            var quoteResponse = await _httpClient.PostAsJsonAsync(
                $"{BaseUrl}/payment-quotes/onchain",
                quoteRequest,
                JsonOptions,
                cancellationToken);

            if (!quoteResponse.IsSuccessStatusCode)
            {
                var errorBody = await quoteResponse.Content.ReadAsStringAsync(cancellationToken);
                Console.Error.WriteLine($"[Strike] On-chain quote failed: {errorBody}");
                return OnChainPaymentResult.Failed(
                    $"HTTP_{(int)quoteResponse.StatusCode}",
                    $"Failed to create on-chain quote: {errorBody}");
            }

            quote = await quoteResponse.Content.ReadFromJsonAsync<StrikeOnChainQuote>(
                JsonOptions, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return OnChainPaymentResult.Failed("TIMEOUT", "On-chain quote request timed out before anything was executed");
        }
        catch (HttpRequestException ex)
        {
            return OnChainPaymentResult.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return OnChainPaymentResult.Failed("JSON_ERROR", ex.Message);
        }

        if (quote == null || string.IsNullOrEmpty(quote.PaymentQuoteId))
        {
            return OnChainPaymentResult.Failed("INVALID_QUOTE", "No payment quote ID returned");
        }

        var quoteId = quote.PaymentQuoteId;
        Console.Error.WriteLine($"[Strike] On-chain quote created: {quoteId}");

        long feeSats = 0;
        if (quote.OnchainFee?.Amount != null)
        {
            feeSats = ConvertToSats(quote.OnchainFee.Amount, quote.OnchainFee.Currency);
        }

        // Phase 2 — execute. From the moment this call is ISSUED, funds may move: a lost
        // response (timeout, cancellation, transport error, 5xx, unreadable body) is
        // AMBIGUOUS, never "failed". Report Submitted = true / UNKNOWN with the ids we have.
        StrikePayment? payment;
        try
        {
            var executeResponse = await _httpClient.PatchAsync(
                $"{BaseUrl}/payment-quotes/{quoteId}/execute",
                null,
                cancellationToken);

            if (!executeResponse.IsSuccessStatusCode)
            {
                var errorBody = await SafeReadAsync(executeResponse);
                var code = (int)executeResponse.StatusCode;
                Console.Error.WriteLine($"[Strike] On-chain execute failed: HTTP {code}");
                // A definitive 4xx rejection (bad request, auth, insufficient funds, expired
                // quote) means Strike did not execute. 408/409/429 and every 5xx do NOT prove
                // that, so they stay ambiguous.
                if (code >= 400 && code < 500 && code is not 408 and not 409 and not 429)
                {
                    return new OnChainPaymentResult
                    {
                        Success = false,
                        Submitted = false,
                        QuoteId = quoteId,
                        State = "FAILED",
                        ErrorCode = $"HTTP_{code}",
                        ErrorMessage = $"Strike rejected the on-chain payment execution: {errorBody}"
                    };
                }
                return Ambiguous(quoteId, null, amountSats, feeSats, $"HTTP_{code}",
                    $"Strike returned HTTP {code} to the on-chain execute call; the payment may still have executed.");
            }

            payment = await executeResponse.Content.ReadFromJsonAsync<StrikePayment>(JsonOptions, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Ambiguous(quoteId, null, amountSats, feeSats, "TIMEOUT",
                "The on-chain execute call timed out or was cancelled after it was sent; the payment may have executed.");
        }
        catch (HttpRequestException ex)
        {
            return Ambiguous(quoteId, null, amountSats, feeSats, "HTTP_ERROR",
                $"Transport error during the on-chain execute call ({ex.Message}); the payment may have executed.");
        }
        catch (JsonException)
        {
            return Ambiguous(quoteId, null, amountSats, feeSats, "JSON_ERROR",
                "Strike accepted the on-chain execute call but its response could not be read; the payment may have executed.");
        }

        if (payment == null || string.IsNullOrEmpty(payment.PaymentId))
        {
            return Ambiguous(quoteId, null, amountSats, feeSats, "INVALID_PAYMENT",
                "Strike accepted the on-chain execute call but returned no payment id; the payment may have executed.");
        }

        Console.Error.WriteLine($"[Strike] On-chain payment executed: {payment.PaymentId}, state: {payment.State}");

        // Phase 3 — poll. Execute succeeded, so the payment exists at Strike. Running out of
        // polling time (or being cancelled / losing the connection while polling) leaves it
        // PENDING — the normal on-chain state for ~10 minutes — not failed.
        var state = string.IsNullOrEmpty(payment.State) ? "PENDING" : payment.State;
        string? txId = payment.Onchain?.TxId;
        if (state == "PENDING")
        {
            try
            {
                var polled = await WaitForOnChainCompletion(payment.PaymentId, _onChainPollTimeout, _onChainPollInterval, cancellationToken);
                if (polled != null)
                {
                    state = polled.State ?? "PENDING";
                    txId = polled.Onchain?.TxId ?? txId;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or JsonException)
            {
                Console.Error.WriteLine($"[Strike] Stopped polling on-chain payment {payment.PaymentId}; it remains PENDING.");
            }
        }

        if (state == "FAILED")
        {
            // Strike reports the executed payment as terminally FAILED: no funds left the account.
            return new OnChainPaymentResult
            {
                Success = false,
                Submitted = true,
                PaymentId = payment.PaymentId,
                QuoteId = quoteId,
                State = "FAILED",
                AmountSats = amountSats,
                ErrorCode = "PAYMENT_FAILED",
                ErrorMessage = "Strike reported the on-chain payment as FAILED."
            };
        }

        return new OnChainPaymentResult
        {
            Success = true,
            Submitted = true,
            PaymentId = payment.PaymentId,
            QuoteId = quoteId,
            TxId = txId,
            State = state,
            AmountSats = amountSats,
            FeeSats = feeSats
        };
    }

    private static OnChainPaymentResult Ambiguous(string quoteId, string? paymentId, long amountSats, long feeSats, string errorCode, string message) =>
        new()
        {
            Success = false,
            Submitted = true,
            State = "UNKNOWN",
            QuoteId = quoteId,
            PaymentId = paymentId,
            AmountSats = amountSats,
            FeeSats = feeSats,
            ErrorCode = errorCode,
            ErrorMessage = message
        };

    private static async Task<string> SafeReadAsync(HttpResponseMessage response)
    {
        try { return await response.Content.ReadAsStringAsync(); }
        catch { return "(no response body)"; }
    }

    /// <summary>
    /// On-chain polling: returns the first non-PENDING payment, or null when the window runs
    /// out (the payment is still PENDING — not a failure).
    /// </summary>
    private async Task<StrikePayment?> WaitForOnChainCompletion(
        string paymentId,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        var endTime = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < endTime)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await _httpClient.GetAsync(
                $"{BaseUrl}/payments/{Uri.EscapeDataString(paymentId)}", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var polled = await response.Content.ReadFromJsonAsync<StrikePayment>(JsonOptions, cancellationToken);
                if (polled != null && !string.IsNullOrEmpty(polled.State) && polled.State != "PENDING")
                {
                    return polled;
                }
            }

            await Task.Delay(pollInterval, cancellationToken);
        }
        return null;
    }

    /// <summary>
    /// Looks up an on-chain payment by id (<c>GET /v1/payments/{id}</c>). Moves no value.
    /// </summary>
    public async Task<OnChainPaymentResult> GetOnChainPaymentStatusAsync(
        string paymentId,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return OnChainPaymentResult.Failed("NOT_CONFIGURED", "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }
        if (string.IsNullOrWhiteSpace(paymentId))
        {
            return OnChainPaymentResult.Failed("INVALID_PAYMENT_ID", "Payment id is required");
        }

        try
        {
            var response = await _httpClient.GetAsync(
                $"{BaseUrl}/payments/{Uri.EscapeDataString(paymentId)}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return OnChainPaymentResult.Failed($"HTTP_{(int)response.StatusCode}",
                    $"Strike status lookup for payment {paymentId} returned HTTP {(int)response.StatusCode}");
            }

            var payment = await response.Content.ReadFromJsonAsync<StrikePayment>(JsonOptions, cancellationToken);
            if (payment == null || string.IsNullOrEmpty(payment.State))
            {
                return OnChainPaymentResult.Failed("INVALID_PAYMENT", "Strike returned no payment state");
            }

            return new OnChainPaymentResult
            {
                Success = true,
                Submitted = true,
                PaymentId = string.IsNullOrEmpty(payment.PaymentId) ? paymentId : payment.PaymentId,
                State = payment.State,
                TxId = payment.Onchain?.TxId
            };
        }
        catch (OperationCanceledException)
        {
            return OnChainPaymentResult.Failed("TIMEOUT", "Strike status lookup timed out");
        }
        catch (HttpRequestException ex)
        {
            return OnChainPaymentResult.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return OnChainPaymentResult.Failed("JSON_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Exchanges currency (USD ↔ BTC) using Strike.
    /// </summary>
    public async Task<CurrencyExchangeResult> ExchangeCurrencyAsync(
        string sourceCurrency,
        string targetCurrency,
        decimal amount,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return CurrencyExchangeResult.Failed("NOT_CONFIGURED",
                "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }

        sourceCurrency = sourceCurrency.ToUpperInvariant();
        targetCurrency = targetCurrency.ToUpperInvariant();

        // Validate currencies
        if ((sourceCurrency != "USD" && sourceCurrency != "BTC") ||
            (targetCurrency != "USD" && targetCurrency != "BTC"))
        {
            return CurrencyExchangeResult.Failed("INVALID_CURRENCY",
                "Strike only supports USD and BTC currency exchange");
        }

        if (sourceCurrency == targetCurrency)
        {
            return CurrencyExchangeResult.Failed("SAME_CURRENCY",
                "Source and target currency cannot be the same");
        }

        if (amount <= 0)
        {
            return CurrencyExchangeResult.Failed("INVALID_AMOUNT", "Amount must be greater than 0");
        }

        try
        {
            // Create exchange quote
            var quoteRequest = new
            {
                sell = sourceCurrency,
                buy = targetCurrency,
                amount = new { currency = sourceCurrency, amount = amount.ToString("F8") }
            };

            Console.Error.WriteLine($"[Strike] Creating exchange quote: {amount} {sourceCurrency} → {targetCurrency}...");

            var quoteResponse = await _httpClient.PostAsJsonAsync(
                $"{BaseUrl}/currency-exchange-quotes",
                quoteRequest,
                JsonOptions,
                cancellationToken);

            if (!quoteResponse.IsSuccessStatusCode)
            {
                var errorBody = await quoteResponse.Content.ReadAsStringAsync(cancellationToken);
                Console.Error.WriteLine($"[Strike] Exchange quote failed: {errorBody}");
                return CurrencyExchangeResult.Failed(
                    $"HTTP_{(int)quoteResponse.StatusCode}",
                    $"Failed to create exchange quote: {errorBody}");
            }

            var quote = await quoteResponse.Content.ReadFromJsonAsync<StrikeExchangeQuote>(
                JsonOptions, cancellationToken);

            if (quote == null || string.IsNullOrEmpty(quote.Id))
            {
                return CurrencyExchangeResult.Failed("INVALID_QUOTE", "No exchange quote ID returned");
            }

            Console.Error.WriteLine($"[Strike] Exchange quote created: {quote.Id}");

            // Execute the exchange
            var executeResponse = await _httpClient.PatchAsync(
                $"{BaseUrl}/currency-exchange-quotes/{quote.Id}/execute",
                null,
                cancellationToken);

            if (!executeResponse.IsSuccessStatusCode)
            {
                var errorBody = await executeResponse.Content.ReadAsStringAsync(cancellationToken);
                Console.Error.WriteLine($"[Strike] Exchange execute failed: {errorBody}");
                return CurrencyExchangeResult.Failed(
                    $"HTTP_{(int)executeResponse.StatusCode}",
                    $"Failed to execute exchange: {errorBody}");
            }

            var exchange = await executeResponse.Content.ReadFromJsonAsync<StrikeExchangeQuote>(
                JsonOptions, cancellationToken);

            if (exchange == null)
            {
                return CurrencyExchangeResult.Failed("INVALID_EXCHANGE", "No exchange result returned");
            }

            // Parse amounts
            decimal sourceAmount = 0;
            decimal targetAmount = 0;
            decimal? fee = null;

            if (exchange.SourceAmount?.Amount != null)
                decimal.TryParse(exchange.SourceAmount.Amount, out sourceAmount);
            if (exchange.TargetAmount?.Amount != null)
                decimal.TryParse(exchange.TargetAmount.Amount, out targetAmount);
            if (exchange.Fee?.Amount != null && decimal.TryParse(exchange.Fee.Amount, out var feeAmount))
                fee = feeAmount;

            // Calculate rate
            decimal? rate = targetAmount > 0 && sourceAmount > 0 ? targetAmount / sourceAmount : null;

            Console.Error.WriteLine($"[Strike] Exchange completed: {sourceAmount} {sourceCurrency} → {targetAmount} {targetCurrency}");

            return CurrencyExchangeResult.Succeeded(
                exchange.Id ?? "",
                sourceCurrency,
                targetCurrency,
                sourceAmount,
                targetAmount,
                rate,
                fee,
                exchange.State ?? "COMPLETED");
        }
        catch (TaskCanceledException)
        {
            return CurrencyExchangeResult.Failed("TIMEOUT", "Exchange request timed out");
        }
        catch (HttpRequestException ex)
        {
            return CurrencyExchangeResult.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return CurrencyExchangeResult.Failed("JSON_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Gets all currency balances from Strike (USD, BTC, etc.).
    /// </summary>
    public async Task<MultiCurrencyBalance> GetAllBalancesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return MultiCurrencyBalance.Failed("NOT_CONFIGURED",
                "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }

        try
        {
            var response = await _httpClient.GetAsync($"{BaseUrl}/balances", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                return MultiCurrencyBalance.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    $"Failed to get balances: {errorBody}");
            }

            var balances = await response.Content.ReadFromJsonAsync<List<StrikeBalance>>(
                JsonOptions, cancellationToken);

            if (balances == null)
            {
                return MultiCurrencyBalance.Failed("INVALID_RESPONSE", "No balances returned");
            }

            var result = new List<CurrencyBalance>();

            foreach (var balance in balances)
            {
                if (string.IsNullOrEmpty(balance.Currency))
                    continue;

                decimal available = 0, total = 0, pending = 0;

                if (balance.Available != null)
                    decimal.TryParse(balance.Available, out available);
                else if (balance.Current != null)
                    decimal.TryParse(balance.Current, out available);
                if (balance.Total != null)
                    decimal.TryParse(balance.Total, out total);
                if (balance.Pending != null)
                    decimal.TryParse(balance.Pending, out pending);

                result.Add(new CurrencyBalance
                {
                    Currency = balance.Currency.ToUpperInvariant(),
                    Available = available,
                    Total = total > 0 ? total : available,
                    Pending = pending
                });
            }

            Console.Error.WriteLine($"[Strike] Retrieved {result.Count} currency balances");

            return MultiCurrencyBalance.Succeeded(result);
        }
        catch (HttpRequestException ex)
        {
            return MultiCurrencyBalance.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return MultiCurrencyBalance.Failed("JSON_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Polls for payment completion.
    /// </summary>
    private async Task<StrikePayment> WaitForPaymentCompletion(
        string paymentId,
        int timeoutSecs,
        CancellationToken cancellationToken)
    {
        var endTime = DateTime.UtcNow.AddSeconds(timeoutSecs);
        var pollInterval = TimeSpan.FromSeconds(2);

        while (DateTime.UtcNow < endTime)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await _httpClient.GetAsync(
                $"{BaseUrl}/payments/{paymentId}",
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var payment = await response.Content.ReadFromJsonAsync<StrikePayment>(
                    JsonOptions, cancellationToken);

                if (payment != null && payment.State != "PENDING")
                {
                    return payment;
                }
            }

            await Task.Delay(pollInterval, cancellationToken);
        }

        return new StrikePayment { PaymentId = paymentId, State = "TIMEOUT" };
    }

    /// <summary>
    /// Converts an amount string to satoshis.
    /// </summary>
    private static long ConvertToSats(string? amountStr, string? currency)
    {
        if (string.IsNullOrEmpty(amountStr) || !decimal.TryParse(amountStr, out var amount))
            return 0;

        return currency?.ToUpperInvariant() switch
        {
            "BTC" => (long)(amount * 100_000_000m),
            "SAT" or "SATS" or "SATOSHI" => (long)amount,
            "MSAT" or "MILLISATOSHI" => (long)(amount / 1000m),
            _ => 0
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }

    #region Strike API Models

    private class StrikePaymentQuoteRequest
    {
        [JsonPropertyName("lnInvoice")]
        public string LnInvoice { get; set; } = "";

        [JsonPropertyName("sourceCurrency")]
        public string SourceCurrency { get; set; } = "BTC";
    }

    private class StrikePaymentQuote
    {
        [JsonPropertyName("paymentQuoteId")]
        public string? PaymentQuoteId { get; set; }
    }

    private class StrikePayment
    {
        [JsonPropertyName("paymentId")]
        public string PaymentId { get; set; } = "";

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("lightning")]
        public StrikeLightningDetails? Lightning { get; set; }

        [JsonPropertyName("onchain")]
        public StrikeOnchainDetails? Onchain { get; set; }
    }

    private class StrikeOnchainDetails
    {
        [JsonPropertyName("txnId")]
        public string? TxnId { get; set; }

        [JsonPropertyName("txId")]
        public string? TxIdAlt { get; set; }

        [JsonIgnore]
        public string? TxId => !string.IsNullOrEmpty(TxnId) ? TxnId : TxIdAlt;
    }

    private class StrikeLightningDetails
    {
        [JsonPropertyName("preImage")]
        public string? PreImage { get; set; }
    }

    private class StrikeBalance
    {
        [JsonPropertyName("currency")]
        public string? Currency { get; set; }

        [JsonPropertyName("current")]
        public string? Current { get; set; }

        [JsonPropertyName("available")]
        public string? Available { get; set; }

        [JsonPropertyName("total")]
        public string? Total { get; set; }

        [JsonPropertyName("pending")]
        public string? Pending { get; set; }

        [JsonPropertyName("reserved")]
        public string? Reserved { get; set; }
    }

    private class StrikeOnChainQuote
    {
        [JsonPropertyName("paymentQuoteId")]
        public string? PaymentQuoteId { get; set; }

        [JsonPropertyName("onchainFee")]
        public StrikeAmount? OnchainFee { get; set; }
    }

    private class StrikeExchangeQuote
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("sourceAmount")]
        public StrikeAmount? SourceAmount { get; set; }

        [JsonPropertyName("targetAmount")]
        public StrikeAmount? TargetAmount { get; set; }

        [JsonPropertyName("fee")]
        public StrikeAmount? Fee { get; set; }
    }

    private class StrikeAmount
    {
        [JsonPropertyName("amount")]
        public string? Amount { get; set; }

        [JsonPropertyName("currency")]
        public string? Currency { get; set; }
    }

    private class StrikeReceiveResponse
    {
        [JsonPropertyName("receiveRequestId")]
        public string? ReceiveRequestId { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("bolt11")]
        public StrikeBolt11Response? Bolt11 { get; set; }

        [JsonPropertyName("completedAt")]
        public DateTime? CompletedAt { get; set; }
    }

    private class StrikeBolt11Response
    {
        [JsonPropertyName("invoice")]
        public string? Invoice { get; set; }

        [JsonPropertyName("btcAmount")]
        public string? BtcAmount { get; set; }

        [JsonPropertyName("requestedAmount")]
        public StrikeAmount? RequestedAmount { get; set; }

        [JsonPropertyName("expires")]
        public DateTime? ExpiresAt { get; set; }
    }

    private class StrikeTicker
    {
        [JsonPropertyName("amount")]
        public string? Amount { get; set; }

        [JsonPropertyName("sourceCurrency")]
        public string? SourceCurrency { get; set; }

        [JsonPropertyName("targetCurrency")]
        public string? TargetCurrency { get; set; }
    }

    #endregion
}

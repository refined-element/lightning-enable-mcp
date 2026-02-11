using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Lightning payment provider using Strike's REST API.
/// Strike returns preimage via lightning.preImage on execute-payment-quote response.
/// Full L402 protocol support enabled.
/// </summary>
public class StrikePaymentProvider : ILightningPaymentProvider, IDisposable
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

    public StrikePaymentProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _apiKey = Environment.GetEnvironmentVariable("STRIKE_API_KEY");

        if (IsConfigured)
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _apiKey);
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    /// <summary>
    /// Provider name for identification.
    /// </summary>
    public string Name => "Strike";

    /// <summary>
    /// Strike now returns preimage for outgoing Lightning payments via
    /// the lightning.preImage property on the execute-payment-quote response.
    /// This enables full L402 protocol support.
    /// </summary>
    public bool SupportsPreimage => true;

    /// <summary>
    /// Whether the API key is configured.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// Pays a BOLT11 Lightning invoice.
    /// Strike returns preimage via the lightning.preImage property on execute-payment-quote.
    /// </summary>
    public async Task<ProviderPaymentResult> PayInvoiceAsync(
        string bolt11Invoice,
        long? maxFeeSats = null,
        int timeoutSecs = 60,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return ProviderPaymentResult.Failed("NOT_CONFIGURED",
                "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }

        try
        {
            // Step 1: Create payment quote
            var quoteRequest = new StrikePaymentQuoteRequest
            {
                LnInvoice = bolt11Invoice,
                SourceCurrency = "USD" // Default to USD, could be configurable
            };

            var quoteResponse = await _httpClient.PostAsJsonAsync(
                $"{BaseUrl}/payment-quotes/lightning",
                quoteRequest,
                JsonOptions,
                cancellationToken);

            if (!quoteResponse.IsSuccessStatusCode)
            {
                var errorBody = await quoteResponse.Content.ReadAsStringAsync(cancellationToken);
                return ProviderPaymentResult.Failed(
                    $"HTTP_{(int)quoteResponse.StatusCode}",
                    $"Failed to create payment quote: {errorBody}");
            }

            var quote = await quoteResponse.Content.ReadFromJsonAsync<StrikePaymentQuote>(
                JsonOptions, cancellationToken);

            if (quote == null || string.IsNullOrEmpty(quote.PaymentQuoteId))
            {
                return ProviderPaymentResult.Failed("INVALID_QUOTE", "No payment quote ID returned");
            }

            Console.Error.WriteLine($"[Strike] Created payment quote: {quote.PaymentQuoteId}");

            // Step 2: Execute the payment quote
            var executeResponse = await _httpClient.PatchAsync(
                $"{BaseUrl}/payment-quotes/{quote.PaymentQuoteId}/execute",
                null,
                cancellationToken);

            if (!executeResponse.IsSuccessStatusCode)
            {
                var errorBody = await executeResponse.Content.ReadAsStringAsync(cancellationToken);
                return ProviderPaymentResult.Failed(
                    $"HTTP_{(int)executeResponse.StatusCode}",
                    $"Failed to execute payment: {errorBody}");
            }

            var payment = await executeResponse.Content.ReadFromJsonAsync<StrikePayment>(
                JsonOptions, cancellationToken);

            if (payment == null)
            {
                return ProviderPaymentResult.Failed("INVALID_PAYMENT", "No payment returned");
            }

            Console.Error.WriteLine($"[Strike] Payment executed: {payment.PaymentId}, state: {payment.State}");

            // Step 3: If pending, poll for completion
            if (payment.State == "PENDING")
            {
                payment = await WaitForPaymentCompletion(
                    payment.PaymentId,
                    timeoutSecs,
                    cancellationToken);
            }

            if (payment.State == "COMPLETED")
            {
                var preimage = payment.Lightning?.Preimage;
                var feeSats = payment.Lightning?.NetworkFee?.Amount != null
                    ? ConvertToSats(payment.Lightning.NetworkFee.Amount, payment.Lightning.NetworkFee.Currency)
                    : (long?)null;

                if (!string.IsNullOrEmpty(preimage))
                {
                    Console.Error.WriteLine($"[Strike] Preimage received: {preimage[..Math.Min(8, preimage.Length)]}...");
                }

                return ProviderPaymentResult.Succeeded(
                    preimage: preimage,
                    paymentId: payment.PaymentId,
                    feeSats: feeSats);
            }

            return ProviderPaymentResult.Failed(payment.State ?? "UNKNOWN", "Payment did not complete");
        }
        catch (TaskCanceledException)
        {
            return ProviderPaymentResult.Failed("TIMEOUT", "Payment request timed out");
        }
        catch (HttpRequestException ex)
        {
            return ProviderPaymentResult.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return ProviderPaymentResult.Failed("JSON_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Creates a Lightning invoice to receive payment.
    /// Uses receive-requests endpoint for simpler flow.
    /// </summary>
    public async Task<ProviderInvoiceResult> CreateInvoiceAsync(
        long amountSats,
        string? memo = null,
        int expirySeconds = 3600,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return ProviderInvoiceResult.Failed("NOT_CONFIGURED",
                "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }

        try
        {
            // Convert sats to BTC for Strike API
            var amountBtc = amountSats / 100_000_000m;

            var request = new StrikeReceiveRequest
            {
                Bolt11 = new StrikeBolt11Request
                {
                    Amount = new StrikeAmount
                    {
                        Amount = amountBtc.ToString("F8"),
                        Currency = "BTC"
                    },
                    Description = memo,
                    ExpiryInSeconds = expirySeconds
                },
                TargetCurrency = "BTC" // Receive as BTC
            };

            var response = await _httpClient.PostAsJsonAsync(
                $"{BaseUrl}/receive-requests",
                request,
                JsonOptions,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                return ProviderInvoiceResult.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    $"Failed to create receive request: {errorBody}");
            }

            var receiveRequest = await response.Content.ReadFromJsonAsync<StrikeReceiveResponse>(
                JsonOptions, cancellationToken);

            if (receiveRequest?.Bolt11?.Invoice == null)
            {
                return ProviderInvoiceResult.Failed("INVALID_RESPONSE", "No invoice returned");
            }

            // Extract payment hash from the invoice if possible
            // Strike doesn't return it directly, so we'd need to parse BOLT11
            // For now, use the receive request ID as a reference
            var paymentHash = receiveRequest.ReceiveRequestId ?? "";

            return ProviderInvoiceResult.Succeeded(
                invoiceId: receiveRequest.ReceiveRequestId ?? "",
                bolt11: receiveRequest.Bolt11.Invoice,
                paymentHash: paymentHash,
                amountSats: amountSats,
                expiresAt: receiveRequest.Bolt11.ExpiresAt);
        }
        catch (HttpRequestException ex)
        {
            return ProviderInvoiceResult.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return ProviderInvoiceResult.Failed("JSON_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Gets the account balance.
    /// </summary>
    public async Task<ProviderBalanceResult> GetBalanceAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return ProviderBalanceResult.Failed("NOT_CONFIGURED",
                "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }

        try
        {
            var response = await _httpClient.GetAsync($"{BaseUrl}/balances", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                return ProviderBalanceResult.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    $"Failed to get balance: {errorBody}");
            }

            var balances = await response.Content.ReadFromJsonAsync<List<StrikeBalance>>(
                JsonOptions, cancellationToken);

            if (balances == null || balances.Count == 0)
            {
                return ProviderBalanceResult.Succeeded(0);
            }

            // Find BTC balance, or sum all balances converted to sats
            long totalSats = 0;
            foreach (var balance in balances)
            {
                if (balance.Currency?.ToUpperInvariant() == "BTC" && balance.Current != null)
                {
                    totalSats += ConvertToSats(balance.Current.Amount, "BTC");
                }
            }

            return ProviderBalanceResult.Succeeded(totalSats);
        }
        catch (HttpRequestException ex)
        {
            return ProviderBalanceResult.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return ProviderBalanceResult.Failed("JSON_ERROR", ex.Message);
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
            _ => 0 // Unknown currency, can't convert
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }

    #region Request/Response Models

    private class StrikePaymentQuoteRequest
    {
        [JsonPropertyName("lnInvoice")]
        public string LnInvoice { get; set; } = "";

        [JsonPropertyName("sourceCurrency")]
        public string SourceCurrency { get; set; } = "USD";

        [JsonPropertyName("amount")]
        public StrikeAmount? Amount { get; set; }
    }

    private class StrikePaymentQuote
    {
        [JsonPropertyName("paymentQuoteId")]
        public string? PaymentQuoteId { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("lnInvoice")]
        public string? LnInvoice { get; set; }

        [JsonPropertyName("sourceCurrency")]
        public string? SourceCurrency { get; set; }

        [JsonPropertyName("targetCurrency")]
        public string? TargetCurrency { get; set; }

        [JsonPropertyName("sourceAmount")]
        public StrikeAmount? SourceAmount { get; set; }

        [JsonPropertyName("targetAmount")]
        public StrikeAmount? TargetAmount { get; set; }

        [JsonPropertyName("lightningNetworkFee")]
        public StrikeAmount? LightningNetworkFee { get; set; }

        [JsonPropertyName("conversionRate")]
        public StrikeConversionRate? ConversionRate { get; set; }

        [JsonPropertyName("expiration")]
        public DateTime? Expiration { get; set; }
    }

    private class StrikePayment
    {
        [JsonPropertyName("paymentId")]
        public string PaymentId { get; set; } = "";

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("created")]
        public DateTime? Created { get; set; }

        [JsonPropertyName("completed")]
        public DateTime? Completed { get; set; }

        [JsonPropertyName("sourceAmount")]
        public StrikeAmount? SourceAmount { get; set; }

        [JsonPropertyName("targetAmount")]
        public StrikeAmount? TargetAmount { get; set; }

        [JsonPropertyName("conversionRate")]
        public StrikeConversionRate? ConversionRate { get; set; }

        [JsonPropertyName("lightning")]
        public StrikeLightningDetails? Lightning { get; set; }

        // Strike now returns preimage via lightning.preImage on execute-payment-quote
    }

    private class StrikeLightningDetails
    {
        [JsonPropertyName("networkFee")]
        public StrikeAmount? NetworkFee { get; set; }

        // For incoming payments only:
        [JsonPropertyName("preimage")]
        public string? Preimage { get; set; }

        [JsonPropertyName("paymentHash")]
        public string? PaymentHash { get; set; }
    }

    private class StrikeReceiveRequest
    {
        [JsonPropertyName("bolt11")]
        public StrikeBolt11Request? Bolt11 { get; set; }

        [JsonPropertyName("targetCurrency")]
        public string? TargetCurrency { get; set; }
    }

    private class StrikeBolt11Request
    {
        [JsonPropertyName("amount")]
        public StrikeAmount? Amount { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("expiryInSeconds")]
        public int ExpiryInSeconds { get; set; } = 3600;
    }

    private class StrikeReceiveResponse
    {
        [JsonPropertyName("receiveRequestId")]
        public string? ReceiveRequestId { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("bolt11")]
        public StrikeBolt11Response? Bolt11 { get; set; }

        [JsonPropertyName("targetCurrency")]
        public string? TargetCurrency { get; set; }
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

    private class StrikeBalance
    {
        [JsonPropertyName("currency")]
        public string? Currency { get; set; }

        [JsonPropertyName("current")]
        public StrikeAmount? Current { get; set; }

        [JsonPropertyName("pending")]
        public StrikeAmount? Pending { get; set; }

        [JsonPropertyName("reserved")]
        public StrikeAmount? Reserved { get; set; }
    }

    private class StrikeAmount
    {
        [JsonPropertyName("amount")]
        public string? Amount { get; set; }

        [JsonPropertyName("currency")]
        public string? Currency { get; set; }
    }

    private class StrikeConversionRate
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

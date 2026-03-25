using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Banking service using Strike's REST API.
/// Supports bank payment methods, payouts (withdrawals), and deposits.
///
/// Configuration: Set STRIKE_API_KEY environment variable.
/// Get your API key from: https://dashboard.strike.me/
/// </summary>
public class StrikeBankingService : IStrikeBankingService, IDisposable
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

    public StrikeBankingService(HttpClient httpClient, IBudgetConfigurationService? budgetConfigService = null)
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
                Console.Error.WriteLine("[Strike Banking] Using API key from config file");
            }
        }

        Console.Error.WriteLine($"[Strike Banking] Initializing service. API key configured: {IsConfigured}");
        if (IsConfigured)
        {
            Console.Error.WriteLine("[Strike Banking] API key configured");
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _apiKey);
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            Console.Error.WriteLine("[Strike Banking] Authorization header set");
        }
        else
        {
            Console.Error.WriteLine("[Strike Banking] WARNING: STRIKE_API_KEY not found in environment!");
        }
    }

    /// <summary>
    /// Whether the Strike API key is configured.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    #region Payment Methods

    /// <summary>
    /// Creates a bank payment method (ACH or wire) linked to the Strike account.
    /// </summary>
    public async Task<BankPaymentMethodResult> CreateBankPaymentMethodAsync(
        CreateBankPaymentMethodRequest request, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return BankPaymentMethodResult.Failed("NOT_CONFIGURED",
                "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }

        try
        {
            var apiRequest = new
            {
                transferType = request.TransferType,
                accountNumber = request.AccountNumber,
                routingNumber = request.RoutingNumber,
                accountType = request.AccountType,
                bankName = request.BankName,
                beneficiaries = request.Beneficiaries?.Select(b => new
                {
                    type = b.Type,
                    name = b.Name,
                    address = b.Address != null ? new
                    {
                        country = b.Address.Country,
                        state = b.Address.State,
                        city = b.Address.City,
                        postCode = b.Address.PostalCode,
                        line1 = b.Address.Line1
                    } : (object?)null
                }).ToArray()
            };

            Console.Error.WriteLine($"[Strike Banking] Creating bank payment method ({request.TransferType})...");

            var response = await _httpClient.PostAsJsonAsync(
                $"{BaseUrl}/payment-methods/bank", apiRequest, JsonOptions, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine($"[Strike Banking] Create payment method failed: {errorBody}");
                return BankPaymentMethodResult.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    $"Failed to create bank payment method: {errorBody}");
            }

            var result = await response.Content.ReadFromJsonAsync<StrikeBankPaymentMethodResponse>(
                JsonOptions, ct);

            if (result == null || string.IsNullOrEmpty(result.Id))
            {
                return BankPaymentMethodResult.Failed("INVALID_RESPONSE",
                    "No payment method ID returned");
            }

            Console.Error.WriteLine($"[Strike Banking] Payment method created: {result.Id}");

            return BankPaymentMethodResult.Succeeded(
                result.Id ?? "",
                result.TransferType ?? "",
                null,
                result.State);
        }
        catch (TaskCanceledException)
        {
            return BankPaymentMethodResult.Failed("TIMEOUT", "Request timed out");
        }
        catch (HttpRequestException ex)
        {
            return BankPaymentMethodResult.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return BankPaymentMethodResult.Failed("JSON_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Lists all bank payment methods on the Strike account.
    /// </summary>
    public async Task<ListBankPaymentMethodsResult> ListBankPaymentMethodsAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return ListBankPaymentMethodsResult.Failed("NOT_CONFIGURED",
                "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }

        try
        {
            Console.Error.WriteLine("[Strike Banking] Listing bank payment methods...");

            var response = await _httpClient.GetAsync(
                $"{BaseUrl}/payment-methods/bank", ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine($"[Strike Banking] List payment methods failed: {errorBody}");
                return ListBankPaymentMethodsResult.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    $"Failed to list bank payment methods: {errorBody}");
            }

            var items = await response.Content.ReadFromJsonAsync<List<StrikeBankPaymentMethodResponse>>(
                JsonOptions, ct);

            var methods = items?.Select(m => new BankPaymentMethodInfo
            {
                Id = m.Id ?? "",
                TransferType = m.TransferType ?? "",
                AccountNumber = m.AccountNumber,
                State = m.State,
                Currency = m.Currency
            }).ToList() ?? new List<BankPaymentMethodInfo>();

            Console.Error.WriteLine($"[Strike Banking] Found {methods.Count} bank payment methods");

            return ListBankPaymentMethodsResult.Succeeded(methods);
        }
        catch (TaskCanceledException)
        {
            return ListBankPaymentMethodsResult.Failed("TIMEOUT", "Request timed out");
        }
        catch (HttpRequestException ex)
        {
            return ListBankPaymentMethodsResult.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return ListBankPaymentMethodsResult.Failed("JSON_ERROR", ex.Message);
        }
    }

    #endregion

    #region Payout Originators

    /// <summary>
    /// Creates a payout originator (beneficiary entity for payouts).
    /// </summary>
    public async Task<PayoutOriginatorResult> CreatePayoutOriginatorAsync(
        CreatePayoutOriginatorRequest request, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return PayoutOriginatorResult.Failed("NOT_CONFIGURED",
                "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }

        try
        {
            var apiRequest = new
            {
                type = request.Type,
                name = request.Name,
                address = request.Address != null ? new
                {
                    country = request.Address.Country,
                    state = request.Address.State,
                    city = request.Address.City,
                    postCode = request.Address.PostalCode,
                    line1 = request.Address.Line1
                } : (object?)null
            };

            Console.Error.WriteLine($"[Strike Banking] Creating payout originator ({request.Name})...");

            var response = await _httpClient.PostAsJsonAsync(
                $"{BaseUrl}/payout-originators", apiRequest, JsonOptions, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine($"[Strike Banking] Create payout originator failed: {errorBody}");
                return PayoutOriginatorResult.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    $"Failed to create payout originator: {errorBody}");
            }

            var result = await response.Content.ReadFromJsonAsync<StrikePayoutOriginatorResponse>(
                JsonOptions, ct);

            if (result == null || string.IsNullOrEmpty(result.Id))
            {
                return PayoutOriginatorResult.Failed("INVALID_RESPONSE",
                    "No payout originator ID returned");
            }

            Console.Error.WriteLine($"[Strike Banking] Payout originator created: {result.Id}");

            return PayoutOriginatorResult.Succeeded(
                result.Id,
                result.State,
                result.Name);
        }
        catch (TaskCanceledException)
        {
            return PayoutOriginatorResult.Failed("TIMEOUT", "Request timed out");
        }
        catch (HttpRequestException ex)
        {
            return PayoutOriginatorResult.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return PayoutOriginatorResult.Failed("JSON_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Lists all payout originators on the Strike account.
    /// </summary>
    public async Task<ListPayoutOriginatorsResult> ListPayoutOriginatorsAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return ListPayoutOriginatorsResult.Failed("NOT_CONFIGURED",
                "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }

        try
        {
            Console.Error.WriteLine("[Strike Banking] Listing payout originators...");

            var response = await _httpClient.GetAsync(
                $"{BaseUrl}/payout-originators", ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine($"[Strike Banking] List payout originators failed: {errorBody}");
                return ListPayoutOriginatorsResult.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    $"Failed to list payout originators: {errorBody}");
            }

            var items = await response.Content.ReadFromJsonAsync<List<StrikePayoutOriginatorResponse>>(
                JsonOptions, ct);

            var originators = items?.Select(o => new PayoutOriginatorInfo
            {
                Id = o.Id ?? "",
                Type = o.Type,
                Name = o.Name,
                State = o.State
            }).ToList() ?? new List<PayoutOriginatorInfo>();

            Console.Error.WriteLine($"[Strike Banking] Found {originators.Count} payout originators");

            return ListPayoutOriginatorsResult.Succeeded(originators);
        }
        catch (TaskCanceledException)
        {
            return ListPayoutOriginatorsResult.Failed("TIMEOUT", "Request timed out");
        }
        catch (HttpRequestException ex)
        {
            return ListPayoutOriginatorsResult.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return ListPayoutOriginatorsResult.Failed("JSON_ERROR", ex.Message);
        }
    }

    #endregion

    #region Payouts

    /// <summary>
    /// Creates a payout (withdrawal to bank account). Must be initiated separately.
    /// </summary>
    public async Task<PayoutResult> CreatePayoutAsync(
        CreatePayoutRequest request, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return PayoutResult.Failed("NOT_CONFIGURED",
                "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }

        try
        {
            var apiRequest = new
            {
                payoutOriginatorId = request.PayoutOriginatorId,
                paymentMethodId = request.PaymentMethodId,
                amount = new { amount = request.Amount, currency = request.Currency },
                feePolicy = request.FeePolicy
            };

            Console.Error.WriteLine($"[Strike Banking] Creating payout ({request.Amount} {request.Currency})...");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/payouts");
            httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            httpRequest.Content = JsonContent.Create(apiRequest, options: JsonOptions);

            var response = await _httpClient.SendAsync(httpRequest, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine($"[Strike Banking] Create payout failed: {errorBody}");
                return PayoutResult.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    $"Failed to create payout: {errorBody}");
            }

            var result = await response.Content.ReadFromJsonAsync<StrikePayoutResponse>(
                JsonOptions, ct);

            if (result == null || string.IsNullOrEmpty(result.Id))
            {
                return PayoutResult.Failed("INVALID_RESPONSE", "No payout ID returned");
            }

            Console.Error.WriteLine($"[Strike Banking] Payout created: {result.Id}");

            return PayoutResult.Succeeded(
                result.Id,
                result.State,
                result.Amount?.Amount,
                result.Amount?.Currency,
                result.Fee?.Amount);
        }
        catch (TaskCanceledException)
        {
            return PayoutResult.Failed("TIMEOUT", "Request timed out");
        }
        catch (HttpRequestException ex)
        {
            return PayoutResult.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return PayoutResult.Failed("JSON_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Initiates a previously created payout, triggering the bank transfer.
    /// </summary>
    public async Task<PayoutResult> InitiatePayoutAsync(string payoutId, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return PayoutResult.Failed("NOT_CONFIGURED",
                "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }

        if (string.IsNullOrWhiteSpace(payoutId))
        {
            return PayoutResult.Failed("INVALID_PAYOUT_ID", "Payout ID is required");
        }

        try
        {
            Console.Error.WriteLine($"[Strike Banking] Initiating payout: {payoutId}...");

            var response = await _httpClient.PatchAsync(
                $"{BaseUrl}/payouts/{payoutId}/initiate", null, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine($"[Strike Banking] Initiate payout failed: {errorBody}");
                return PayoutResult.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    $"Failed to initiate payout: {errorBody}");
            }

            var result = await response.Content.ReadFromJsonAsync<StrikePayoutResponse>(
                JsonOptions, ct);

            if (result == null)
            {
                return PayoutResult.Failed("INVALID_RESPONSE", "No payout returned");
            }

            Console.Error.WriteLine($"[Strike Banking] Payout initiated: {result.Id}, state: {result.State}");

            return PayoutResult.Succeeded(
                result.Id ?? payoutId,
                result.State,
                result.Amount?.Amount,
                result.Amount?.Currency,
                result.Fee?.Amount);
        }
        catch (TaskCanceledException)
        {
            return PayoutResult.Failed("TIMEOUT", "Request timed out");
        }
        catch (HttpRequestException ex)
        {
            return PayoutResult.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return PayoutResult.Failed("JSON_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Lists all payouts on the Strike account.
    /// </summary>
    public async Task<ListPayoutsResult> ListPayoutsAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return ListPayoutsResult.Failed("NOT_CONFIGURED",
                "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }

        try
        {
            Console.Error.WriteLine("[Strike Banking] Listing payouts...");

            var response = await _httpClient.GetAsync(
                $"{BaseUrl}/payouts", ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine($"[Strike Banking] List payouts failed: {errorBody}");
                return ListPayoutsResult.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    $"Failed to list payouts: {errorBody}");
            }

            // Strike may return items[] wrapper or direct array
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            List<StrikePayoutResponse>? items;

            try
            {
                // Try paginated response first
                var paginated = JsonSerializer.Deserialize<StrikePaginatedResponse<StrikePayoutResponse>>(
                    responseBody, JsonOptions);
                items = paginated?.Items;
            }
            catch
            {
                // Fall back to direct array
                items = JsonSerializer.Deserialize<List<StrikePayoutResponse>>(
                    responseBody, JsonOptions);
            }

            var payouts = items?.Select(p => new PayoutInfo
            {
                Id = p.Id ?? "",
                State = p.State,
                Amount = p.Amount?.Amount,
                Currency = p.Amount?.Currency,
                Fee = p.Fee?.Amount,
                Created = p.Created
            }).ToList() ?? new List<PayoutInfo>();

            Console.Error.WriteLine($"[Strike Banking] Found {payouts.Count} payouts");

            return ListPayoutsResult.Succeeded(payouts);
        }
        catch (TaskCanceledException)
        {
            return ListPayoutsResult.Failed("TIMEOUT", "Request timed out");
        }
        catch (HttpRequestException ex)
        {
            return ListPayoutsResult.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return ListPayoutsResult.Failed("JSON_ERROR", ex.Message);
        }
    }

    #endregion

    #region Deposits

    /// <summary>
    /// Initiates a deposit (pull funds from bank into Strike account).
    /// </summary>
    public async Task<DepositResult> InitiateDepositAsync(
        InitiateDepositRequest request, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return DepositResult.Failed("NOT_CONFIGURED",
                "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }

        try
        {
            var apiRequest = new
            {
                paymentMethodId = request.PaymentMethodId,
                amount = request.Amount,
                feePolicy = request.FeePolicy
            };

            Console.Error.WriteLine($"[Strike Banking] Initiating deposit ({request.Amount})...");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/deposits");
            httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            httpRequest.Content = JsonContent.Create(apiRequest, options: JsonOptions);

            var response = await _httpClient.SendAsync(httpRequest, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine($"[Strike Banking] Initiate deposit failed: {errorBody}");
                return DepositResult.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    $"Failed to initiate deposit: {errorBody}");
            }

            var result = await response.Content.ReadFromJsonAsync<StrikeDepositResponse>(
                JsonOptions, ct);

            if (result == null || string.IsNullOrEmpty(result.Id))
            {
                return DepositResult.Failed("INVALID_RESPONSE", "No deposit ID returned");
            }

            Console.Error.WriteLine($"[Strike Banking] Deposit initiated: {result.Id}");

            return DepositResult.Succeeded(
                result.Id,
                result.State,
                result.Amount?.Amount,
                result.Amount?.Currency,
                result.Fee?.Amount);
        }
        catch (TaskCanceledException)
        {
            return DepositResult.Failed("TIMEOUT", "Request timed out");
        }
        catch (HttpRequestException ex)
        {
            return DepositResult.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return DepositResult.Failed("JSON_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Estimates the fee for a deposit before initiating it.
    /// </summary>
    public async Task<DepositFeeResult> EstimateDepositFeeAsync(
        EstimateDepositFeeRequest request, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return DepositFeeResult.Failed("NOT_CONFIGURED",
                "Strike not configured. Set STRIKE_API_KEY environment variable.");
        }

        try
        {
            var apiRequest = new
            {
                paymentMethodId = request.PaymentMethodId,
                amount = request.Amount
            };

            Console.Error.WriteLine($"[Strike Banking] Estimating deposit fee ({request.Amount})...");

            var response = await _httpClient.PostAsJsonAsync(
                $"{BaseUrl}/deposits/fee", apiRequest, JsonOptions, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine($"[Strike Banking] Estimate deposit fee failed: {errorBody}");
                return DepositFeeResult.Failed(
                    $"HTTP_{(int)response.StatusCode}",
                    $"Failed to estimate deposit fee: {errorBody}");
            }

            var result = await response.Content.ReadFromJsonAsync<StrikeDepositFeeResponse>(
                JsonOptions, ct);

            if (result == null)
            {
                return DepositFeeResult.Failed("INVALID_RESPONSE", "No fee estimate returned");
            }

            Console.Error.WriteLine($"[Strike Banking] Fee estimate: {result.Fee?.Amount} {result.Fee?.Currency}");

            return DepositFeeResult.Succeeded(
                result.Fee?.Amount ?? "0",
                result.Fee?.Currency ?? "",
                result.TotalAmount?.Amount);
        }
        catch (TaskCanceledException)
        {
            return DepositFeeResult.Failed("TIMEOUT", "Request timed out");
        }
        catch (HttpRequestException ex)
        {
            return DepositFeeResult.Failed("HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return DepositFeeResult.Failed("JSON_ERROR", ex.Message);
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Parses an amount from a Strike API amount object.
    /// </summary>
    private static string? ParseAmount(StrikeAmountResponse? amount)
    {
        return amount?.Amount;
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }

    #endregion

    #region Strike API Models

    private class StrikePaginatedResponse<T>
    {
        [JsonPropertyName("items")]
        public List<T>? Items { get; set; }

        [JsonPropertyName("count")]
        public int? Count { get; set; }
    }

    #endregion
}

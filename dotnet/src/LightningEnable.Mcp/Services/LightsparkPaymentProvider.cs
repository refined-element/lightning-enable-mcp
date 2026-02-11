using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Lightning payment provider using Lightspark's GraphQL API.
/// CRITICAL: Lightspark returns payment preimages, making it ideal for L402 authentication.
/// </summary>
public class LightsparkPaymentProvider : ILightningPaymentProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string? _clientId;
    private readonly string? _clientSecret;
    private readonly string? _nodeId;
    private readonly string _environment;
    private bool _disposed;

    private const string GraphQLEndpoint = "https://api.lightspark.com/graphql";

    // JSON serialization options for GraphQL requests/responses
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public LightsparkPaymentProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _clientId = Environment.GetEnvironmentVariable("LIGHTSPARK_CLIENT_ID");
        _clientSecret = Environment.GetEnvironmentVariable("LIGHTSPARK_CLIENT_SECRET");
        _nodeId = Environment.GetEnvironmentVariable("LIGHTSPARK_NODE_ID");
        _environment = Environment.GetEnvironmentVariable("LIGHTSPARK_ENVIRONMENT") ?? "production";

        if (IsConfigured)
        {
            // Set up Basic auth with client_id:client_secret
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    /// <summary>
    /// Provider name for identification.
    /// </summary>
    public string Name => "Lightspark";

    /// <summary>
    /// CRITICAL: Lightspark returns payment preimages, enabling L402 authentication.
    /// This is the primary reason for using Lightspark over providers that don't return preimages.
    /// </summary>
    public bool SupportsPreimage => true;

    /// <summary>
    /// Whether all required environment variables are set.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrEmpty(_clientId) &&
        !string.IsNullOrEmpty(_clientSecret) &&
        !string.IsNullOrEmpty(_nodeId);

    /// <summary>
    /// Pays a BOLT11 Lightning invoice and returns the preimage.
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
                "Lightspark not configured. Set LIGHTSPARK_CLIENT_ID, LIGHTSPARK_CLIENT_SECRET, and LIGHTSPARK_NODE_ID environment variables.");
        }

        try
        {
            var request = new GraphQLRequest
            {
                Query = @"
                    mutation PayInvoice($node_id: ID!, $encoded_invoice: String!, $timeout_secs: Int!) {
                        pay_invoice(input: {
                            node_id: $node_id
                            encoded_invoice: $encoded_invoice
                            timeout_secs: $timeout_secs
                        }) {
                            payment {
                                id
                                status
                                payment_preimage
                                amount { value currency_amount_preferred_currency_unit }
                                fees { value currency_amount_preferred_currency_unit }
                            }
                        }
                    }",
                Variables = new Dictionary<string, object?>
                {
                    ["node_id"] = _nodeId,
                    ["encoded_invoice"] = bolt11Invoice,
                    ["timeout_secs"] = timeoutSecs
                }
            };

            var response = await ExecuteGraphQLAsync<PayInvoiceResponse>(request, cancellationToken);

            if (response == null)
            {
                return ProviderPaymentResult.Failed("NULL_RESPONSE", "Lightspark returned null response");
            }

            if (response.Errors?.Count > 0)
            {
                var error = response.Errors[0];
                return ProviderPaymentResult.Failed(
                    error.Extensions?.Code ?? "GRAPHQL_ERROR",
                    error.Message ?? "Unknown GraphQL error");
            }

            var payment = response.Data?.PayInvoice?.Payment;
            if (payment == null)
            {
                return ProviderPaymentResult.Failed("NO_PAYMENT", "Payment object not returned in response");
            }

            // Check payment status
            var status = payment.Status?.ToUpperInvariant();
            if (status != "COMPLETED" && status != "SUCCEEDED" && status != "SUCCESS")
            {
                return ProviderPaymentResult.Failed("PAYMENT_FAILED", $"Payment status: {payment.Status}");
            }

            // CRITICAL: Extract the preimage - this is the whole reason for using Lightspark!
            var preimage = payment.PaymentPreimage;
            if (string.IsNullOrEmpty(preimage))
            {
                return ProviderPaymentResult.Failed("NO_PREIMAGE",
                    "Payment succeeded but no preimage returned. This should not happen with Lightspark.");
            }

            // Parse fees if available
            long? feeSats = null;
            if (payment.Fees?.Value != null && long.TryParse(payment.Fees.Value, out var feeValue))
            {
                feeSats = ConvertToSats(feeValue, payment.Fees.Unit);
            }

            return ProviderPaymentResult.Succeeded(preimage, payment.Id, feeSats);
        }
        catch (HttpRequestException ex)
        {
            return ProviderPaymentResult.Failed("HTTP_ERROR", $"HTTP request failed: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return ProviderPaymentResult.Failed("JSON_ERROR", $"Failed to parse response: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return ProviderPaymentResult.Failed("TIMEOUT", "Payment request timed out");
        }
        catch (Exception ex)
        {
            return ProviderPaymentResult.Failed("EXCEPTION", $"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the node's Lightning balance.
    /// </summary>
    public async Task<ProviderBalanceResult> GetBalanceAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return ProviderBalanceResult.Failed("NOT_CONFIGURED",
                "Lightspark not configured. Set required environment variables.");
        }

        try
        {
            var request = new GraphQLRequest
            {
                Query = @"
                    query GetNodeBalance($node_id: ID!) {
                        entity(id: $node_id) {
                            ... on LightsparkNode {
                                balances {
                                    owned_balance {
                                        original_value
                                        original_unit
                                    }
                                    available_to_send_balance {
                                        original_value
                                        original_unit
                                    }
                                }
                            }
                        }
                    }",
                Variables = new Dictionary<string, object?>
                {
                    ["node_id"] = _nodeId
                }
            };

            var response = await ExecuteGraphQLAsync<GetBalanceResponse>(request, cancellationToken);

            if (response == null)
            {
                return ProviderBalanceResult.Failed("NULL_RESPONSE", "Lightspark returned null response");
            }

            if (response.Errors?.Count > 0)
            {
                var error = response.Errors[0];
                return ProviderBalanceResult.Failed(
                    error.Extensions?.Code ?? "GRAPHQL_ERROR",
                    error.Message ?? "Unknown GraphQL error");
            }

            var balances = response.Data?.Entity?.Balances;
            if (balances == null)
            {
                return ProviderBalanceResult.Failed("NO_BALANCES", "Balance information not returned");
            }

            // Parse owned balance
            long ownedSats = 0;
            if (balances.OwnedBalance?.OriginalValue != null &&
                long.TryParse(balances.OwnedBalance.OriginalValue, out var ownedValue))
            {
                ownedSats = ConvertToSats(ownedValue, balances.OwnedBalance.OriginalUnit);
            }

            // Parse available to send balance
            long? availableToSendSats = null;
            if (balances.AvailableToSendBalance?.OriginalValue != null &&
                long.TryParse(balances.AvailableToSendBalance.OriginalValue, out var availableValue))
            {
                availableToSendSats = ConvertToSats(availableValue, balances.AvailableToSendBalance.OriginalUnit);
            }

            return ProviderBalanceResult.Succeeded(ownedSats, availableToSendSats);
        }
        catch (HttpRequestException ex)
        {
            return ProviderBalanceResult.Failed("HTTP_ERROR", $"HTTP request failed: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return ProviderBalanceResult.Failed("JSON_ERROR", $"Failed to parse response: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ProviderBalanceResult.Failed("EXCEPTION", $"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a Lightning invoice for receiving payment.
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
                "Lightspark not configured. Set required environment variables.");
        }

        try
        {
            // Lightspark uses millisatoshis
            var amountMsats = amountSats * 1000;

            var request = new GraphQLRequest
            {
                Query = @"
                    mutation CreateInvoice($node_id: ID!, $amount_msats: Long!, $memo: String, $expiry_secs: Int) {
                        create_invoice(input: {
                            node_id: $node_id
                            amount_msats: $amount_msats
                            memo: $memo
                            expiry_secs: $expiry_secs
                        }) {
                            invoice {
                                id
                                data {
                                    encoded_payment_request
                                    payment_hash
                                    expires_at
                                }
                            }
                        }
                    }",
                Variables = new Dictionary<string, object?>
                {
                    ["node_id"] = _nodeId,
                    ["amount_msats"] = amountMsats,
                    ["memo"] = memo,
                    ["expiry_secs"] = expirySeconds
                }
            };

            var response = await ExecuteGraphQLAsync<CreateInvoiceResponse>(request, cancellationToken);

            if (response == null)
            {
                return ProviderInvoiceResult.Failed("NULL_RESPONSE", "Lightspark returned null response");
            }

            if (response.Errors?.Count > 0)
            {
                var error = response.Errors[0];
                return ProviderInvoiceResult.Failed(
                    error.Extensions?.Code ?? "GRAPHQL_ERROR",
                    error.Message ?? "Unknown GraphQL error");
            }

            var invoice = response.Data?.CreateInvoice?.Invoice;
            if (invoice == null)
            {
                return ProviderInvoiceResult.Failed("NO_INVOICE", "Invoice not returned in response");
            }

            var bolt11 = invoice.Data?.EncodedPaymentRequest;
            if (string.IsNullOrEmpty(bolt11))
            {
                return ProviderInvoiceResult.Failed("NO_BOLT11", "BOLT11 payment request not returned");
            }

            // Parse expiry time if available
            DateTime? expiresAt = null;
            if (!string.IsNullOrEmpty(invoice.Data?.ExpiresAt) &&
                DateTime.TryParse(invoice.Data.ExpiresAt, out var parsedExpiry))
            {
                expiresAt = parsedExpiry;
            }

            return ProviderInvoiceResult.Succeeded(
                invoice.Id ?? "unknown",
                bolt11,
                invoice.Data?.PaymentHash ?? "",
                amountSats,
                expiresAt);
        }
        catch (HttpRequestException ex)
        {
            return ProviderInvoiceResult.Failed("HTTP_ERROR", $"HTTP request failed: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return ProviderInvoiceResult.Failed("JSON_ERROR", $"Failed to parse response: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ProviderInvoiceResult.Failed("EXCEPTION", $"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// Executes a GraphQL request against the Lightspark API.
    /// </summary>
    private async Task<GraphQLResponse<T>?> ExecuteGraphQLAsync<T>(
        GraphQLRequest request,
        CancellationToken cancellationToken)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(request, JsonOptions),
            Encoding.UTF8,
            "application/json");

        var httpResponse = await _httpClient.PostAsync(GraphQLEndpoint, content, cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorContent = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            Console.Error.WriteLine($"[Lightspark] HTTP {(int)httpResponse.StatusCode}: {errorContent}");
            return null;
        }

        return await httpResponse.Content.ReadFromJsonAsync<GraphQLResponse<T>>(JsonOptions, cancellationToken);
    }

    /// <summary>
    /// Converts an amount to satoshis based on the unit.
    /// </summary>
    private static long ConvertToSats(long value, string? unit)
    {
        return unit?.ToUpperInvariant() switch
        {
            "SATOSHI" => value,
            "MILLISATOSHI" => value / 1000,
            "BITCOIN" => value * 100_000_000,
            "MILLIBITCOIN" => value * 100_000,
            "MICROBITCOIN" => value * 100,
            _ => value // Assume satoshis if unknown
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }

    #region GraphQL Request/Response Models

    private class GraphQLRequest
    {
        [JsonPropertyName("query")]
        public string Query { get; set; } = "";

        [JsonPropertyName("variables")]
        public Dictionary<string, object?>? Variables { get; set; }
    }

    private class GraphQLResponse<T>
    {
        [JsonPropertyName("data")]
        public T? Data { get; set; }

        [JsonPropertyName("errors")]
        public List<GraphQLError>? Errors { get; set; }
    }

    private class GraphQLError
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("extensions")]
        public GraphQLErrorExtensions? Extensions { get; set; }
    }

    private class GraphQLErrorExtensions
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }
    }

    // Pay Invoice Response Models
    private class PayInvoiceResponse
    {
        [JsonPropertyName("pay_invoice")]
        public PayInvoiceResult? PayInvoice { get; set; }
    }

    private class PayInvoiceResult
    {
        [JsonPropertyName("payment")]
        public PaymentInfo? Payment { get; set; }
    }

    private class PaymentInfo
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("payment_preimage")]
        public string? PaymentPreimage { get; set; }

        [JsonPropertyName("amount")]
        public AmountInfo? Amount { get; set; }

        [JsonPropertyName("fees")]
        public AmountInfo? Fees { get; set; }
    }

    private class AmountInfo
    {
        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("currency_amount_preferred_currency_unit")]
        public string? Unit { get; set; }

        [JsonPropertyName("original_value")]
        public string? OriginalValue { get; set; }

        [JsonPropertyName("original_unit")]
        public string? OriginalUnit { get; set; }
    }

    // Get Balance Response Models
    private class GetBalanceResponse
    {
        [JsonPropertyName("entity")]
        public NodeEntity? Entity { get; set; }
    }

    private class NodeEntity
    {
        [JsonPropertyName("balances")]
        public NodeBalances? Balances { get; set; }
    }

    private class NodeBalances
    {
        [JsonPropertyName("owned_balance")]
        public AmountInfo? OwnedBalance { get; set; }

        [JsonPropertyName("available_to_send_balance")]
        public AmountInfo? AvailableToSendBalance { get; set; }
    }

    // Create Invoice Response Models
    private class CreateInvoiceResponse
    {
        [JsonPropertyName("create_invoice")]
        public CreateInvoiceResult? CreateInvoice { get; set; }
    }

    private class CreateInvoiceResult
    {
        [JsonPropertyName("invoice")]
        public InvoiceInfo? Invoice { get; set; }
    }

    private class InvoiceInfo
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("data")]
        public InvoiceData? Data { get; set; }
    }

    private class InvoiceData
    {
        [JsonPropertyName("encoded_payment_request")]
        public string? EncodedPaymentRequest { get; set; }

        [JsonPropertyName("payment_hash")]
        public string? PaymentHash { get; set; }

        [JsonPropertyName("expires_at")]
        public string? ExpiresAt { get; set; }
    }

    #endregion
}

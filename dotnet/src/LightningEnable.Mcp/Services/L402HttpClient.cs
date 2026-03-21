using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// HTTP client with L402 (Lightning 402) payment support.
/// Automatically handles 402 Payment Required responses by paying via NWC.
/// </summary>
public class L402HttpClient : IL402HttpClient
{
    private readonly HttpClient _httpClient;
    private readonly IWalletService _walletService;
    private readonly IBudgetService _budgetService;
    private readonly IPaymentHistoryService _historyService;

    public L402HttpClient(
        HttpClient httpClient,
        IWalletService walletService,
        IBudgetService budgetService,
        IPaymentHistoryService historyService)
    {
        _httpClient = httpClient;
        // Prevent servers from sending compressed responses that may not decompress correctly
        // across all environments (Docker, bridge CLI, etc.)
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "identity");
        _walletService = walletService;
        _budgetService = budgetService;
        _historyService = historyService;
    }

    public async Task<L402FetchResult> FetchWithL402Async(
        string url,
        string method = "GET",
        string? headers = null,
        string? body = null,
        long maxSats = 1000,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Create initial request
            using var request = CreateRequest(url, method, headers, body);

            // Send request
            var response = await _httpClient.SendAsync(request, cancellationToken);

            // Check for 402 Payment Required
            if (response.StatusCode == HttpStatusCode.PaymentRequired)
            {
                return await HandleL402ChallengeAsync(url, method, headers, body, maxSats, response, cancellationToken);
            }

            // Return result for non-402 responses
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.MediaType;

            if (response.IsSuccessStatusCode)
            {
                return L402FetchResult.Succeeded(url, content, (int)response.StatusCode, contentType);
            }

            return L402FetchResult.Failed(url, $"HTTP {(int)response.StatusCode}: {content}", (int)response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            return L402FetchResult.Failed(url, $"Request failed: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return L402FetchResult.Failed(url, "Request timed out");
        }
        catch (Exception ex)
        {
            return L402FetchResult.Failed(url, $"Unexpected error: {ex.Message}");
        }
    }

    public async Task<string> PayChallengeAsync(
        string? macaroonBase64,
        string invoice,
        long maxSats = 1000,
        CancellationToken cancellationToken = default)
    {
        if (!_walletService.IsConfigured)
        {
            throw new InvalidOperationException("NWC wallet not configured. Set NWC_CONNECTION_STRING environment variable.");
        }

        // Normalize inputs: trim whitespace to prevent invalid tokens/payment failures
        invoice = invoice.Trim();
        macaroonBase64 = macaroonBase64?.Trim();
        var isMpp = string.IsNullOrWhiteSpace(macaroonBase64);

        // Extract amount from invoice
        var amountSats = ExtractAmountFromBolt11(invoice);

        // Reject no-amount invoices (security: could bypass budget checks)
        if (amountSats == null || amountSats.Value <= 0)
        {
            throw new InvalidOperationException("Invoice has no amount specified. For security, only invoices with explicit amounts are supported.");
        }

        // Check budget
        var budgetCheck = _budgetService.CheckBudget(amountSats.Value);
        if (!budgetCheck.Allowed)
        {
            throw new InvalidOperationException($"Budget check failed: {budgetCheck.DenialReason}");
        }

        if (amountSats.Value > maxSats)
        {
            throw new InvalidOperationException($"Invoice amount {amountSats.Value} sats exceeds maximum {maxSats} sats");
        }

        // Pay invoice
        var paymentResult = await _walletService.PayInvoiceAsync(invoice, cancellationToken);

        if (!paymentResult.Success)
        {
            throw new InvalidOperationException($"Payment failed: {paymentResult.ErrorMessage}");
        }

        // Check if preimage is available - both L402 and MPP require it
        if (!paymentResult.HasPreimage)
        {
            var trackingInfo = !string.IsNullOrEmpty(paymentResult.TrackingId)
                ? $" (tracking ID: {paymentResult.TrackingId})"
                : "";
            var protocol = isMpp ? "MPP" : "L402";
            throw new InvalidOperationException(
                $"Payment succeeded{trackingInfo} but wallet did not return preimage. " +
                $"{protocol} requires preimage for verification. Use NWC or LND wallet for L402/MPP support.");
        }

        // Record payment
        _budgetService.RecordSpend(amountSats.Value);

        if (isMpp)
        {
            // MPP mode: return just the preimage
            _historyService.RecordPayment(
                url: "manual_payment",
                method: "MANUAL",
                amountSats: amountSats.Value,
                invoice: invoice,
                preimageHex: paymentResult.PreimageHex,
                l402Token: paymentResult.PreimageHex);

            return paymentResult.PreimageHex;
        }

        // L402 mode: return macaroon:preimage
        var l402Token = $"{macaroonBase64}:{paymentResult.PreimageHex}";
        _historyService.RecordPayment(
            url: "manual_payment",
            method: "MANUAL",
            amountSats: amountSats.Value,
            invoice: invoice,
            preimageHex: paymentResult.PreimageHex,
            l402Token: l402Token);

        return l402Token;
    }

    private async Task<L402FetchResult> HandleL402ChallengeAsync(
        string url,
        string method,
        string? headers,
        string? body,
        long maxSats,
        HttpResponseMessage initialResponse,
        CancellationToken cancellationToken)
    {
        // Parse WWW-Authenticate headers (may contain both L402 and MPP challenges)
        var wwwAuthHeaders = initialResponse.Headers.WwwAuthenticate.Select(h => h.ToString()).ToList();
        var parsed = PaymentChallengeParser.ParseBest(wwwAuthHeaders);

        if (!parsed.HasChallenge)
        {
            return L402FetchResult.Failed(url, "Invalid payment challenge: Could not parse WWW-Authenticate header (expected L402 or Payment scheme)", 402);
        }

        // Check wallet configuration
        if (!_walletService.IsConfigured)
        {
            return L402FetchResult.Failed(url, "402 Payment Required but NWC wallet not configured. Set NWC_CONNECTION_STRING environment variable.", 402);
        }

        // Extract amount from invoice
        var amountSats = ExtractAmountFromBolt11(parsed.Invoice!);

        // Reject no-amount invoices (security: could bypass budget checks)
        if (amountSats == null || amountSats.Value <= 0)
        {
            return L402FetchResult.Failed(url, "Payment invoice has no amount specified. For security, only invoices with explicit amounts are supported.", 402);
        }

        // Check budget
        var budgetCheck = _budgetService.CheckBudget(amountSats.Value);
        if (!budgetCheck.Allowed)
        {
            return L402FetchResult.Failed(url, $"Budget check failed: {budgetCheck.DenialReason}", 402);
        }

        if (amountSats.Value > maxSats)
        {
            return L402FetchResult.Failed(url, $"Invoice amount {amountSats.Value} sats exceeds maximum {maxSats} sats", 402);
        }

        // Pay invoice via wallet
        var paymentResult = await _walletService.PayInvoiceAsync(parsed.Invoice!, cancellationToken);

        if (!paymentResult.Success)
        {
            _historyService.RecordFailedPayment(url, method, amountSats.Value, paymentResult.ErrorMessage ?? "Unknown error", parsed.Invoice!);
            return L402FetchResult.Failed(url, $"Payment failed: {paymentResult.ErrorMessage}", 402);
        }

        // Check if preimage is available - both L402 and MPP require it for verification
        if (!paymentResult.HasPreimage)
        {
            var trackingInfo = !string.IsNullOrEmpty(paymentResult.TrackingId)
                ? $" (tracking ID: {paymentResult.TrackingId})"
                : "";
            var protocolName = parsed.IsMpp ? "MPP" : "L402";
            _historyService.RecordFailedPayment(url, method, amountSats.Value,
                $"Payment succeeded but no preimage returned{trackingInfo}. {protocolName} requires preimage.", parsed.Invoice!);
            return L402FetchResult.Failed(url,
                $"Payment succeeded{trackingInfo} but wallet did not return preimage. " +
                $"{protocolName} requires preimage for verification. Use NWC or LND wallet for L402/MPP support.", 402);
        }

        // Record successful payment
        _budgetService.RecordSpend(amountSats.Value);

        string authToken;
        string protocol;

        if (parsed.IsMpp)
        {
            // MPP: preimage-only authentication
            authToken = paymentResult.PreimageHex;
            protocol = "MPP";
        }
        else
        {
            // L402: macaroon:preimage authentication
            authToken = $"{parsed.L402!.MacaroonBase64}:{paymentResult.PreimageHex}";
            protocol = "L402";
        }

        // Retry request with payment proof
        using var retryRequest = CreateRequest(url, method, headers, body);
        if (parsed.IsMpp)
        {
            // Ensure we do not send multiple Authorization headers: remove any existing one first.
            retryRequest.Headers.Remove("Authorization");
            retryRequest.Headers.TryAddWithoutValidation("Authorization",
                $"Payment method=\"lightning\", preimage=\"{paymentResult.PreimageHex}\"");
        }
        else
        {
            retryRequest.Headers.Authorization = new AuthenticationHeaderValue(parsed.L402!.Scheme, authToken);
        }

        var retryResponse = await _httpClient.SendAsync(retryRequest, cancellationToken);
        var content = await retryResponse.Content.ReadAsStringAsync(cancellationToken);
        var contentType = retryResponse.Content.Headers.ContentType?.MediaType;

        // Record in history
        _historyService.RecordPayment(
            url: url,
            method: method,
            amountSats: amountSats.Value,
            invoice: parsed.Invoice!,
            preimageHex: paymentResult.PreimageHex,
            l402Token: authToken,
            statusCode: (int)retryResponse.StatusCode);

        if (retryResponse.IsSuccessStatusCode)
        {
            return L402FetchResult.Succeeded(url, content, (int)retryResponse.StatusCode, contentType, amountSats.Value, authToken, protocol);
        }

        return L402FetchResult.Failed(url, $"Request failed after payment: HTTP {(int)retryResponse.StatusCode}: {content}", (int)retryResponse.StatusCode, amountSats.Value, authToken, protocol);
    }

    private static HttpRequestMessage CreateRequest(string url, string method, string? headers, string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), url);

        // Add custom headers
        if (!string.IsNullOrEmpty(headers))
        {
            try
            {
                var headerDict = JsonSerializer.Deserialize<Dictionary<string, string>>(headers);
                if (headerDict != null)
                {
                    foreach (var (key, value) in headerDict)
                    {
                        // Skip certain headers that shouldn't be set manually
                        if (!key.Equals("Host", StringComparison.OrdinalIgnoreCase) &&
                            !key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        {
                            request.Headers.TryAddWithoutValidation(key, value);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore invalid JSON headers
            }
        }

        // Add body
        if (!string.IsNullOrEmpty(body))
        {
            // Determine content type from headers or default to application/json
            var contentType = "application/json";
            if (!string.IsNullOrEmpty(headers))
            {
                try
                {
                    var headerDict = JsonSerializer.Deserialize<Dictionary<string, string>>(headers);
                    if (headerDict?.TryGetValue("Content-Type", out var ct) == true)
                    {
                        contentType = ct;
                    }
                }
                catch
                {
                    // Use default
                }
            }

            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        return request;
    }

    /// <summary>
    /// Extracts the amount in satoshis from a BOLT11 invoice.
    /// Returns null if no amount is specified or the invoice format is invalid.
    /// </summary>
    private static long? ExtractAmountFromBolt11(string bolt11)
    {
        if (string.IsNullOrWhiteSpace(bolt11))
            return null;

        // BOLT11 format: lnbc{amount}{multiplier}...
        // Multipliers: m = milli (0.001), u = micro (0.000001), n = nano (0.000000001), p = pico (0.000000000001)

        var invoice = bolt11.ToLowerInvariant();

        // Find the network prefix
        var prefixEnd = 0;
        if (invoice.StartsWith("lnbcrt"))
            prefixEnd = 6; // regtest (check first due to longer prefix)
        else if (invoice.StartsWith("lntbs"))
            prefixEnd = 5; // signet
        else if (invoice.StartsWith("lnbc"))
            prefixEnd = 4; // mainnet
        else if (invoice.StartsWith("lntb"))
            prefixEnd = 4; // testnet
        else
            return null; // Unknown format

        // Extract amount portion (digits followed by optional multiplier)
        var amountPart = new StringBuilder();
        var multiplier = 1.0m;

        for (int i = prefixEnd; i < invoice.Length; i++)
        {
            var c = invoice[i];

            if (char.IsDigit(c))
            {
                amountPart.Append(c);
            }
            else if (c == 'm' || c == 'u' || c == 'n' || c == 'p')
            {
                multiplier = c switch
                {
                    'm' => 0.001m,      // milli-bitcoin = 100,000 sats
                    'u' => 0.000001m,   // micro-bitcoin = 100 sats
                    'n' => 0.000000001m, // nano-bitcoin = 0.1 sats
                    'p' => 0.000000000001m, // pico-bitcoin = 0.0001 sats
                    _ => 1.0m
                };
                break;
            }
            else
            {
                // Reached non-amount character without multiplier
                break;
            }
        }

        // No amount specified in invoice (zero-amount invoice)
        if (amountPart.Length == 0)
            return null;

        if (!decimal.TryParse(amountPart.ToString(), out var amount))
            return null;

        // Amount must be positive
        if (amount <= 0)
            return null;

        // Convert to satoshis
        // 1 BTC = 100,000,000 satoshis
        var btcAmount = amount * multiplier;
        var sats = btcAmount * 100_000_000m;

        var result = (long)Math.Ceiling(sats);

        // Final safety check - ensure positive result
        return result > 0 ? result : null;
    }
}

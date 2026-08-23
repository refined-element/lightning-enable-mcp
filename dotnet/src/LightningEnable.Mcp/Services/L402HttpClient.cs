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

            // 3xx redirect: the handler is configured with AllowAutoRedirect = false, so
            // it does NOT follow. Surface the target as an actionable result instead of a
            // cryptic "HTTP 301". Crucially this happens BEFORE any 402/payment handling,
            // so a provider that host-redirects before its 402 is never paid, and no
            // agent-supplied custom header is ever sent to the redirect host.
            if (RedirectResolver.TryResolve(response, url, out var redirectLocation))
            {
                return L402FetchResult.Redirected(url, (int)response.StatusCode, redirectLocation);
            }

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

        if (amountSats.Value > maxSats)
        {
            throw new InvalidOperationException($"Invoice amount {amountSats.Value} sats exceeds maximum {maxSats} sats");
        }

        // Atomically reserve against the session cap BEFORE paying — closes the
        // check-then-pay race. Committed on settle/pending, released on a proven failure.
        var reservation = await _budgetService.TryReserveAsync(amountSats.Value, cancellationToken);
        if (!reservation.Success)
        {
            throw new InvalidOperationException($"Budget check failed: {reservation.DenialReason}");
        }
        var reservationId = reservation.ReservationId!;

        // Pay invoice. If the wallet call throws, no funds provably moved here — release.
        NwcPaymentResult paymentResult;
        try
        {
            paymentResult = await _walletService.PayInvoiceAsync(invoice, cancellationToken);
        }
        catch
        {
            _budgetService.ReleaseReservation(reservationId);
            throw;
        }

        // IN FLIGHT: not settled, may still fail — so no preimage exists to authenticate
        // with. The funds are committed, so record the spend rather than losing it, but
        // do not call this a failure (the caller must not retry and pay twice).
        if (paymentResult.IsPending)
        {
            // Funds committed but not settled — commit the reservation (do NOT release).
            _budgetService.CommitReservation(reservationId, amountSats.Value);
            var pendingTracking = !string.IsNullOrEmpty(paymentResult.TrackingId)
                ? $" (tracking ID: {paymentResult.TrackingId})"
                : "";
            throw new InvalidOperationException(
                $"Payment has not settled yet{pendingTracking} — it may still succeed or fail. " +
                "Do not retry it; check its status with the provider.");
        }

        if (!paymentResult.Success)
        {
            // Hard failure — the wallet proves no funds moved, so release the reservation.
            _budgetService.ReleaseReservation(reservationId);
            throw new InvalidOperationException($"Payment failed: {paymentResult.ErrorMessage}");
        }

        // Check if preimage is available - both L402 and MPP require it
        if (!paymentResult.HasPreimage)
        {
            // The payment SETTLED — the money left the wallet — there is simply no proof
            // of it. Commit the reservation before bailing out, or the budget silently
            // under-counts a payment that actually happened.
            _budgetService.CommitReservation(reservationId, amountSats.Value);

            var trackingInfo = !string.IsNullOrEmpty(paymentResult.TrackingId)
                ? $" (tracking ID: {paymentResult.TrackingId})"
                : "";
            var protocol = isMpp ? "MPP" : "L402";
            throw new InvalidOperationException(
                $"Payment succeeded{trackingInfo} but wallet did not return a usable preimage. " +
                $"{protocol} requires a preimage for verification. Use NWC or LND wallet for L402/MPP support.");
        }

        // Settled with preimage — commit the reservation as spend.
        _budgetService.CommitReservation(reservationId, amountSats.Value);

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

        // Modern draft-00 sanity check: an expired challenge must never be paid.
        if (parsed.IsMpp && parsed.Mpp!.IsModern && parsed.Mpp.IsExpired())
        {
            return L402FetchResult.Failed(url,
                $"Payment challenge expired at {parsed.Mpp.Expires} — refusing to pay. Request a fresh challenge from the endpoint.", 402);
        }

        // Extract amount from invoice
        var amountSats = ExtractAmountFromBolt11(parsed.Invoice!);

        // Reject no-amount invoices (security: could bypass budget checks)
        if (amountSats == null || amountSats.Value <= 0)
        {
            return L402FetchResult.Failed(url, "Payment invoice has no amount specified. For security, only invoices with explicit amounts are supported.", 402);
        }

        if (amountSats.Value > maxSats)
        {
            return L402FetchResult.Failed(url, $"Invoice amount {amountSats.Value} sats exceeds maximum {maxSats} sats", 402);
        }

        // Modern draft-00 sanity check: the declared amount must agree with the invoice.
        // A mismatch means the challenge is inconsistent — refuse before any payment.
        if (parsed.IsMpp && parsed.Mpp!.IsModern &&
            long.TryParse(parsed.Mpp.Amount, out var declaredSats) &&
            declaredSats != amountSats.Value)
        {
            return L402FetchResult.Failed(url,
                $"Payment challenge declares {declaredSats} sats but the invoice is for {amountSats.Value} sats — refusing to pay an inconsistent challenge.", 402);
        }

        // Atomically reserve the amount against the session cap BEFORE paying — this closes
        // the check-then-pay race so two concurrent L402 fetches can't both pass against the
        // same balance. Committed on settle/pending, released on a proven hard failure.
        var reservation = await _budgetService.TryReserveAsync(amountSats.Value, cancellationToken);
        if (!reservation.Success)
        {
            return L402FetchResult.Failed(url, $"Budget check failed: {reservation.DenialReason}", 402);
        }
        var reservationId = reservation.ReservationId!;

        // Pay invoice via wallet. If the wallet call itself throws, no funds provably moved
        // on this path — release the reservation and rethrow.
        NwcPaymentResult paymentResult;
        try
        {
            paymentResult = await _walletService.PayInvoiceAsync(parsed.Invoice!, cancellationToken);
        }
        catch
        {
            _budgetService.ReleaseReservation(reservationId);
            throw;
        }

        // IN FLIGHT: not settled, may still fail — no preimage exists to authenticate
        // with. The funds are committed, so record the spend rather than losing it, but
        // do not call this a failure (the caller must not retry and pay twice).
        if (paymentResult.IsPending)
        {
            // Funds committed but not settled — commit the reservation (do NOT release).
            _budgetService.CommitReservation(reservationId, amountSats.Value);
            var pendingTracking = !string.IsNullOrEmpty(paymentResult.TrackingId)
                ? $" (tracking ID: {paymentResult.TrackingId})"
                : "";
            // PENDING, not success: this payment may still fail, so the audit trail must
            // not claim it settled. The sats still count toward the session total (the
            // funds are committed) — see PaymentHistoryService.GetSummary.
            _historyService.RecordPayment(url, method, amountSats.Value, parsed.Invoice!, null, null, null,
                PaymentStatus.Pending,
                $"Payment has not settled yet{pendingTracking} — it may still succeed or fail.");
            // Report the committed amount: callers use PaidAmountSats to tell the agent
            // money moved and it must not retry. Defaulting to 0 here would discard that.
            return L402FetchResult.Failed(url,
                $"Payment has not settled yet{pendingTracking} — it may still succeed or fail. " +
                "Do not retry it; check its status with the provider.", 402, amountSats.Value);
        }

        if (!paymentResult.Success)
        {
            // Hard failure — the wallet proves no funds moved, so release the reservation.
            _budgetService.ReleaseReservation(reservationId);
            _historyService.RecordFailedPayment(url, method, amountSats.Value, paymentResult.ErrorMessage ?? "Unknown error", parsed.Invoice!);
            return L402FetchResult.Failed(url, $"Payment failed: {paymentResult.ErrorMessage}", 402);
        }

        // Check if preimage is available - both L402 and MPP require it for verification
        if (!paymentResult.HasPreimage)
        {
            // The payment SETTLED — the money left the wallet — there is simply no proof
            // of it. Commit the reservation before bailing out, or the budget silently
            // under-counts a payment that actually happened.
            _budgetService.CommitReservation(reservationId, amountSats.Value);

            var trackingInfo = !string.IsNullOrEmpty(paymentResult.TrackingId)
                ? $" (tracking ID: {paymentResult.TrackingId})"
                : "";
            var protocolName = parsed.IsMpp ? "MPP" : "L402";
            _historyService.RecordFailedPayment(url, method, amountSats.Value,
                $"Payment succeeded but no usable preimage returned{trackingInfo}. {protocolName} requires a preimage.", parsed.Invoice!);
            // The spend above is real and already recorded — report it so callers can warn
            // the agent off a retry. Defaulting to 0 would hide a payment that happened.
            return L402FetchResult.Failed(url,
                $"Payment succeeded{trackingInfo} but wallet did not return a usable preimage. " +
                $"{protocolName} requires a preimage for verification. Use NWC or LND wallet for L402/MPP support.", 402, amountSats.Value);
        }

        // SINGLE SOURCE OF TRUTH for recording a real payment. The invoice is paid (the
        // preimage is in hand), so the client records the spend and arms the cooldown here,
        // EXACTLY ONCE, before the retry — and the history entry once after it (below).
        // This holds regardless of whether the authorized retry returns 2xx / 3xx-redirect /
        // 4xx / 5xx. The consuming tools (access_l402_resource, settle_agent_service) are
        // passive: they MUST NOT record spend / payment / payment-time / failed-payment
        // themselves. RecordPaymentTime arms the cross-payment cooldown on EVERY real
        // payment — success, redirect, or post-payment error — matching the Python port.
        // Settled with preimage — commit the reservation as spend, exactly once, before
        // the authorized retry (which may 2xx / 3xx / 4xx / 5xx — the money already moved).
        _budgetService.CommitReservation(reservationId, amountSats.Value);
        _budgetService.RecordPaymentTime();

        string authToken;
        string protocol;

        if (parsed.IsMpp && parsed.Mpp!.IsModern)
        {
            // Modern draft-00: single-use credential echoing the challenge byte-exact.
            // Never cache or replay it — each fetch mints a fresh one.
            authToken = parsed.Mpp.BuildModernCredential(paymentResult.PreimageHex);
            protocol = "MPP";
        }
        else if (parsed.IsMpp)
        {
            // Legacy MPP: preimage-only authentication
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
                parsed.Mpp!.IsModern
                    ? $"Payment {authToken}"
                    : $"Payment method=\"lightning\", preimage=\"{paymentResult.PreimageHex}\"");
        }
        else
        {
            retryRequest.Headers.Authorization = new AuthenticationHeaderValue(parsed.L402!.Scheme, authToken);
        }

        var retryResponse = await _httpClient.SendAsync(retryRequest, cancellationToken);

        // Payment-Receipt (draft-00): parsed tolerantly — a missing or malformed receipt
        // must never fail the successful payment. Safe to surface: it carries only the
        // payment hash, never the preimage.
        MppPaymentReceipt? paymentReceipt = null;
        if (retryResponse.Headers.TryGetValues("Payment-Receipt", out var receiptValues))
        {
            paymentReceipt = MppPaymentReceipt.Parse(receiptValues.FirstOrDefault());
        }

        // Record the settled payment in session history EXACTLY ONCE, here, regardless of
        // the retry outcome (2xx / 3xx-redirect / 4xx / 5xx). The invoice was paid, so this
        // is always a settled RecordPayment and NEVER a RecordFailedPayment. Deduped from
        // the two identical calls the redirect branch and the normal branch used to each
        // make (the review flagged the duplicated block).
        _historyService.RecordPayment(
            url: url,
            method: method,
            amountSats: amountSats.Value,
            invoice: parsed.Invoice!,
            preimageHex: paymentResult.PreimageHex,
            l402Token: authToken,
            statusCode: (int)retryResponse.StatusCode);

        // Consistency with the initial-fetch path: a 3xx on the paid retry is not
        // auto-followed (AllowAutoRedirect = false). The payment already settled, so
        // surface the redirect target as actionable AND report the paid amount + token
        // so the caller warns the agent off a double-pay and can reuse the token.
        //
        // KNOWN LIMITATION (same-host paid redirect): a redirect whose target is the SAME
        // host is deliberately still NOT followed here — auto-follow on the paid retry is
        // too risky in the money path (a re-issued 402 could trigger a second payment).
        // L402 providers SHOULD serve the resource directly on the paid retry rather than
        // redirecting; if one redirects after payment, we hand the paid token back so a
        // well-behaved agent retries the redirect target WITH the token instead of paying
        // again. See the "ALREADY PAID" message below.
        if (RedirectResolver.TryResolve(retryResponse, url, out var retryRedirect))
        {
            return new L402FetchResult
            {
                Success = false,
                Url = url,
                StatusCode = (int)retryResponse.StatusCode,
                RedirectLocation = retryRedirect,
                PaidAmountSats = amountSats.Value,
                L402Token = authToken,
                Protocol = protocol,
                // TryResolve == true guarantees a non-null, absolute http(s) Location, so
                // the redirect message is unconditional. (The old ternary's "redirect with
                // no Location header" branch was unreachable and has been removed.) Explicit
                // "already paid, do NOT pay again" wording so the agent reuses the token
                // against the redirect target instead of re-paying. Surfaced verbatim by
                // both access_l402_resource and settle_agent_service.
                ErrorMessage =
                    $"Payment succeeded ({amountSats.Value} sats). The resource redirected to {retryRedirect}. " +
                    "You have ALREADY PAID — do NOT pay again. Retry the redirect target with the returned L402 token."
            };
        }

        var content = await retryResponse.Content.ReadAsStringAsync(cancellationToken);
        var contentType = retryResponse.Content.Headers.ContentType?.MediaType;

        if (retryResponse.IsSuccessStatusCode)
        {
            return L402FetchResult.Succeeded(url, content, (int)retryResponse.StatusCode, contentType, amountSats.Value, authToken, protocol, paymentReceipt);
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

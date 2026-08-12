using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for fetching a URL with automatic L402 payment handling.
/// When a 402 Payment Required response is received, the tool automatically
/// pays the Lightning invoice and retries the request.
/// </summary>
[McpServerToolType]
public static class AccessL402ResourceTool
{
    /// <summary>
    /// Fetches a URL, automatically paying any L402 challenge.
    /// </summary>
    /// <param name="url">The URL to fetch.</param>
    /// <param name="method">HTTP method (GET, POST, PUT, DELETE). Defaults to GET.</param>
    /// <param name="headers">Optional headers as JSON object (e.g., {"Authorization": "Bearer token"}).</param>
    /// <param name="body">Optional request body for POST/PUT requests.</param>
    /// <param name="maxSats">Maximum satoshis to pay for L402 challenge. Defaults to 1000.</param>
    /// <param name="server">MCP server for elicitation.</param>
    /// <param name="l402Client">Injected L402 HTTP client.</param>
    /// <param name="budgetService">Injected budget service.</param>
    /// <param name="priceService">Injected price service.</param>
    /// <param name="paymentHistory">Injected payment history service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Response body or error message.</returns>
    [McpServerTool(Name = "access_l402_resource"), Description("Fetch a URL, automatically pay any L402 Lightning payment challenge")]
    public static async Task<string> AccessL402Resource(
        [Description("The URL to fetch")] string url,
        [Description("HTTP method (GET, POST, PUT, DELETE). Defaults to GET")] string method = "GET",
        [Description("Optional headers as JSON object")] string? headers = null,
        [Description("Optional request body for POST/PUT requests")] string? body = null,
        [Description("Maximum satoshis to pay for L402 challenge. Defaults to 1000")] int maxSats = 1000,
        [Description("Confirmation code relayed by the human operator from the server console (stderr). Required when a previous call returned requiresConfirmation=true.")] string? confirmationNonce = null,
        McpServer? server = null,
        IL402HttpClient? l402Client = null,
        IBudgetService? budgetService = null,
        IPriceService? priceService = null,
        IPaymentHistoryService? paymentHistory = null,
        IRateLimiter? rateLimiter = null,
        CancellationToken cancellationToken = default)
    {
        // Captured for the durable receipt (success path); set from the budget check below.
        var paymentPolicy = "auto (no budget check)";

        // Rate limiting check
        if (rateLimiter != null && !rateLimiter.IsAllowed("access_l402_resource"))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Rate limit exceeded",
                message = "Too many requests. Please wait before trying again.",
                remaining = rateLimiter.GetRemainingRequests("access_l402_resource")
            });
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "URL is required"
            });
        }

        // SSRF Protection: Validate URL to prevent access to internal resources
        var urlValidationError = ValidateUrl(url);
        if (urlValidationError != null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = urlValidationError
            });
        }

        if (l402Client == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "L402 HTTP client not available"
            });
        }

        // Check budget approval for maximum possible payment amount
        if (budgetService != null)
        {
            var approvalResult = await budgetService.CheckApprovalLevelAsync(maxSats, cancellationToken);
            paymentPolicy = PaymentPolicy.Label(approvalResult.Level);

            if (approvalResult.Level == ApprovalLevel.Deny)
            {
                // PASSIVE: no payment was attempted, so the tool records nothing. The client
                // (L402HttpClient) is the single source of truth for payment/failed-payment
                // recording — the tool must not call RecordFailedPayment (a budget denial is
                // a clean not-paid refusal, and double-recording would fragment the ledger).
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = approvalResult.DenialReason,
                    budget = new
                    {
                        maxSats,
                        amountUsd = approvalResult.AmountUsd,
                        remainingSessionUsd = approvalResult.RemainingSessionBudgetUsd
                    }
                });
            }

            // Handle confirmation requirements for L402 payments
            if (approvalResult.RequiresConfirmation)
            {
                // Check if a confirmed nonce was provided
                if (!string.IsNullOrWhiteSpace(confirmationNonce))
                {
                    var confirmation = budgetService.ValidateAndConsumeConfirmation(confirmationNonce.Trim().ToUpperInvariant(), approvalResult.AmountSats, "access_l402_resource", url);
                    if (confirmation == null)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            success = false,
                            error = "Confirmation code is invalid, expired, already used, or does not match THIS " +
                                    "request's amount, tool, and URL. Codes are bound to the exact amount, tool, and " +
                                    "destination approved — a code cannot be redirected to a different URL.",
                            message = "The code may have expired (2-minute limit), been used already, or been issued for a " +
                                      "different amount/tool/URL. Request a new confirmation by calling access_l402_resource without a confirmationNonce."
                        });
                    }

                    Console.Error.WriteLine($"[Lightning Enable] L402 payment of up to {approvalResult.AmountUsd:C} confirmed via nonce {confirmation.Nonce} for {RedactUrl(url)}");
                }
                else
                {
                    // Try MCP elicitation first (most clients don't support this yet)
                    var elicitationConfirmed = await RequestL402ConfirmationAsync(
                        server,
                        approvalResult,
                        url,
                        cancellationToken);

                    if (!elicitationConfirmed)
                    {
                        // Always fall back to nonce-based confirmation.
                        // MCP elicitation is unreliable — many clients (including Claude Code)
                        // report Elicitation capability but don't handle it correctly.
                        var urlDisplay = RedactUrl(url);
                        var pending = budgetService.CreatePendingConfirmation(
                            maxSats,
                            approvalResult.AmountUsd,
                            "access_l402_resource",
                            urlDisplay,
                            url);

                        // OUT-OF-BAND CONFIRMATION: code to STDERR only (human sees the server
                        // console/logs; the model only sees tool results). An injected agent
                        // can't read it to self-approve. The code MUST NOT appear in the result.
                        Console.Error.WriteLine(
                            "[Lightning Enable] *** L402 PAYMENT CONFIRMATION REQUIRED ***\n" +
                            $"  access_l402_resource — up to {approvalResult.AmountUsd:C} ({maxSats:N0} sats), {urlDisplay}\n" +
                            $"  Confirmation code: {pending.Nonce}\n" +
                            "  To approve, give this code to the agent. Expires in 120s.");

                        return JsonSerializer.Serialize(new
                        {
                            success = false,
                            requiresConfirmation = true,
                            error = "L402 payment requires human confirmation",
                            message = $"This L402 request may cost up to {approvalResult.AmountUsd:C} ({maxSats:N0} sats), which exceeds the auto-approve threshold. " +
                                      "A confirmation code was printed to the server console/logs — visible to the human operator, NOT to you. " +
                                      "Ask the human to read that code and give it to you.",
                            howToConfirm = "Ask the human operator for the confirmation code shown in the server console, then call " +
                                           "access_l402_resource(url=\"...\", confirmationNonce=\"<code-from-human>\").",
                            expiresInSeconds = 120,
                            amount = new
                            {
                                maxSats,
                                usd = Math.Round(approvalResult.AmountUsd, 2)
                            },
                            thresholds = new
                            {
                                autoApprove = budgetService.GetUserConfiguration().Tiers.AutoApprove,
                                note = "Payments above this require a human-supplied confirmation code"
                            }
                        });
                    }
                }
            }

            // Log if needed
            if (approvalResult.Level == ApprovalLevel.LogAndApprove)
            {
                Console.Error.WriteLine($"[Lightning Enable] Auto-approved L402 payment up to: {approvalResult.AmountUsd:C} ({maxSats} sats) for {RedactUrl(url)}");
            }
        }

        // Ambient payment intent: the durable receipt is written by the wallet-seam
        // decorator (ReceiptRecordingWalletService) the moment the invoice is paid —
        // this scope only enriches it (redacted endpoint + policy) and carries back
        // the honest receipt_written signal. The tool itself writes nothing, so an
        // L402 payment produces exactly ONE receipt line.
        using var receiptScope = PaymentReceiptScope.Begin("l402", context: RedactUrl(url), policy: paymentPolicy);

        try
        {
            var result = await l402Client.FetchWithL402Async(
                url,
                method,
                headers,
                body,
                maxSats,
                cancellationToken);

            // Unfollowed 3xx redirect (AllowAutoRedirect = false): return a clear,
            // actionable result naming the redirect target instead of a cryptic HTTP
            // failure. We do NOT follow it here — following would leak the agent's custom
            // headers to a cross-origin host and, on the L402 path, risk paying a provider
            // that then drops the L402 header on the host change. If a payment already
            // settled before the redirect (paid retry), the token is surfaced too.
            if (!string.IsNullOrEmpty(result.RedirectLocation))
            {
                decimal? redirectAmountUsd = null;
                if (result.PaidAmountSats > 0 && priceService != null)
                {
                    try { redirectAmountUsd = Math.Round(await priceService.SatsToUsdAsync(result.PaidAmountSats, cancellationToken), 2); }
                    catch { /* USD conversion is best-effort */ }
                }

                return JsonSerializer.Serialize(new
                {
                    success = false,
                    url = result.Url,
                    statusCode = result.StatusCode,
                    error = result.ErrorMessage,
                    redirect_location = result.RedirectLocation,
                    receipt_written = result.PaidAmountSats > 0 ? (bool?)(receiptScope.ReceiptWritten ?? false) : null,
                    payment = result.PaidAmountSats > 0
                        ? new
                        {
                            paid = true,
                            amountSats = result.PaidAmountSats,
                            amountUsd = redirectAmountUsd,
                            l402Token = result.L402Token,
                            protocol = result.Protocol ?? "L402"
                        }
                        : null
                });
            }

            if (result.Success)
            {
                if (result.PaidAmountSats > 0)
                {
                    // Budget recording and payment history are handled by L402HttpClient.FetchWithL402Async()

                    var amountUsd = priceService != null
                        ? await priceService.SatsToUsdAsync(result.PaidAmountSats, cancellationToken)
                        : 0m;

                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        url = result.Url,
                        statusCode = result.StatusCode,
                        contentType = result.ContentType,
                        content = result.Content,
                        receipt_written = receiptScope.ReceiptWritten ?? false,
                        payment = new
                        {
                            paid = true,
                            amountSats = result.PaidAmountSats,
                            amountUsd = Math.Round(amountUsd, 2),
                            l402Token = result.L402Token,
                            protocol = result.Protocol ?? "L402"
                        }
                    });
                }
                else
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        url = result.Url,
                        statusCode = result.StatusCode,
                        contentType = result.ContentType,
                        content = result.Content,
                        payment = new { paid = false }
                    });
                }
            }
            else
            {
                // If payment was made but the retry failed (e.g., store split-flow),
                // surface the L402 token so it can be used with the correct endpoint
                if (result.PaidAmountSats > 0 && !string.IsNullOrEmpty(result.L402Token))
                {
                    decimal? amountUsd = null;
                    try
                    {
                        if (priceService != null)
                            amountUsd = Math.Round(await priceService.SatsToUsdAsync(result.PaidAmountSats, cancellationToken), 2);
                    }
                    catch { /* USD conversion is best-effort — never lose the token */ }

                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        alreadyPaid = true,
                        url = result.Url,
                        statusCode = result.StatusCode,
                        error = result.ErrorMessage,
                        receipt_written = receiptScope.ReceiptWritten ?? false,
                        payment = new
                        {
                            paid = true,
                            amountSats = result.PaidAmountSats,
                            amountUsd,
                            l402Token = result.L402Token,
                            protocol = result.Protocol ?? "L402",
                            note = "Payment succeeded but the server returned a non-success status on the authorized " +
                                   "retry. You have ALREADY PAID — do NOT pay again. The payment token above is valid; " +
                                   "retry the target with it instead of re-paying."
                        }
                    });
                }

                // Money may still have moved here WITHOUT a usable token (payment
                // pending, or settled without a preimage) — the alreadyPaid branch
                // above requires a token. Surface paid + receipt_written so the
                // committed sats are never invisible in the tool result.
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    url = result.Url,
                    statusCode = result.StatusCode,
                    error = result.ErrorMessage,
                    receipt_written = result.PaidAmountSats > 0 ? (bool?)(receiptScope.ReceiptWritten ?? false) : null,
                    payment = result.PaidAmountSats > 0
                        ? new { paid = true, amountSats = result.PaidAmountSats }
                        : null
                });
            }
        }
        catch (Exception ex)
        {
            // The client can throw AFTER the invoice settled (e.g. a transient error on
            // the authorized retry). null = nothing was paid; true/false = money moved
            // and the durable receipt did/didn't land.
            return JsonSerializer.Serialize(new
            {
                success = false,
                url,
                receipt_written = receiptScope.ReceiptWritten,
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Returns a display-safe URL with credentials stripped. The query string, fragment,
    /// and userinfo can carry secrets (e.g. <c>?token=...</c>); this keeps only
    /// scheme://host[:port]/path, marks when anything was dropped, and length-caps the
    /// result. Use it anywhere the URL is printed to stderr or logged so credentials never
    /// reach console/log history (engineering standard #5). The full URL is still returned
    /// in tool results — the caller already supplied it.
    /// </summary>
    internal static string RedactUrl(string url)
    {
        const int maxLen = 80;
        var (safe, dropped) = BuildRedactedUrl(url);
        if (safe.Length > maxLen)
            safe = safe.Substring(0, maxLen) + "...";  // cap the URL part; marker added after
        return dropped ? safe + " (redacted)" : safe;
    }

    private static (string safe, bool dropped) BuildRedactedUrl(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                // uri.Host already brackets IPv6 literals (e.g. "[2001:db8::1]"), so
                // scheme://host:port stays unambiguous without extra handling.
                var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
                var safe = $"{uri.Scheme}://{uri.Host}{port}{uri.AbsolutePath}";
                var dropped = !string.IsNullOrEmpty(uri.Query)
                    || !string.IsNullOrEmpty(uri.Fragment)
                    || !string.IsNullOrEmpty(uri.UserInfo);
                return (safe, dropped);
            }
        }
        catch
        {
            // fall through to the hand-rolled fallback below
        }

        // Parse failed: strip query, fragment, AND userinfo by hand so we never leave
        // `user:pass@host`, and report whether anything was removed.
        var s = url.Split('?')[0].Split('#')[0];
        var strippedQueryOrFragment = s.Length != url.Length;
        var schemeIdx = s.IndexOf("//", StringComparison.Ordinal);
        var strippedUserInfo = false;
        if (schemeIdx >= 0)
        {
            var atIdx = s.IndexOf('@', schemeIdx + 2);
            if (atIdx >= 0)
            {
                s = s.Substring(0, schemeIdx + 2) + s.Substring(atIdx + 1);
                strippedUserInfo = true;
            }
        }
        return (s, strippedQueryOrFragment || strippedUserInfo);
    }

    /// <summary>
    /// Validates URL to prevent SSRF attacks.
    /// Blocks access to private IPs, localhost, and internal networks.
    /// <para/>
    /// This is the INITIAL-URL check (belt-and-suspenders) and delegates to the
    /// shared <see cref="SsrfUrlGuard"/> — a cheap, synchronous, never-throwing
    /// pre-check (scheme + IP-literal classification + blocked-hostname match). It
    /// deliberately does NOT resolve DNS: the definitive guard is the connect-time
    /// <see cref="SsrfConnectValidator"/> wired onto the L402 HTTP client in
    /// Program.cs, which validates the actual IP the socket connects to (closing the
    /// DNS-rebind window) and re-validates every auto-redirect hop. Removing the old
    /// blocking <c>Dns.GetHostAddresses</c> here also fixes the double-resolution
    /// and the unhandled <see cref="ArgumentOutOfRangeException"/> an overlong host
    /// used to throw. Error messages are generic — they never echo the internal host/IP.
    /// </summary>
    internal static string? ValidateUrl(string url) => SsrfUrlGuard.Validate(url);

    /// <summary>
    /// Requests user confirmation for L402 payments based on the approval level.
    /// </summary>
    private static async Task<bool> RequestL402ConfirmationAsync(
        McpServer? server,
        ApprovalCheckResult approvalResult,
        string url,
        CancellationToken cancellationToken)
    {
        // If no server or elicitation not supported, auto-deny for safety
        if (server?.ClientCapabilities?.Elicitation == null)
        {
            Console.Error.WriteLine($"[Lightning Enable] L402 payment up to {approvalResult.AmountUsd:C} requires confirmation but elicitation not supported by client");
            Console.Error.WriteLine("[Lightning Enable] For payments requiring confirmation, use a client that supports MCP elicitation");
            return false;
        }

        try
        {
            var urlDisplay = RedactUrl(url);

            if (approvalResult.Level == ApprovalLevel.FormConfirm)
            {
                // Form-based confirmation (in-band)
                var schema = new ElicitRequestParams.RequestSchema
                {
                    Properties =
                    {
                        ["approved"] = new ElicitRequestParams.BooleanSchema
                        {
                            Description = "Set to true to approve this L402 payment"
                        }
                    }
                };

                var response = await server.ElicitAsync(new ElicitRequestParams
                {
                    Message = $"L402 Payment Authorization\n\n" +
                              $"URL: {urlDisplay}\n" +
                              $"Max Amount: {approvalResult.AmountUsd:C} ({approvalResult.AmountSats:N0} sats)\n\n" +
                              $"Authorize this L402 API payment?",
                    RequestedSchema = schema
                }, cancellationToken);

                if (response.Action == "accept" &&
                    response.Content?.TryGetValue("approved", out var approvedElement) == true)
                {
                    return approvedElement.ValueKind == JsonValueKind.True;
                }

                return false;
            }
            else if (approvalResult.Level == ApprovalLevel.UrlConfirm)
            {
                // URL-based confirmation (out-of-band) with amount verification
                var schema = new ElicitRequestParams.RequestSchema
                {
                    Properties =
                    {
                        ["confirmAmount"] = new ElicitRequestParams.StringSchema
                        {
                            Description = $"Enter '{approvalResult.AmountUsd:F2}' to confirm this payment"
                        }
                    }
                };

                var response = await server.ElicitAsync(new ElicitRequestParams
                {
                    Message = $"LARGE L402 PAYMENT - Verification Required\n\n" +
                              $"URL: {urlDisplay}\n" +
                              $"Max Amount: {approvalResult.AmountUsd:C} ({approvalResult.AmountSats:N0} sats)\n\n" +
                              $"This is a significant API payment. Please verify:\n" +
                              $"- You initiated this API call\n" +
                              $"- You trust this endpoint\n" +
                              $"- The amount is acceptable\n\n" +
                              $"Type the payment amount in USD to confirm (e.g., {approvalResult.AmountUsd:F2}):",
                    RequestedSchema = schema
                }, cancellationToken);

                if (response.Action == "accept" &&
                    response.Content?.TryGetValue("confirmAmount", out var amountElement) == true)
                {
                    var enteredAmount = amountElement.GetString();
                    if (decimal.TryParse(enteredAmount?.Replace("$", "").Trim(), out var amount))
                    {
                        return Math.Abs(amount - approvalResult.AmountUsd) < 0.01m;
                    }
                }

                return false;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Lightning Enable] L402 elicitation failed: {ex.Message}");
            return false;
        }

        return false;
    }
}

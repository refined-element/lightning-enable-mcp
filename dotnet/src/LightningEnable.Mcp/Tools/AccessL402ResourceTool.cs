using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
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
        [Description("Confirmation nonce from confirm_payment tool. Required when previous call returned requiresConfirmation=true.")] string? confirmationNonce = null,
        McpServer? server = null,
        IL402HttpClient? l402Client = null,
        IBudgetService? budgetService = null,
        IPriceService? priceService = null,
        IPaymentHistoryService? paymentHistory = null,
        IRateLimiter? rateLimiter = null,
        CancellationToken cancellationToken = default)
    {
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

            if (approvalResult.Level == ApprovalLevel.Deny)
            {
                paymentHistory?.RecordFailedPayment(
                    url,
                    "L402",
                    maxSats,
                    approvalResult.DenialReason ?? "Budget limit exceeded",
                    null);

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
                    var confirmation = budgetService.ValidateAndConsumeConfirmation(confirmationNonce.Trim().ToUpperInvariant());
                    if (confirmation == null)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            success = false,
                            error = "Invalid, expired, or already-used confirmation nonce",
                            message = "The nonce may have expired (2 minute limit) or was already used. " +
                                      "Request a new confirmation by calling access_l402_resource without a nonce."
                        });
                    }

                    Console.Error.WriteLine($"[Lightning Enable] L402 payment of up to {approvalResult.AmountUsd:C} confirmed via nonce {confirmation.Nonce} for {url}");
                }
                else
                {
                    // Try MCP elicitation first
                    var elicitationConfirmed = await RequestL402ConfirmationAsync(
                        server,
                        approvalResult,
                        url,
                        cancellationToken);

                    if (!elicitationConfirmed)
                    {
                        // Check if elicitation was even available
                        var elicitationAvailable = server?.ClientCapabilities?.Elicitation != null;

                        if (!elicitationAvailable)
                        {
                            // Create a pending confirmation with a nonce
                            var urlDisplay = url.Length > 60 ? url.Substring(0, 60) + "..." : url;
                            var pending = budgetService.CreatePendingConfirmation(
                                maxSats,
                                approvalResult.AmountUsd,
                                "access_l402_resource",
                                urlDisplay);

                            return JsonSerializer.Serialize(new
                            {
                                success = false,
                                requiresConfirmation = true,
                                error = "L402 payment requires your confirmation",
                                message = $"This L402 request may cost up to {approvalResult.AmountUsd:C} ({maxSats:N0} sats), which exceeds the auto-approve threshold.",
                                nonce = pending.Nonce,
                                howToConfirm = $"Step 1: Call confirm_payment(nonce: \"{pending.Nonce}\") to approve.\n" +
                                               $"Step 2: Call access_l402_resource(url=\"{url}\", confirmationNonce=\"{pending.Nonce}\") to proceed.",
                                expiresInSeconds = 120,
                                amount = new
                                {
                                    maxSats,
                                    usd = Math.Round(approvalResult.AmountUsd, 2)
                                },
                                thresholds = new
                                {
                                    autoApprove = budgetService.GetUserConfiguration().Tiers.AutoApprove,
                                    note = "Payments above this require confirmation via confirm_payment tool"
                                }
                            });
                        }

                        // Elicitation was available but user declined
                        return JsonSerializer.Serialize(new
                        {
                            success = false,
                            error = "L402 payment cancelled by user",
                            requiresConfirmation = true,
                            amount = new
                            {
                                maxSats,
                                usd = approvalResult.AmountUsd
                            }
                        });
                    }
                }
            }

            // Log if needed
            if (approvalResult.Level == ApprovalLevel.LogAndApprove)
            {
                Console.Error.WriteLine($"[Lightning Enable] Auto-approved L402 payment up to: {approvalResult.AmountUsd:C} ({maxSats} sats) for {url}");
            }
        }

        try
        {
            var result = await l402Client.FetchWithL402Async(
                url,
                method,
                headers,
                body,
                maxSats,
                cancellationToken);

            if (result.Success)
            {
                if (result.PaidAmountSats > 0)
                {
                    // Record the actual payment
                    budgetService?.RecordSpend(result.PaidAmountSats);
                    budgetService?.RecordPaymentTime();
                    paymentHistory?.RecordPayment(
                        url,
                        "L402",
                        result.PaidAmountSats,
                        null,
                        null,
                        result.L402Token,
                        result.StatusCode);

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
                        payment = new
                        {
                            paid = true,
                            amountSats = result.PaidAmountSats,
                            amountUsd = Math.Round(amountUsd, 2),
                            l402Token = result.L402Token
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
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    url = result.Url,
                    statusCode = result.StatusCode,
                    error = result.ErrorMessage
                });
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                url,
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Validates URL to prevent SSRF attacks.
    /// Blocks access to private IPs, localhost, and internal networks.
    /// </summary>
    private static string? ValidateUrl(string url)
    {
        // Validate URL format
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "Invalid URL format";
        }

        // Only allow HTTP and HTTPS schemes
        if (uri.Scheme != "http" && uri.Scheme != "https")
        {
            return "Only HTTP and HTTPS URLs are allowed";
        }

        // Check for localhost variations
        var host = uri.Host.ToLowerInvariant();
        if (host == "localhost" || host == "127.0.0.1" || host == "::1" || host == "[::1]")
        {
            return "Access to localhost is not allowed";
        }

        // Check for link-local addresses
        if (host.StartsWith("169.254.") || host == "fe80::")
        {
            return "Access to link-local addresses is not allowed";
        }

        // Check for cloud metadata endpoints
        if (host == "169.254.169.254" || host == "metadata.google.internal" ||
            host == "metadata.azure.com" || host.EndsWith(".internal"))
        {
            return "Access to cloud metadata endpoints is not allowed";
        }

        // Try to resolve hostname and check for private IPs
        try
        {
            var addresses = Dns.GetHostAddresses(uri.Host);
            foreach (var addr in addresses)
            {
                if (IsPrivateOrReservedAddress(addr))
                {
                    return "Access to private or internal networks is not allowed";
                }
            }
        }
        catch (SocketException)
        {
            // DNS resolution failed - allow the request to proceed
            // (will fail naturally with a more informative error)
        }

        return null; // Valid URL
    }

    /// <summary>
    /// Checks if an IP address is private, loopback, or reserved.
    /// </summary>
    private static bool IsPrivateOrReservedAddress(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            return true;

        byte[] bytes = address.GetAddressBytes();

        // IPv4 checks
        if (bytes.Length == 4)
        {
            // 127.x.x.x - Loopback
            if (bytes[0] == 127)
                return true;

            // 10.x.x.x - Private
            if (bytes[0] == 10)
                return true;

            // 172.16.x.x - 172.31.x.x - Private
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;

            // 192.168.x.x - Private
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;

            // 169.254.x.x - Link-local
            if (bytes[0] == 169 && bytes[1] == 254)
                return true;

            // 0.x.x.x - Reserved
            if (bytes[0] == 0)
                return true;

            // 224.x.x.x - 239.x.x.x - Multicast
            if (bytes[0] >= 224 && bytes[0] <= 239)
                return true;

            // 240.x.x.x - 255.x.x.x - Reserved
            if (bytes[0] >= 240)
                return true;
        }

        // IPv6 checks (loopback)
        if (IPAddress.IsLoopback(address))
            return true;

        return false;
    }

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
            var urlDisplay = url.Length > 50 ? url.Substring(0, 50) + "..." : url;

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

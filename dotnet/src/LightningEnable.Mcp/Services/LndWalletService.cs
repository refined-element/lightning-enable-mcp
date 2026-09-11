using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
/// - LND_PAYMENT_TIMEOUT_SECONDS: How long a payment may stay in flight before it is
///   reported as pending (default 25, so a tool call stays well under 30s)
/// - LND_FEE_LIMIT_SATS: Fixed routing-fee ceiling per payment. Unset = 5% of the
///   invoice amount, floored at MinFeeLimitSats.
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
    private readonly string? _baseHost;
    private readonly int _paymentTimeoutSeconds;
    private readonly long? _feeLimitSatsOverride;
    private bool _disposed;

    /// <summary>
    /// routerrpc SendPaymentV2 - the supported way to pay an invoice. The legacy
    /// lnrpc.SendPaymentSync route this client used to call
    /// (POST /v1/channels/transactions) has been REMOVED from LND: a modern node
    /// answers it with 404 {"code":5,"message":"Not Found"} and never creates a
    /// payment, so every payment failed with HTTP_404 while the node recorded no
    /// attempt at all. Verified against LND v0.21.3-beta.
    /// </summary>
    internal const string RouterSendPath = "/v2/router/send";

    /// <summary>
    /// Kept only as a fallback for nodes old enough to lack routerrpc (relative to the
    /// /v1/ base address). A 404 on the v2 route proves it was never reached, so nothing
    /// was submitted and the fallback cannot double-pay.
    /// </summary>
    internal const string LegacySendPath = "channels/transactions";

    /// <summary>
    /// LND stops trying after timeout_seconds; the client-side bound (this plus
    /// <see cref="StreamTimeoutSlackSeconds"/>) is the backstop for a stalled stream, so a
    /// payment can never block an agent indefinitely.
    /// </summary>
    internal const int DefaultPaymentTimeoutSeconds = 25;
    internal const int StreamTimeoutSlackSeconds = 10;

    /// <summary>
    /// SendPaymentV2 considers ONLY zero-fee routes when fee_limit_sat is left at its
    /// default of 0. A limit must always be sent. 5% mirrors lncli's default ceiling; the
    /// floor keeps tiny invoices (where 5% rounds to ~0) payable past a 1-sat base fee.
    /// </summary>
    internal const int DefaultFeeLimitPercent = 5;
    internal const long MinFeeLimitSats = 2;

    /// <summary>
    /// LND fills payment_preimage with 32 zero bytes when no proof exists. It is 64 valid
    /// hex characters, so a length/format check alone accepts it as a preimage.
    /// </summary>
    internal const string ZeroPreimageHex =
        "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// Client-side bound on the whole SendPaymentV2 read. Derived from
    /// LND_PAYMENT_TIMEOUT_SECONDS + slack; settable so a test can exercise the stalled-
    /// stream path without waiting the real bound.
    /// </summary>
    internal TimeSpan ClientReadTimeout { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
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

        _paymentTimeoutSeconds = PositiveIntEnv("LND_PAYMENT_TIMEOUT_SECONDS") ?? DefaultPaymentTimeoutSeconds;
        ClientReadTimeout = TimeSpan.FromSeconds(_paymentTimeoutSeconds + StreamTimeoutSlackSeconds);
        // null = derive per invoice (percentage of the amount).
        _feeLimitSatsOverride = PositiveIntEnv("LND_FEE_LIMIT_SATS");

        if (IsConfigured)
        {
            // Configure base address. The /v2 routes sit OUTSIDE the /v1/ base, so the
            // scheme-normalized host is kept separately for them.
            var scheme = _restHost!.StartsWith("https://") || _restHost.StartsWith("http://")
                ? ""
                : "https://";
            _baseHost = $"{scheme}{_restHost}".TrimEnd('/');
            _httpClient.BaseAddress = new Uri($"{_baseHost}/v1/");

            // Add macaroon header
            _httpClient.DefaultRequestHeaders.Add("Grpc-Metadata-macaroon", _macaroonHex);

            Console.Error.WriteLine($"[LND] Initialized REST client for {_restHost}");
        }
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_restHost) && !string.IsNullOrEmpty(_macaroonHex);

    /// <summary>
    /// Server-certificate policy for the LND REST client, from the documented env vars:
    /// LND_SKIP_TLS_VERIFY=true accepts any certificate (dev only); LND_TLS_CERT_PATH pins
    /// the node's own tls.cert (PEM or DER), which is what a self-signed LND cert needs.
    /// Returns null when neither is set, so the default chain validation applies.
    /// Pinning wins over skip when both are set.
    /// </summary>
    internal static Func<HttpRequestMessage, X509Certificate2?, X509Chain?, System.Net.Security.SslPolicyErrors, bool>?
        BuildServerCertificateValidator(string? skipTlsVerify, string? tlsCertPath)
    {
        if (!string.IsNullOrWhiteSpace(tlsCertPath) && !tlsCertPath.StartsWith("${"))
        {
            // LND writes tls.cert as PEM; accept DER too. Public certificate only, no key.
            var pemOrDer = File.ReadAllBytes(tlsCertPath.Trim());
            var pinned = System.Text.Encoding.ASCII.GetString(pemOrDer, 0, Math.Min(pemOrDer.Length, 32)).Contains("-----BEGIN")
                ? X509Certificate2.CreateFromPem(System.Text.Encoding.ASCII.GetString(pemOrDer))
#pragma warning disable SYSLIB0057 // net8.0 target has no X509CertificateLoader
                : new X509Certificate2(pemOrDer);
#pragma warning restore SYSLIB0057
            var pinnedThumbprint = pinned.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
            Console.Error.WriteLine("[LND] Pinning TLS certificate from LND_TLS_CERT_PATH");
            return (_, cert, _, _) =>
                cert is not null
                && CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)),
                    Convert.FromHexString(pinnedThumbprint));
        }

        if (string.Equals(skipTlsVerify?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("[LND] WARNING: LND_SKIP_TLS_VERIFY=true - TLS certificate verification is OFF");
            return (_, _, _, _) => true;
        }

        return null;
    }

    /// <summary>Primary handler for the LND HttpClient, honoring the TLS env vars above.</summary>
    internal static HttpMessageHandler CreateHttpHandler()
    {
        var handler = new HttpClientHandler();
        var validator = BuildServerCertificateValidator(
            Environment.GetEnvironmentVariable("LND_SKIP_TLS_VERIFY"),
            Environment.GetEnvironmentVariable("LND_TLS_CERT_PATH"));
        if (validator is not null)
        {
            handler.ServerCertificateCustomValidationCallback = validator;
        }
        return handler;
    }

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
    ///
    /// POST /v2/router/send (routerrpc SendPaymentV2), a server-streaming route whose
    /// frames are newline-delimited {"result": &lt;lnrpc.Payment&gt;} objects. The last
    /// frame carries the terminal status; on SUCCEEDED, payment_preimage is a hex string
    /// (NOT base64, unlike the old v1 route).
    ///
    /// The legacy POST /v1/channels/transactions (lnrpc.SendPaymentSync) has been REMOVED
    /// from LND and 404s on a modern node, so it is only tried when v2 itself is absent -
    /// see <see cref="RouterSendPath"/>.
    ///
    /// Outcomes: Failed = provably not paid (retryable); SucceededWithoutPreimage =
    /// settled but no usable proof (terminal); Pending = accepted / in flight, outcome
    /// unknown (do NOT retry).
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

            var routerOutcome = await RouterSendPaymentAsync(bolt11, cancellationToken);

            if (routerOutcome.RouteAbsent)
            {
                // routerrpc is not served by this node. The 404 proves the request never
                // reached a payment RPC, so nothing was submitted and re-sending on the
                // legacy route cannot double-pay.
                Console.Error.WriteLine("[LND] Node does not serve routerrpc SendPaymentV2; falling back to the legacy send route");
                return await LegacySendPaymentAsync(bolt11, cancellationToken);
            }

            if (routerOutcome.Result is { } terminal)
            {
                return terminal;
            }

            var payment = routerOutcome.Payment ?? new JsonObject();
            var status = (payment["status"]?.GetValue<string>() ?? "").ToUpperInvariant();
            var paymentHash = StringOrNull(payment["payment_hash"]) ?? PaymentHashHexFromBolt11(bolt11);

            if (status == "FAILED")
            {
                var reason = StringOrNull(payment["failure_reason"]) ?? "FAILED";
                Console.Error.WriteLine($"[LND] Payment failed: {reason}");
                return NwcPaymentResult.Failed("PAYMENT_FAILED", $"Payment failed: {reason}");
            }

            if (status != "SUCCEEDED")
            {
                // IN_FLIGHT / INITIATED / no frame at all. The payment was ACCEPTED, so this
                // is neither success nor failure: reporting it as a failure would invite a
                // retry that pays twice. Non-terminal by contract.
                Console.Error.WriteLine($"[LND] Payment not settled (status: {(status.Length == 0 ? "unknown" : status)}) - reporting pending");
                return NwcPaymentResult.Pending(
                    paymentHash ?? "unknown",
                    $"LND accepted the payment but has not settled it (status: {(status.Length == 0 ? "unknown" : status)}). " +
                    "Do NOT retry it - check its status on your node (lncli listpayments) before doing anything else.");
            }

            // SUCCEEDED: v2 returns the preimage as a hex string, already decoded.
            return PreimageFromSettledHex(StringOrNull(payment["payment_preimage"]), paymentHash);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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

    /// <summary>
    /// Outcome of one SendPaymentV2 attempt. Exactly one of: <see cref="RouteAbsent"/>
    /// (404 - fall back), <see cref="Result"/> (already terminal: HTTP error, stream
    /// error, or client-side timeout), or <see cref="Payment"/> (the last lnrpc.Payment
    /// frame; null when the node sent none).
    /// </summary>
    private readonly record struct RouterOutcome(bool RouteAbsent, NwcPaymentResult? Result, JsonObject? Payment);

    /// <summary>
    /// Pays via routerrpc SendPaymentV2. The whole read is bounded: LND gives up after
    /// timeout_seconds and <see cref="ClientReadTimeout"/> covers a stream that stalls
    /// without closing, so a payment can never block the calling agent indefinitely.
    /// </summary>
    private async Task<RouterOutcome> RouterSendPaymentAsync(string bolt11, CancellationToken cancellationToken)
    {
        var body = new
        {
            payment_request = bolt11,
            timeout_seconds = _paymentTimeoutSeconds,
            // LND REST takes 64-bit fields as strings.
            fee_limit_sat = FeeLimitSats(bolt11).ToString(),
            // Only the terminal frame is of interest; skip the IN_FLIGHT chatter.
            no_inflight_updates = true
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ClientReadTimeout);
        var token = timeoutCts.Token;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseHost}{RouterSendPath}")
            {
                Content = JsonContent.Create(body, options: JsonOptions)
            };

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new RouterOutcome(RouteAbsent: true, null, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                // Deliberately NOT retried on the legacy route: the route exists, so the
                // payment may have been submitted before the error.
                var errorBody = await response.Content.ReadAsStringAsync(token);
                Console.Error.WriteLine($"[LND] Payment failed: HTTP {(int)response.StatusCode}");
                return new RouterOutcome(false,
                    NwcPaymentResult.Failed($"HTTP_{(int)response.StatusCode}", $"LND payment failed: {errorBody}"),
                    null);
            }

            JsonObject? lastPayment = null;
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(token) is { } rawLine)
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;

                JsonNode? frame;
                try
                {
                    frame = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    continue; // skip a torn/partial frame rather than failing the payment
                }

                if (frame is not JsonObject frameObj) continue;

                if (frameObj["error"] is { } error)
                {
                    var message = error is JsonObject errObj
                        ? StringOrNull(errObj["message"]) ?? error.ToJsonString()
                        : error.ToJsonString();
                    Console.Error.WriteLine("[LND] Payment stream error");
                    return new RouterOutcome(false,
                        NwcPaymentResult.Failed("STREAM_ERROR", $"LND payment stream error: {message}"),
                        null);
                }

                if (frameObj["result"] is JsonObject result)
                {
                    lastPayment = result;
                    var status = (StringOrNull(result["status"]) ?? "").ToUpperInvariant();
                    if (status is "SUCCEEDED" or "FAILED") break;
                }
            }

            return new RouterOutcome(false, null, lastPayment);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The request WAS submitted, so this is pending, not failed. Bounded by
            // construction: the alternative (waiting forever on a stalled read) is the hang
            // this bound exists to prevent.
            Console.Error.WriteLine($"[LND] No final payment status within {ClientReadTimeout.TotalSeconds:0}s - reporting pending");
            return new RouterOutcome(false,
                NwcPaymentResult.Pending(
                    PaymentHashHexFromBolt11(bolt11) ?? "unknown",
                    $"LND did not report a final payment status within {ClientReadTimeout.TotalSeconds:0}s. " +
                    "The payment may still be in flight - do NOT retry it; check its status on your node (lncli listpayments)."),
                null);
        }
    }

    /// <summary>
    /// Pays via the pre-routerrpc lnrpc.SendPaymentSync route (old nodes only).
    /// </summary>
    private async Task<NwcPaymentResult> LegacySendPaymentAsync(string bolt11, CancellationToken cancellationToken)
    {
        var request = new { payment_request = bolt11 };
        var response = await _httpClient.PostAsJsonAsync(LegacySendPath, request, JsonOptions, cancellationToken);

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

        // The legacy route returns the preimage as base64 - convert to hex. payment_preimage
        // is read as a raw JsonNode (not a typed string) so a non-string value (e.g. a JSON
        // number) does NOT throw JsonException at deserialization -> generic catch ->
        // Failed("EXCEPTION") -> retryable -> double-pay. Any non-string / missing / empty
        // value falls through to the settled-but-no-preimage branch below, exactly like NWC.
        string? preimageB64 = null;
        if (result.PaymentPreimage is JsonValue preimageValue
            && preimageValue.TryGetValue<string>(out var preimageStr))
        {
            preimageB64 = preimageStr;
        }

        if (!string.IsNullOrEmpty(preimageB64))
        {
            byte[] preimageBytes;
            try
            {
                preimageBytes = Convert.FromBase64String(preimageB64);
            }
            catch (FormatException)
            {
                // The payment SETTLED (LND reported no payment_error) - the funds are gone.
                // A preimage that is not decodable base64 is settled-but-unprovable, NOT a
                // failure (a failure invites a double-pay). Deliberately does not echo the
                // offending value.
                Console.Error.WriteLine("[LND] WARNING: Preimage is not valid base64 - L402 verification will NOT work");
                return NwcPaymentResult.SucceededWithoutPreimage(
                    result.PaymentHash ?? "unknown",
                    "LND returned a preimage that is not valid base64. The payment settled, " +
                    "but L402/MPP verification is not possible without a real preimage.");
            }

            return PreimageFromSettledHex(Convert.ToHexString(preimageBytes).ToLowerInvariant(), result.PaymentHash);
        }

        // The payment SETTLED (LND reported no payment_error) - the funds are gone. A settled
        // payment with no preimage is settled-but-unprovable, NOT a failure - returning Failed
        // made the consumer retry and pay twice (and the budget under-count).
        Console.Error.WriteLine("[LND] WARNING: Settled with no preimage - L402 verification will NOT work");
        return NwcPaymentResult.SucceededWithoutPreimage(
            result.PaymentHash ?? "unknown",
            "LND returned no preimage. The payment settled, but L402/MPP verification " +
            "is not possible without a real preimage.");
    }

    /// <summary>
    /// Gate for the one field L402 treats as proof of payment. The payment has SETTLED by
    /// the time this runs, so anything that is not a real preimage is settled-but-
    /// UNPROVABLE (terminal), never a payment failure - a failure would invite a retry
    /// that pays twice.
    /// </summary>
    private static NwcPaymentResult PreimageFromSettledHex(string? preimageHex, string? paymentHash)
    {
        var candidate = preimageHex?.Trim().ToLowerInvariant();

        // LND "always returns a preimage" in practice, but practice is not a guard:
        // anything that is not 32 bytes hex-encoded cannot be a preimage, and the all-zero
        // value is LND's explicit "no proof here" sentinel - it passes a length/hex check,
        // so it has to be rejected by name.
        if (!Preimage.IsValid(candidate) || candidate == ZeroPreimageHex)
        {
            // Deliberately does not echo the offending value (engineering standard #5:
            // never log preimage-position content).
            Console.Error.WriteLine("[LND] WARNING: Response is not a valid 64-char hex preimage - L402 verification will NOT work");
            return NwcPaymentResult.SucceededWithoutPreimage(
                paymentHash ?? "unknown",
                "LND returned a value that is not a valid 64-character hex preimage. " +
                "The payment settled, but L402/MPP verification is not possible without a real preimage.");
        }

        Console.Error.WriteLine("[LND] Payment succeeded, preimage received");
        return NwcPaymentResult.Succeeded(candidate!);
    }

    /// <summary>
    /// Routing-fee ceiling for one payment. Never 0: SendPaymentV2 reads a 0 limit as
    /// "consider only zero-fee routes", which silently fails most real payments.
    /// </summary>
    internal long FeeLimitSats(string bolt11)
    {
        if (_feeLimitSatsOverride is { } fixedLimit)
        {
            return fixedLimit;
        }

        var amountSats = Bolt11Parser.ExtractAmountSats(bolt11) ?? 0;
        if (amountSats <= 0)
        {
            return MinFeeLimitSats;
        }

        var percent = (amountSats * DefaultFeeLimitPercent + 99) / 100; // ceil
        return Math.Max(percent, MinFeeLimitSats);
    }

    private static string? PaymentHashHexFromBolt11(string bolt11)
    {
        var hash = NwcWalletService.ExtractPaymentHashFromBolt11(bolt11);
        return hash is null ? null : Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? StringOrNull(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s) ? s : null;

    /// <summary>Reads a positive integer from the environment; null when unset or invalid.</summary>
    private static int? PositiveIntEnv(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name)?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        if (!int.TryParse(raw, out var value))
        {
            Console.Error.WriteLine($"[LND] Ignoring {name}: not an integer");
            return null;
        }
        if (value <= 0)
        {
            Console.Error.WriteLine($"[LND] Ignoring {name}: must be positive");
            return null;
        }
        return value;
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

        // Raw JsonNode, not string: a non-string payment_preimage (e.g. a JSON number)
        // must not throw JsonException at deserialization — it is classified as an
        // unusable preimage on a settled payment, not a hard (retryable) failure.
        [JsonPropertyName("payment_preimage")]
        public JsonNode? PaymentPreimage { get; set; }

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

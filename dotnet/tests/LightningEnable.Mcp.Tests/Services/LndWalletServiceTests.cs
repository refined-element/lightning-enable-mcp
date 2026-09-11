using System.Net;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using FluentAssertions;
using System.Text.Json;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// The LND wallet boundary must validate preimages as 64-char hex.
///
/// LND normally always returns a real preimage — but "normally" is not a guard.
/// This boundary base64-decoded whatever arrived and hex-encoded it, publishing
/// the result as PreimageHex without ever asking whether it was preimage-shaped.
/// </summary>
public class LndWalletServiceTests
{
    private const string ValidPreimageHex =
        "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef";

    /// <summary>
    /// A node that does NOT serve routerrpc: <c>/v2/router/send</c> answers 404 (so
    /// <c>PayInvoiceAsync</c> falls back to the legacy route) and every other request
    /// gets <paramref name="responseContent"/>. Used by the tests that assert the
    /// legacy route's base64 preimage semantics.
    /// </summary>
    private static RoutingHandler CreateLegacyOnlyHandler(string responseContent) =>
        new(req => req.RequestUri!.AbsolutePath.EndsWith(LndWalletService.RouterSendPath)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"code":5,"message":"Not Found"}""")
            }
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseContent)
            });

    /// <summary>
    /// Records every request and answers each with the supplied factory.
    /// </summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<(string Method, string Path, string Body)> Requests { get; } = new();

        public RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body));
            return _respond(request);
        }
    }

    /// <summary>
    /// Runs <paramref name="act"/> with LND configured, restoring env vars afterwards.
    /// </summary>
    private static async Task WithConfiguredLnd(
        string preimageBase64,
        Func<NwcPaymentResult, Task> assert)
    {
        var json = $$"""
            {"payment_preimage":"{{preimageBase64}}","payment_error":"","payment_hash":"aGFzaA=="}
            """;
        await WithLndResponse(json, assert);
    }

    /// <summary>
    /// Runs a pay_invoice against a fully custom LND JSON response body, restoring env
    /// vars afterwards. Lets a test drive shapes the preimage-only helper can't
    /// (a populated payment_error, a non-string preimage, etc.).
    /// </summary>
    private static async Task WithLndResponse(
        string responseJson,
        Func<NwcPaymentResult, Task> assert)
    {
        var originalHost = Environment.GetEnvironmentVariable("LND_REST_HOST");
        var originalMacaroon = Environment.GetEnvironmentVariable("LND_MACAROON_HEX");
        try
        {
            Environment.SetEnvironmentVariable("LND_REST_HOST", "localhost:8080");
            Environment.SetEnvironmentVariable("LND_MACAROON_HEX", "abc123");

            using var httpClient = new HttpClient(CreateLegacyOnlyHandler(responseJson));
            using var service = new LndWalletService(httpClient);

            var result = await service.PayInvoiceAsync("lnbc1000n1p3abcdef");
            await assert(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LND_REST_HOST", originalHost);
            Environment.SetEnvironmentVariable("LND_MACAROON_HEX", originalMacaroon);
        }
    }

    public static TheoryData<byte[]> WrongLengthPreimages => new()
    {
        new byte[] { 0xde, 0xad, 0xbe, 0xef },      // 4 bytes -> "deadbeef", not a preimage
        Enumerable.Repeat((byte)0x01, 31).ToArray(), // one byte short
        Enumerable.Repeat((byte)0x01, 33).ToArray(), // one byte long
    };

    [Theory]
    [MemberData(nameof(WrongLengthPreimages))]
    public async Task PayInvoice_WrongLengthPreimage_IsNeverPublishedAsProof(byte[] raw)
    {
        await WithConfiguredLnd(Convert.ToBase64String(raw), result =>
        {
            result.HasPreimage.Should().BeFalse(
                "a value that is not 32 bytes of hex cannot be a preimage");
            result.PreimageHex.Should().BeNull(
                "a non-preimage must never occupy the field L402 treats as proof of payment");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PayInvoice_WrongLengthPreimage_StillReportsSettled()
    {
        // The funds ARE gone. Reporting failure would invite a retry that pays twice.
        await WithConfiguredLnd(Convert.ToBase64String(new byte[] { 0xde, 0xad, 0xbe, 0xef }), result =>
        {
            result.Success.Should().BeTrue("the payment settled — it is simply unprovable");
            result.IsPending.Should().BeFalse();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PayInvoice_ValidPreimage_IsReturned()
    {
        var preimageBytes = Convert.FromHexString(ValidPreimageHex);
        await WithConfiguredLnd(Convert.ToBase64String(preimageBytes), result =>
        {
            result.Success.Should().BeTrue();
            result.HasPreimage.Should().BeTrue();
            result.PreimageHex.Should().Be(ValidPreimageHex);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PayInvoice_SettledButNoPreimage_IsSuccessNotFailure()
    {
        // LND reported no payment_error, so the payment SETTLED — the funds are gone.
        // The old code returned Failed("NO_PREIMAGE", ...) here (Success=false), which the
        // consumer (L402HttpClient) surfaces as "Payment failed" WITHOUT recording the
        // spend — the agent retries and pays twice, and the budget under-counts. A settled
        // payment with no preimage is settled-but-unprovable, NOT a failure: it must be
        // SucceededWithoutPreimage (Success=true, HasPreimage=false), matching the adjacent
        // invalid-format branch and the SucceededWithoutPreimage contract in NwcConfig.cs.
        await WithConfiguredLnd(preimageBase64: "", result =>
        {
            result.Success.Should().BeTrue("the payment settled — it is unprovable, not failed");
            result.HasPreimage.Should().BeFalse();
            result.PreimageHex.Should().BeNull();
            result.IsPending.Should().BeFalse();
            // payment_hash from the LND response is the reconciliation handle ("aGFzaA==").
            result.TrackingId.Should().Be("aGFzaA==");
            return Task.CompletedTask;
        });
    }

    // Non-empty values that are NOT decodable base64: Convert.FromBase64String throws
    // FormatException on each.
    public static TheoryData<string> MalformedBase64Preimages => new()
    {
        "not-valid-base64",  // '-' is outside the base64 alphabet
        "abc",               // length not a multiple of 4 -> invalid padding
        "@@@@",              // '@' is outside the base64 alphabet
    };

    [Theory]
    [MemberData(nameof(MalformedBase64Preimages))]
    public async Task PayInvoice_SettledButMalformedBase64Preimage_IsSuccessNotFailure(string malformedBase64)
    {
        // LND reported no payment_error, so the payment SETTLED — the funds are gone.
        // Convert.FromBase64String on a non-base64 value throws FormatException; left
        // unguarded (LndWalletService.cs:137) it fell through to the generic catch ->
        // Failed("EXCEPTION") -> a RETRYABLE failure -> the agent retries and pays
        // twice. A settled-but-undecodable preimage is settled-but-unprovable, NOT a
        // failure: it must be SucceededWithoutPreimage (Success=true, HasPreimage=false),
        // matching the no-preimage / invalid-hex branches and the Python LND fix.
        await WithConfiguredLnd(malformedBase64, result =>
        {
            result.Success.Should().BeTrue("the payment settled — it is unprovable, not failed");
            result.HasPreimage.Should().BeFalse();
            result.PreimageHex.Should().BeNull();
            result.IsPending.Should().BeFalse();
            // payment_hash from the LND response is the reconciliation handle ("aGFzaA==").
            result.TrackingId.Should().Be("aGFzaA==");
            return Task.CompletedTask;
        });
    }

    // A settled response (payment_error empty) whose payment_preimage is NOT a JSON
    // string. Reading it into the typed string? model threw JsonException at
    // deserialization -> generic catch -> Failed("EXCEPTION") -> retryable -> double-pay.
    public static TheoryData<string> SettledNonStringPreimageResponses => new()
    {
        """{"payment_preimage":12345,"payment_error":"","payment_hash":"aGFzaA=="}""",  // number
        """{"payment_preimage":true,"payment_error":"","payment_hash":"aGFzaA=="}""",   // bool
        """{"payment_preimage":[1,2],"payment_error":"","payment_hash":"aGFzaA=="}""",  // array
    };

    [Theory]
    [MemberData(nameof(SettledNonStringPreimageResponses))]
    public async Task PayInvoice_SettledButNonStringPreimage_IsSuccessNotFailure(string json)
    {
        // LND reported no payment_error, so the payment SETTLED — the funds are gone.
        // A non-string payment_preimage is settled-but-unprovable, NOT a failure. Reading
        // it into the typed string? model threw JsonException before payment_error could
        // even be inspected -> generic catch -> Failed("EXCEPTION") -> a RETRYABLE failure
        // -> double-pay. It must land on SucceededWithoutPreimage (Success=true,
        // HasPreimage=false) with the payment_hash as the reconciliation handle.
        await WithLndResponse(json, result =>
        {
            result.Success.Should().BeTrue("the payment settled — it is unprovable, not failed");
            result.HasPreimage.Should().BeFalse();
            result.PreimageHex.Should().BeNull();
            result.IsPending.Should().BeFalse();
            result.TrackingId.Should().Be("aGFzaA==");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PayInvoice_GenuinePaymentError_StaysRetryableFailure()
    {
        // No over-correction: LND reported a payment_error, so the payment did NOT
        // settle. This must stay a (retryable) failure and never be downgraded to a
        // settled-but-unprovable success.
        var json = """
            {"payment_preimage":"","payment_error":"insufficient_balance","payment_hash":"aGFzaA=="}
            """;
        await WithLndResponse(json, result =>
        {
            result.Success.Should().BeFalse();
            result.IsPending.Should().BeFalse();
            result.ErrorCode.Should().Be("PAYMENT_ERROR");
            return Task.CompletedTask;
        });
    }
    // -----------------------------------------------------------------------
    // routerrpc SendPaymentV2 - the supported payment route.
    //
    // LND REMOVED the legacy lnrpc.SendPaymentSync REST route
    // (POST /v1/channels/transactions). A modern node answers it with
    // 404 {"code":5,"message":"Not Found"} and never creates a payment, so EVERY
    // LND payment failed with HTTP_404 and no attempt recorded on the node
    // (verified against LND v0.21.3-beta). Payments must go to
    // POST /v2/router/send instead, falling back to the old route only when the
    // node does not serve v2 (a 404 means nothing was submitted, so the fallback
    // cannot double-pay).
    // -----------------------------------------------------------------------

    private const string ZeroPreimage =
        "0000000000000000000000000000000000000000000000000000000000000000";

    private const string PaymentHashHex =
        "abababababababababababababababababababababababababababababababab";

    /// <summary>One grpc-gateway server-streaming frame: {"result": &lt;lnrpc.Payment&gt;}.</summary>
    private static string Frame(string status, string? preimage = null, string? failureReason = null, string? paymentHash = null)
    {
        var payment = new Dictionary<string, object?> { ["status"] = status };
        if (preimage is not null) payment["payment_preimage"] = preimage;
        if (failureReason is not null) payment["failure_reason"] = failureReason;
        if (paymentHash is not null) payment["payment_hash"] = paymentHash;
        return JsonSerializer.Serialize(new { result = payment }) + "\n";
    }

    private static HttpResponseMessage Ndjson(params string[] frames) =>
        new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(frames)) };

    /// <summary>
    /// Runs one PayInvoiceAsync against a node whose /v2/router/send answers with
    /// <paramref name="routerResponse"/>; the legacy route answers with
    /// <paramref name="legacyResponse"/> (or fails the test when null, because a
    /// healthy node must never see it).
    /// </summary>
    private static async Task<(NwcPaymentResult Result, RoutingHandler Handler)> PayViaRouter(
        Func<HttpResponseMessage> routerResponse,
        Func<HttpResponseMessage>? legacyResponse = null,
        Action<LndWalletService>? configure = null,
        string bolt11 = "lnbc30n1p3abcdef")
    {
        var originalHost = Environment.GetEnvironmentVariable("LND_REST_HOST");
        var originalMacaroon = Environment.GetEnvironmentVariable("LND_MACAROON_HEX");
        try
        {
            Environment.SetEnvironmentVariable("LND_REST_HOST", "localhost:8080");
            Environment.SetEnvironmentVariable("LND_MACAROON_HEX", "abc123");

            var handler = new RoutingHandler(req =>
                req.RequestUri!.AbsolutePath.EndsWith(LndWalletService.RouterSendPath)
                    ? routerResponse()
                    : legacyResponse?.Invoke()
                      ?? throw new InvalidOperationException("unexpected legacy request to " + req.RequestUri));

            using var httpClient = new HttpClient(handler);
            using var service = new LndWalletService(httpClient);
            configure?.Invoke(service);

            var result = await service.PayInvoiceAsync(bolt11);
            return (result, handler);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LND_REST_HOST", originalHost);
            Environment.SetEnvironmentVariable("LND_MACAROON_HEX", originalMacaroon);
        }
    }

    [Fact]
    public async Task PayInvoice_PostsToRouterSendV2_NotTheRemovedV1Route()
    {
        var (result, handler) = await PayViaRouter(
            () => Ndjson(Frame("SUCCEEDED", preimage: ValidPreimageHex)));

        result.Success.Should().BeTrue();
        result.HasPreimage.Should().BeTrue();
        result.PreimageHex.Should().Be(ValidPreimageHex);

        handler.Requests.Should().HaveCount(1, "the removed route must not be touched at all on a healthy node");
        handler.Requests[0].Method.Should().Be("POST");
        handler.Requests[0].Path.Should().Be("/v2/router/send");
    }

    [Fact]
    public async Task PayInvoice_SendsANonZeroFeeLimitAndANodeSideTimeout()
    {
        // SendPaymentV2 considers ONLY zero-fee routes when fee_limit_sat is 0.
        var (_, handler) = await PayViaRouter(
            () => Ndjson(Frame("SUCCEEDED", preimage: ValidPreimageHex)));

        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var root = body.RootElement;
        root.GetProperty("payment_request").GetString().Should().Be("lnbc30n1p3abcdef");
        long.Parse(root.GetProperty("fee_limit_sat").GetString()!).Should().BePositive();
        // A node-side timeout keeps the call bounded even if the client never cancels.
        root.GetProperty("timeout_seconds").GetInt32().Should().BePositive();
    }

    [Fact]
    public async Task PayInvoice_FailedFrame_IsARetryableFailure()
    {
        var (result, _) = await PayViaRouter(
            () => Ndjson(Frame("FAILED", preimage: ZeroPreimage, failureReason: "FAILURE_REASON_NO_ROUTE")));

        result.Success.Should().BeFalse();
        result.IsPending.Should().BeFalse();
        result.ErrorMessage.Should().Contain("NO_ROUTE");
    }

    [Fact]
    public async Task PayInvoice_AllZeroPreimage_IsNeverReturnedAsProof()
    {
        // LND fills payment_preimage with 32 zero bytes when there is no proof. It is 64
        // valid hex characters, so a length/hex check alone accepts it - and the agent
        // would publish it as an L402 Authorization preimage for a payment it cannot prove.
        var (result, _) = await PayViaRouter(
            () => Ndjson(Frame("SUCCEEDED", preimage: ZeroPreimage, paymentHash: PaymentHashHex)));

        result.Success.Should().BeTrue("the payment settled - it is unprovable, not failed");
        result.HasPreimage.Should().BeFalse();
        result.PreimageHex.Should().BeNull();
        result.IsPending.Should().BeFalse();
        result.TrackingId.Should().Be(PaymentHashHex);
    }

    [Fact]
    public async Task PayInvoice_InFlightWithoutATerminalFrame_IsPendingNotFailed()
    {
        // Reporting an in-flight payment as failed invites a double-pay.
        var (result, _) = await PayViaRouter(
            () => Ndjson(Frame("IN_FLIGHT", preimage: ZeroPreimage)));

        result.IsPending.Should().BeTrue();
        result.Success.Should().BeFalse();
        result.HasPreimage.Should().BeFalse();
    }

    [Fact]
    public async Task PayInvoice_NoFramesAtAll_IsPendingNotFailed()
    {
        var (result, _) = await PayViaRouter(() => Ndjson());

        result.IsPending.Should().BeTrue();
        result.Success.Should().BeFalse();
    }

    /// <summary>A response body that never yields a byte - models a stalled LND stream.</summary>
    private sealed class HangingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            Task.Delay(Timeout.Infinite);

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new HangingStream());

        protected override bool TryComputeLength(out long length) { length = -1; return false; }

        private sealed class HangingStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task PayInvoice_AStalledStream_CannotHangTheAgentForever()
    {
        // The reported symptom: pay_invoice blocked for minutes with no result. A stalled
        // read must surface as a BOUNDED, non-retryable "pending" result, never an
        // unbounded await.
        var pay = PayViaRouter(
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new HangingContent() },
            configure: s => s.ClientReadTimeout = TimeSpan.FromSeconds(1)); // keep the test fast; same code path

        var finished = await Task.WhenAny(pay, Task.Delay(TimeSpan.FromSeconds(30)));
        finished.Should().BeSameAs(pay, "the client-side bound must fire");

        var (result, _) = await pay;
        result.IsPending.Should().BeTrue("the request WAS submitted, so this is pending, not failed");
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task PayInvoice_FallsBackToTheLegacyRoute_WhenV2IsAbsent()
    {
        // Old nodes without routerrpc: a 404 means nothing was submitted, so the fallback
        // is double-pay-safe.
        var preimageB64 = Convert.ToBase64String(Convert.FromHexString(ValidPreimageHex));
        var (result, handler) = await PayViaRouter(
            () => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"code":5,"message":"Not Found"}""")
            },
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"payment_preimage":"{{preimageB64}}","payment_error":""}""")
            });

        result.Success.Should().BeTrue();
        result.PreimageHex.Should().Be(ValidPreimageHex);
        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Path.Should().Be("/v1/channels/transactions");
    }

    [Fact]
    public async Task PayInvoice_V2HttpError_DoesNotFallBack()
    {
        // A non-404 error came from a route that EXISTS - retrying it on the legacy route
        // could submit the payment twice.
        var (result, handler) = await PayViaRouter(
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") });

        result.Success.Should().BeFalse();
        result.IsPending.Should().BeFalse();
        result.ErrorCode.Should().Be("HTTP_500");
        handler.Requests.Should().HaveCount(1, "the legacy route must not be tried after a non-404 error");
    }
    // -----------------------------------------------------------------------
    // TLS: LND serves a self-signed tls.cert. The doc header on LndWalletService has
    // advertised LND_TLS_CERT_PATH and LND_SKIP_TLS_VERIFY since the wallet shipped, but
    // neither was wired to the HttpClient, so a node whose cert is not in the OS trust
    // store failed the handshake before any route was reached.
    // -----------------------------------------------------------------------

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 SelfSigned(string cn)
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            $"CN={cn}", key, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void Tls_NeitherVarSet_UsesDefaultValidation()
    {
        LndWalletService.BuildServerCertificateValidator(null, null).Should().BeNull();
        LndWalletService.BuildServerCertificateValidator("false", "").Should().BeNull();
    }

    [Fact]
    public void Tls_SkipVerify_AcceptsAnyCertificate()
    {
        var validator = LndWalletService.BuildServerCertificateValidator("true", null);
        validator.Should().NotBeNull();
        using var cert = SelfSigned("lnd");
        validator!(new HttpRequestMessage(), cert, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors)
            .Should().BeTrue();
    }

    [Fact]
    public void Tls_CertPath_PinsExactlyThatCertificate()
    {
        using var lndCert = SelfSigned("lnd");
        using var other = SelfSigned("impostor");
        var path = Path.Combine(Path.GetTempPath(), $"lnd-tls-{Guid.NewGuid():N}.cert");
        File.WriteAllText(path, lndCert.ExportCertificatePem());
        try
        {
            // Pinning wins even when skip is also set.
            var validator = LndWalletService.BuildServerCertificateValidator("true", path);
            validator.Should().NotBeNull();
            var chainErrors = System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors;
            validator!(new HttpRequestMessage(), lndCert, null, chainErrors).Should().BeTrue("the pinned cert is trusted despite being self-signed");
            validator(new HttpRequestMessage(), other, null, chainErrors).Should().BeFalse("any other cert is rejected");
            validator(new HttpRequestMessage(), null, null, chainErrors).Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }
    // -----------------------------------------------------------------------
    // Transport failures AFTER the 2xx headers arrived.
    //
    // Once LND has answered 2xx on /v2/router/send the node may already have accepted
    // the payment. An IOException from the body read (connection dropped mid-stream, a
    // torn chunk, a read timeout) proves nothing about the outcome, so it must surface
    // as Pending with the invoice payment hash for reconciliation - never as the
    // retryable Failed("HTTP_ERROR") / Failed("EXCEPTION"), which invites a double-pay.
    // -----------------------------------------------------------------------

    /// <summary>A real, signed mainnet BOLT11 for 30 sats whose payment hash is 0xab repeated 32 times.</summary>
    private const string SignedBolt11Ab =
        "lnbc300n1pj48ugqpp54w46h2at4w46h2at4w46h2at4w46h2at4w46h2at4w46h2at4w4sdqvve5hsar4wfjssp5ehxumnwdehxumnwdehxumnwdehxumnwdehxumnwdehxumnwdehxs06u9h7uafxwghsp89k25c54uyvlhvx6tqgdfxl4nsydtte98pj3pvglezl4hppm69wqmfwz6qhj7qfegnea04dsdp66ljkfau43ehuqqfftjap";

    private static readonly string PaymentHashAb = string.Concat(Enumerable.Repeat("ab", 32));

    /// <summary>Delivers the prefix bytes, then the body read throws the given error.</summary>
    private sealed class BrokenAfterHeadersContent : HttpContent
    {
        private readonly byte[] _prefix;
        private readonly Exception _error;

        public BrokenAfterHeadersContent(string prefix, Exception error)
        {
            _prefix = System.Text.Encoding.UTF8.GetBytes(prefix);
            _error = error;
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            throw new NotSupportedException("read via the content stream");

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new BrokenStream(_prefix, _error));

        protected override bool TryComputeLength(out long length) { length = -1; return false; }

        private sealed class BrokenStream : Stream
        {
            private readonly byte[] _prefix;
            private readonly Exception _error;
            private int _pos;

            public BrokenStream(byte[] prefix, Exception error) { _prefix = prefix; _error = error; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_pos >= _prefix.Length) throw _error;
                var n = Math.Min(count, _prefix.Length - _pos);
                Array.Copy(_prefix, _pos, buffer, offset, n);
                _pos += n;
                return n;
            }
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_pos >= _prefix.Length) return ValueTask.FromException<int>(_error);
                var n = Math.Min(buffer.Length, _prefix.Length - _pos);
                _prefix.AsMemory(_pos, n).CopyTo(buffer);
                _pos += n;
                return ValueTask.FromResult(n);
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    public static TheoryData<Exception> MidStreamErrors => new()
    {
        new IOException("connection reset by peer"),
        new HttpRequestException("the response ended prematurely"),
        new HttpIOException(HttpRequestError.ResponseEnded, "response ended"),
    };

    [Theory]
    [MemberData(nameof(MidStreamErrors))]
    public async Task PayInvoice_ReadErrorAfter2xxWithNoFrames_IsPendingWithTheInvoiceHash(Exception error)
    {
        var (result, handler) = await PayViaRouter(
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenAfterHeadersContent("", error) },
            bolt11: SignedBolt11Ab);

        result.IsPending.Should().BeTrue("a 2xx was observed, so the node may have accepted the payment");
        result.Success.Should().BeFalse();
        result.HasPreimage.Should().BeFalse();
        result.ErrorCode.Should().Be("PAYMENT_PENDING", "must not be the retryable HTTP_ERROR/EXCEPTION");
        result.TrackingId.Should().Be(PaymentHashAb);
        handler.Requests.Should().HaveCount(1, "no legacy-route retry after a 2xx");
    }

    [Theory]
    [MemberData(nameof(MidStreamErrors))]
    public async Task PayInvoice_ReadErrorAfterAnInFlightFrame_IsPendingNotFailed(Exception error)
    {
        var (result, handler) = await PayViaRouter(
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new BrokenAfterHeadersContent(Frame("IN_FLIGHT", preimage: ZeroPreimage), error)
            },
            bolt11: SignedBolt11Ab);

        result.IsPending.Should().BeTrue();
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("PAYMENT_PENDING");
        result.TrackingId.Should().Be(PaymentHashAb);
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task PayInvoice_ConnectErrorBeforeAnyResponse_IsStillAConnectionFailure()
    {
        // No 2xx was ever observed, so nothing was submitted: the retryable failure stands.
        var (result, _) = await PayViaRouter(
            () => throw new HttpRequestException("connection refused"),
            bolt11: SignedBolt11Ab);

        result.IsPending.Should().BeFalse();
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("HTTP_ERROR");
    }

    // -----------------------------------------------------------------------
    // Routing-fee ceiling: 5% of the invoice, ceil'd, floored at 2 sats, env-overridable.
    // -----------------------------------------------------------------------

    private static LndWalletService FeeService(string? feeLimitOverride)
    {
        var original = Environment.GetEnvironmentVariable("LND_FEE_LIMIT_SATS");
        try
        {
            Environment.SetEnvironmentVariable("LND_FEE_LIMIT_SATS", feeLimitOverride);
            return new LndWalletService(new HttpClient());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LND_FEE_LIMIT_SATS", original);
        }
    }

    [Theory]
    [InlineData("lnbc10u1p3abcdef", 50)]    // 1000 sats: 5% exactly
    [InlineData("lnbc10010n1p3abcdef", 51)] // 1001 sats: 50.05 -> ceil
    [InlineData("lnbc300n1p3abcdef", 2)]    // 30 sats: 1.5 -> ceil 2 == floor
    [InlineData("lnbc100n1p3abcdef", 2)]    // 10 sats: 0.5 -> ceil 1 -> floor 2
    [InlineData("lnbc10n1p3abcdef", 2)]     // 1 sat: floor
    public void FeeLimit_FivePercentCeil_WithATwoSatFloor(string bolt11, long expected)
    {
        using var service = FeeService(null);
        service.FeeLimitSats(bolt11).Should().Be(expected);
    }

    [Fact]
    public void FeeLimit_AmountlessOrUndecodableInvoice_GetsTheFloor()
    {
        using var service = FeeService(null);
        service.FeeLimitSats("lnbc1p3abcdef").Should().Be(LndWalletService.MinFeeLimitSats);
        service.FeeLimitSats("not-an-invoice").Should().Be(LndWalletService.MinFeeLimitSats);
    }

    [Fact]
    public void FeeLimit_EnvOverride_WinsOverThePercentage()
    {
        using var service = FeeService("7");
        service.FeeLimitSats("lnbc10u1p3abcdef").Should().Be(7);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("abc")]
    [InlineData("  ")]
    public void FeeLimit_InvalidOverride_IsIgnored(string bad)
    {
        // 0 means "zero-fee routes only" to SendPaymentV2, so it must never pass through.
        using var service = FeeService(bad);
        service.FeeLimitSats("lnbc10u1p3abcdef").Should().Be(50);
    }

    [Fact]
    public void Tls_UnreadableCertPath_FailsWithAClearConfigError()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"lnd-tls-missing-{Guid.NewGuid():N}.cert");
        var act = () => LndWalletService.BuildServerCertificateValidator(null, missing);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*LND_TLS_CERT_PATH*")
            .WithMessage($"*{missing}*");
    }
}

using System.Net.Http.Headers;
using System.Text;

namespace LightningEnable.Mcp.Services;

/// <summary>One answer from <c>/api/producer/claim</c>: status, raw body, and <c>Retry-After</c>.</summary>
public sealed record ClaimHttpResponse(int Status, string Body, int? RetryAfterSeconds = null);

/// <summary>
/// The producer claim endpoint (<c>GET</c>/<c>POST {api}/api/producer/claim</c>) on the
/// operator-configured Lightning Enable origin (<see cref="FirstPartyOrigin"/>), never a tool
/// argument.
/// </summary>
/// <remarks>
/// Deliberately NOT <see cref="IL402HttpClient"/>: that client pays a 402 on its own, and the
/// claim needs the 402 in hand so the tool can save the body and macaroon before paying and
/// redeem the same token later. Deliberately NOT <see cref="ILightningEnableApiService"/>
/// either: that client sends the configured merchant key on every request, and an
/// unauthenticated claim must create a NEW account rather than claim onto that one.
/// Redirects are not followed, so the <c>Authorization</c> header never leaves this origin.
/// </remarks>
public interface IProducerClaimClient
{
    /// <summary>The absolute claim URL, for recovery instructions.</summary>
    string ClaimUrl { get; }

    /// <summary><c>GET /api/producer/claim</c>: the free quote.</summary>
    Task<ClaimHttpResponse> GetQuoteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// <c>POST /api/producer/claim</c> with <paramref name="jsonBody"/> sent byte for byte.
    /// <paramref name="authorization"/> null is phase 1; <c>L402 mac:preimage</c> is phase 2.
    /// Transport failures throw (<see cref="HttpRequestException"/>, <see cref="TaskCanceledException"/>).
    /// </summary>
    Task<ClaimHttpResponse> PostAsync(string jsonBody, string? authorization, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class ProducerClaimClient : IProducerClaimClient
{
    private const string ClaimPath = "/api/producer/claim";
    private readonly HttpClient _http;

    public ProducerClaimClient(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public string ClaimUrl => FirstPartyOrigin.ResolveApiBaseUrl() + ClaimPath;

    public async Task<ClaimHttpResponse> GetQuoteAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ClaimUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent.Value);
        return await SendAsync(request, cancellationToken);
    }

    public async Task<ClaimHttpResponse> PostAsync(string jsonBody, string? authorization, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ClaimUrl)
        {
            // Bytes, not a re-serialized object: the paid token is bound to this body.
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(jsonBody))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent.Value);
        if (!string.IsNullOrEmpty(authorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }
        return await SendAsync(request, cancellationToken);
    }

    private async Task<ClaimHttpResponse> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        int? retryAfter = null;
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
        {
            retryAfter = (int)Math.Ceiling(delta.TotalSeconds);
        }
        return new ClaimHttpResponse((int)response.StatusCode, body, retryAfter);
    }
}

using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// HTTP client with L402 (Lightning 402) payment support.
/// Automatically handles 402 Payment Required responses.
/// </summary>
public interface IL402HttpClient
{
    /// <summary>
    /// Fetches a URL, automatically paying any L402 challenge if required.
    /// GENERIC path: the URL and method are caller-chosen, so only <c>GET</c> and
    /// <c>HEAD</c> are accepted (see <see cref="PaidHttpMethodGuard"/>); any other method
    /// is refused before any request is sent and before any payment.
    /// </summary>
    /// <param name="url">URL to fetch.</param>
    /// <param name="method">HTTP method: GET or HEAD (case/whitespace-insensitive).</param>
    /// <param name="headers">Optional headers as JSON object.</param>
    /// <param name="body">Optional request body.</param>
    /// <param name="maxSats">Maximum satoshis to pay.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Fetch result with content and payment details.</returns>
    Task<L402FetchResult> FetchWithL402Async(
        string url,
        string method = "GET",
        string? headers = null,
        string? body = null,
        long maxSats = 1000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// INTERNAL first-party path: POSTs a JSON body to a URL on the configured Lightning
    /// Enable API origin (<see cref="FirstPartyOrigin"/>), paying any L402 challenge.
    /// Not reachable through any tool argument — the URL is built by the calling tool from
    /// operator configuration, and any other origin is refused before any request is sent.
    /// This is how account bootstrap keeps working without widening the generic GET/HEAD rule.
    /// </summary>
    /// <param name="url">Absolute URL on the first-party API origin.</param>
    /// <param name="jsonBody">JSON request body.</param>
    /// <param name="maxSats">Maximum satoshis to pay.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<L402FetchResult> PostFirstPartyAsync(
        string url,
        string jsonBody,
        long maxSats = 1000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Manually pays an L402/MPP invoice and returns the token.
    /// </summary>
    /// <param name="macaroonBase64">Base64-encoded macaroon from L402 challenge. Null for MPP (preimage-only).</param>
    /// <param name="invoice">BOLT11 invoice to pay.</param>
    /// <param name="maxSats">Maximum satoshis to pay.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>L402 token (macaroon:preimage) or MPP preimage for use in Authorization header.</returns>
    Task<string> PayChallengeAsync(
        string? macaroonBase64,
        string invoice,
        long maxSats = 1000,
        CancellationToken cancellationToken = default);
}

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
    /// </summary>
    /// <param name="url">URL to fetch.</param>
    /// <param name="method">HTTP method (GET, POST, etc.).</param>
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
    /// Manually pays an L402 invoice and returns the token.
    /// </summary>
    /// <param name="macaroonBase64">Base64-encoded macaroon from challenge.</param>
    /// <param name="invoice">BOLT11 invoice to pay.</param>
    /// <param name="maxSats">Maximum satoshis to pay.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>L402 token (macaroon:preimage) for use in Authorization header.</returns>
    Task<string> PayChallengeAsync(
        string macaroonBase64,
        string invoice,
        long maxSats = 1000,
        CancellationToken cancellationToken = default);
}

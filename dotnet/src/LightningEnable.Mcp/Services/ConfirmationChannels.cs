using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Delivers an over-threshold confirmation code to the human operator — and ONLY to the
/// human operator. Every implementation is out-of-band with respect to the model: the code
/// never appears in a tool result on any channel.
/// </summary>
public interface IConfirmationChannel
{
    /// <summary>Which channel this is.</summary>
    ConfirmationChannelKind Kind { get; }

    /// <summary>
    /// Agent-safe, code-free description of where a delivered code went, for the tool result
    /// ("printed to the server console/logs", "sent to the operator approval webhook", ...).
    /// </summary>
    string OperatorHint { get; }

    /// <summary>
    /// Why this channel refuses to deliver at all, or null when it delivers. A non-null value
    /// means no code is ever minted — the payment is refused before a pending confirmation exists.
    /// </summary>
    string? RefusalReason { get; }

    /// <summary>Deliver the code out of band. Must not throw; report failures in the result.</summary>
    Task<ConfirmationDeliveryResult> DeliverAsync(
        PendingConfirmation pending, ConfirmationRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The JSON body the <c>webhook</c> and <c>file</c> channels share — one shape, so an operator
/// can tail a file in development and POST to their own system in production without
/// re-learning the payload. Carries the nonce, tool, amount (sats + USD), a destination
/// SUMMARY, and the expiry. It never carries a wallet credential, a preimage, or a macaroon.
/// </summary>
public static class ConfirmationPayload
{
    /// <summary>Longest destination prefix included in the payload. Enough to recognise, not the whole secret-ish blob.</summary>
    public const int DestinationSummaryLength = 40;

    /// <summary>Build the canonical one-line JSON body for a pending confirmation.</summary>
    public static string Build(PendingConfirmation pending, ConfirmationRequest request)
    {
        var expiresIn = (int)Math.Max(0, Math.Round((pending.ExpiresAt - pending.CreatedAt).TotalSeconds));
        return JsonSerializer.Serialize(new
        {
            type = "payment.confirmation_required",
            nonce = pending.Nonce,
            tool = pending.ToolName,
            amountSats = pending.AmountSats,
            amountUsd = Math.Round(pending.AmountUsd, 2),
            destination = Summarize(pending.Destination),
            description = request.Description,
            summary = request.Summary,
            createdAt = pending.CreatedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            expiresAt = pending.ExpiresAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            expiresInSeconds = expiresIn
        });
    }

    /// <summary>Truncate a destination to a recognisable prefix.</summary>
    public static string Summarize(string? destination)
    {
        var value = (destination ?? string.Empty).Trim();
        return value.Length <= DestinationSummaryLength
            ? value
            : value[..DestinationSummaryLength] + "...";
    }
}

/// <summary>
/// <c>X-LightningEnable-Signature: t={unix},v1={hex}</c> over <c>{t}.{body}</c> with HMAC-SHA256 —
/// the same scheme the Lightning Enable API signs its merchant webhooks with, so an operator who
/// already verifies those can reuse the code. The timestamp is inside the signed string, so a
/// captured POST cannot be replayed with a fresh timestamp.
/// </summary>
public static class ConfirmationWebhookSignature
{
    /// <summary>Compute the header value for a body at a given unix timestamp.</summary>
    public static string Build(string secret, long unixTimestamp, string body)
    {
        var signed = $"{unixTimestamp.ToString(CultureInfo.InvariantCulture)}.{body}";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signed));
        return $"t={unixTimestamp.ToString(CultureInfo.InvariantCulture)},v1={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}

/// <summary>
/// The historical local behaviour: print the code to stderr, where the human at the terminal
/// sees it and the model (which only sees tool results) does not. Unchanged by design.
/// </summary>
public sealed class StderrConfirmationChannel : IConfirmationChannel
{
    private readonly TextWriter? _writer;

    /// <param name="writer">Test seam. Null means <see cref="Console.Error"/>, read at call time.</param>
    public StderrConfirmationChannel(TextWriter? writer = null) => _writer = writer;

    public ConfirmationChannelKind Kind => ConfirmationChannelKind.Stderr;

    public string OperatorHint => "printed to the server console/logs";

    public string? RefusalReason => null;

    public Task<ConfirmationDeliveryResult> DeliverAsync(
        PendingConfirmation pending, ConfirmationRequest request, CancellationToken cancellationToken)
    {
        var writer = _writer ?? Console.Error;
        writer.WriteLine(
            $"[Lightning Enable] *** {request.Title} ***\n" +
            $"  {request.Summary}\n" +
            $"  Confirmation code: {pending.Nonce}\n" +
            $"  To approve, give this code to the agent. Expires in {(int)Math.Round((pending.ExpiresAt - pending.CreatedAt).TotalSeconds)}s.");
        return Task.FromResult(ConfirmationDeliveryResult.Ok());
    }
}

/// <summary>
/// Refuse over-threshold payments instead of minting a code. No pending confirmation is ever
/// created on this channel — see <c>BudgetService.RequestConfirmationAsync</c>, which
/// short-circuits before minting.
/// </summary>
public sealed class RefusingConfirmationChannel : IConfirmationChannel
{
    private readonly string _reason;

    public RefusingConfirmationChannel(string reason) => _reason = reason;

    /// <summary>The default refusal an operator sees when they chose <c>refuse</c> deliberately.</summary>
    public static RefusingConfirmationChannel Configured() => new(
        "This payment needs human approval, but this server has no approval channel "
        + "(confirmation.channel = \"refuse\"), so it was refused rather than approved. No confirmation code exists "
        + "for the agent to ask for. To allow payments this size, the OPERATOR must either raise tiers.autoApprove in "
        + "~/.lightning-enable/config.json, or configure an approval channel — confirmation.channel = \"webhook\" "
        + "(with confirmation.webhookUrl and confirmation.webhookSecret) or \"file\" (with confirmation.filePath).");

    public ConfirmationChannelKind Kind => ConfirmationChannelKind.Refuse;

    public string OperatorHint => "not delivered — over-threshold payments are refused on this server";

    public string? RefusalReason => _reason;

    public Task<ConfirmationDeliveryResult> DeliverAsync(
        PendingConfirmation pending, ConfirmationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ConfirmationDeliveryResult.Fail(_reason));
}

/// <summary>
/// Append the confirmation to a JSONL file the operator tails. POSIX permissions are pinned to
/// 0600 on every write: the file holds live approval codes, so a world-readable log of it would
/// hand anyone on the box the ability to approve payments.
/// </summary>
public sealed class FileConfirmationChannel : IConfirmationChannel
{
    private readonly string _path;
    private readonly object _writeLock = new();

    public FileConfirmationChannel(string path) => _path = path;

    /// <summary>Where confirmations land when no path is configured.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".lightning-enable",
        "confirmations.jsonl");

    public ConfirmationChannelKind Kind => ConfirmationChannelKind.File;

    /// <summary>The file this channel appends to.</summary>
    public string FilePath => _path;

    public string OperatorHint => $"appended to the approval file at {_path}";

    public string? RefusalReason => null;

    public Task<ConfirmationDeliveryResult> DeliverAsync(
        PendingConfirmation pending, ConfirmationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var line = ConfirmationPayload.Build(pending, request);
            lock (_writeLock)
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(_path, line + '\n', new UTF8Encoding(false));
                RestrictPermissions(_path);
            }

            return Task.FromResult(ConfirmationDeliveryResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(ConfirmationDeliveryResult.Fail(
                $"could not append to the approval file at {_path}: {ex.Message}"));
        }
    }

    /// <summary>0600 on POSIX. Windows inherits the user profile's ACL; nothing portable to do.</summary>
    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            // Best effort: the line is already written and refusing the payment because chmod
            // failed would be worse than a warning. Say so loudly rather than silently.
            Console.Error.WriteLine(
                $"[Lightning Enable] Warning: could not restrict permissions on {path}: {ex.Message}");
        }
    }
}

/// <summary>
/// POST the pending confirmation to an operator-controlled URL, signed with HMAC-SHA256. The
/// human approves through their own system and relays the code back to the agent exactly as
/// they would from a terminal.
/// <para/>
/// The client is the SSRF-guarded one wired in <c>Program.cs</c>: connect-time IP validation and
/// <c>AllowAutoRedirect = false</c>. Redirects are additionally rejected HERE, so a 3xx can never
/// bounce a signed approval payload to a host the operator did not configure — whatever the
/// handler is configured to do. A delivery failure REFUSES the payment; it never approves it.
/// </summary>
public sealed class WebhookConfirmationChannel : IConfirmationChannel
{
    private readonly Func<HttpClient> _clientFactory;
    private readonly string _url;
    private readonly string _secret;
    private readonly TimeSpan _timeout;

    public WebhookConfirmationChannel(
        Func<HttpClient> clientFactory, string url, string secret, TimeSpan? timeout = null)
    {
        _clientFactory = clientFactory;
        _url = url;
        _secret = secret;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public ConfirmationChannelKind Kind => ConfirmationChannelKind.Webhook;

    public string Url => _url;

    public string OperatorHint => "sent to the operator's approval webhook";

    public string? RefusalReason => null;

    public async Task<ConfirmationDeliveryResult> DeliverAsync(
        PendingConfirmation pending, ConfirmationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var body = ConfirmationPayload.Build(pending, request);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            using var message = new HttpRequestMessage(HttpMethod.Post, _url)
            {
                Content = new StringContent(body, Encoding.UTF8)
            };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            message.Headers.TryAddWithoutValidation(
                "X-LightningEnable-Signature", ConfirmationWebhookSignature.Build(_secret, timestamp, body));
            message.Headers.TryAddWithoutValidation("User-Agent", "LightningEnable-MCP/1.0");

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_timeout);

            var client = _clientFactory();
            using var response = await client.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);

            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                // Never chase a redirect with a signed approval payload.
                return ConfirmationDeliveryResult.Fail(
                    $"the approval webhook answered with a {status} redirect, which is never followed "
                    + "(a signed approval must go only to the configured URL)");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ConfirmationDeliveryResult.Fail($"the approval webhook answered HTTP {status}");
            }

            return ConfirmationDeliveryResult.Ok();
        }
        catch (Exception ex)
        {
            return ConfirmationDeliveryResult.Fail($"the approval webhook could not be reached: {ex.Message}");
        }
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using LightningEnable.Mcp.Services;

namespace LightningEnable.Mcp.Tools;

/// <summary>One ownership-verification method: what it proves and when to choose it.</summary>
internal sealed record VerificationMethodInfo(string Method, string Proves, string ChooseWhen);

/// <summary>A plain-language next step for one kind of failed-check reason.</summary>
/// <param name="Contains">The reason substring that selects this hint.</param>
/// <param name="Method">The method the hint is about, or null for a hint about the target itself.</param>
/// <param name="Hint">What to do.</param>
internal sealed record VerificationReasonHint(string Contains, string? Method, string Hint);

/// <summary>
/// Ownership verification for <c>l402_producer</c>: <c>verify</c> with a <c>proxyId</c>, and
/// <c>verification_token</c>.
///
/// <para>A proxy owner proves they control the API behind a proxy by publishing a token in a
/// well-known file, a DNS TXT record, or a response header, and asking Lightning Enable to look
/// for it. Only a verified proxy can be listed publicly; an unverified one keeps working. The
/// server side is <c>ProxyVerificationController</c>: <c>GET /api/proxy/{id}/verification</c>,
/// <c>POST .../verification/token</c> and <c>POST /api/proxy/{id}/verify</c>, all on the merchant
/// API key the other producer actions use.</para>
///
/// <para><b>The token.</b> The server returns it once and stores only a hash. This returns it to
/// the caller once, in its own field, and never logs it. Every instruction value keeps the
/// server's <c>&lt;token&gt;</c> placeholder.</para>
///
/// <para>The method table, error codes, polling bounds and reason hints are pinned against
/// <c>shared/l402-producer-verify.json</c> so both ports agree. Mirrors
/// <c>python/.../tools/producer_verify.py</c>.</para>
/// </summary>
internal static class ProducerVerification
{
    public const int MaxPendingAttempts = 4;
    public const int MaxRetryAfterSeconds = 10;

    public const string ProxyIdRequired = "proxy_id_required";
    public const string InvalidMethod = "invalid_method";
    public const string AmbiguousVerify = "ambiguous_verify";
    public const string ReissueRequired = "reissue_required";
    public const string TokenRequired = "verification_token_required";
    public const string Unauthorized = "unauthorized";
    public const string ProxyNotFound = "proxy_not_found";
    public const string FeatureNotEnabled = "feature_not_enabled";
    public const string RateLimited = "rate_limited";
    public const string RequestRejected = "request_rejected";
    public const string UpstreamError = "upstream_error";

    /// <summary>Every error code this surface returns, and whether retrying can help.</summary>
    public static readonly IReadOnlyDictionary<string, bool> ErrorCodes = new Dictionary<string, bool>
    {
        [ProxyIdRequired] = false,
        [InvalidMethod] = false,
        [AmbiguousVerify] = false,
        [ReissueRequired] = false,
        [TokenRequired] = false,
        [Unauthorized] = false,
        [ProxyNotFound] = false,
        [FeatureNotEnabled] = false,
        [RateLimited] = true,
        [RequestRejected] = false,
        [UpstreamError] = true,
    };

    /// <summary>The three methods, in the order the server tries them.</summary>
    public static readonly IReadOnlyList<VerificationMethodInfo> Methods = new[]
    {
        new VerificationMethodInfo(
            "well-known",
            "that you control the web root of the target host",
            "you control the web server for the target's hostname and can add a file at its root"),
        new VerificationMethodInfo(
            "dns-txt",
            "that you control the DNS zone for the target's hostname",
            "someone else hosts the API but you manage DNS for its hostname"),
        new VerificationMethodInfo(
            "header",
            "only that you control the API's responses at the target base URL",
            "you own the API's code but not the web root or DNS"),
    };

    /// <summary>
    /// Methods a live check produces. <c>grandfathered</c> and <c>platform</c> are not: a new
    /// token does not unlist those, so only these need <c>reissue</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> LiveMethods = Methods.Select(m => m.Method).ToArray();

    /// <summary>
    /// Reason substrings (from <c>ProxyVerificationService</c>) and what to do about each. Order
    /// matters: a method gets the FIRST hint that matches it, so the narrower TXT text sits
    /// before the broader one.
    /// </summary>
    public static readonly IReadOnlyList<VerificationReasonHint> ReasonHints = new[]
    {
        new VerificationReasonHint("The target host was refused before any request", null,
            "No method ran: the target host resolves to a private or internal address, or does not resolve. "
            + "Point the proxy at the API's public HTTPS address."),
        new VerificationReasonHint("The target URL cannot be verified", null,
            "No method ran: the target base URL uses an IP address, localhost or an internal domain, a port other "
            + "than 80 or 443, or a scheme other than http or https. Update the proxy's target to the API's public "
            + "hostname, then issue a token and verify again."),
        new VerificationReasonHint("was re-issued while this check ran", null,
            "The token was replaced while the check ran. Publish the newest token and call verify again."),
        new VerificationReasonHint("which is not followed", null,
            "Redirects are never followed. Serve the file or header on the target host itself, make the base URL "
            + "answer without a trailing-slash redirect, and exempt the path from any sign-in redirect."),
        new VerificationReasonHint("the host check did not finish", null,
            "The target host could not be looked up in time. Call verify again; if it persists, check the "
            + "hostname's DNS."),
        new VerificationReasonHint("the host check failed", null,
            "The target host could not be looked up in time. Call verify again; if it persists, check the "
            + "hostname's DNS."),
        new VerificationReasonHint("no answer within", null,
            "The target answered too slowly. Make the file or base URL answer quickly without waking a cold-start "
            + "backend, or use dns-txt, which does not depend on your server's response time."),
        new VerificationReasonHint("could not be reached", null,
            "The request did not complete. Check that the host answers HTTPS on port 443 with a valid, publicly "
            + "trusted certificate and that a firewall does not block outside requests."),
        new VerificationReasonHint("lightning-enable.json answered HTTP", "well-known",
            "The file is not served at the target host's /.well-known/lightning-enable.json, or a CDN still serves "
            + "a cached 404. Check the path on the target host itself and purge the cache."),
        new VerificationReasonHint("is not valid JSON", "well-known",
            "Serve exactly {\"verify\":\"<token>\"}, with straight quotes and no comments."),
        new VerificationReasonHint("has no \"verify\" field", "well-known",
            "The key must be verify, in lowercase."),
        new VerificationReasonHint("must be a string or an array of strings", "well-known",
            "Put the token in quotes, or in an array of quoted tokens."),
        new VerificationReasonHint("is larger than", "well-known",
            "Serve only the verify field; the file must stay under 4 KB."),
        new VerificationReasonHint("\"verify\" does not contain this proxy's current token", "well-known",
            "The file's token is old, mistyped, or cached. Publish the token you most recently issued, or issue a "
            + "new one."),
        new VerificationReasonHint("contains this proxy's current token", "dns-txt",
            "A TXT record exists, but none of its values is exactly the current token. Remove quotes, prefixes and "
            + "old tokens, or issue a new token."),
        new VerificationReasonHint("no TXT record at", "dns-txt",
            "No TXT record is visible yet. A new record can take minutes to propagate; check with dig +short TXT on "
            + "the record name. Many DNS providers append your zone, so enter only the part before it."),
        new VerificationReasonHint("does not carry this proxy's current token", "header",
            "The header is there, but its value is not the current token. Publish the token you most recently "
            + "issued."),
        new VerificationReasonHint("without X-Lightning-Enable-Verify", "header",
            "The base URL answered without the header. Add it to every response at the base URL, including error "
            + "responses and HEAD."),
    };

    public const string ReasonHintFallback =
        "Fix what the reason names for the method you published, then call verify again. Only that method needs "
        + "to pass; the others failing is expected.";

    private const string About =
        "Ownership verification proves you control the API behind the proxy. Only a verified proxy can be listed "
        + "publicly (l402_producer action=publish); an unverified one keeps working and taking payments by its "
        + "direct URL.";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>State fields copied from the server's answer. An allowlist: never <c>token</c>.</summary>
    private static readonly string[] StateFields =
    {
        "proxyId", "verified", "method", "verifiedAt", "lastCheckedAt", "failures", "tokenIssued",
        "listed", "nextCheckAt", "checkedAt", "reason", "pending", "retryAfterSeconds",
    };

    private static readonly string[] InstructionFields =
    {
        "summary", "url", "recordName", "recordType", "headerName", "body", "value",
    };

    // ── verify ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the ownership check, polling a <c>pending</c> answer up to
    /// <see cref="MaxPendingAttempts"/> checks, each wait honoring <c>retryAfterSeconds</c>
    /// capped at <see cref="MaxRetryAfterSeconds"/>.
    /// </summary>
    /// <param name="delay">Test seam for the wait between checks; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public static async Task<string> VerifyAsync(
        string? proxyId,
        string? method,
        ILightningEnableApiService? apiService,
        Func<TimeSpan, CancellationToken, Task>? delay,
        CancellationToken cancellationToken,
        string? configPath = null)
    {
        var gate = Gate(apiService, proxyId, method, out var slug, out var filter);
        if (gate is not null)
        {
            return gate;
        }

        delay ??= Task.Delay;

        try
        {
            JsonObject state;
            var attempts = 0;
            while (true)
            {
                attempts++;
                var result = await apiService!.VerifyProxyOwnershipAsync(slug, cancellationToken);
                if (!result.Success)
                {
                    return Error(result, slug, apiService);
                }

                state = result.Data ?? new JsonObject();
                if (Bool(state, "pending") != true || attempts >= MaxPendingAttempts)
                {
                    break;
                }

                var wait = Math.Clamp(
                    Number(state, "retryAfterSeconds") ?? result.RetryAfterSeconds ?? 1, 1, MaxRetryAfterSeconds);
                await delay(TimeSpan.FromSeconds(wait), cancellationToken);
            }

            var payload = StatePayload(state, slug, filter);
            payload["attempts"] = attempts;
            var steps = NextSteps(state, slug, filter, attempts);
            var stored = Bool(state, "verified") == true ? null : LoadToken(configPath, slug);
            if (stored is not null)
            {
                // Use the token already issued (by sell_url or verification_token) rather than
                // sending the agent to verification_token, which would replace it.
                payload["storedToken"] = new Dictionary<string, object?>
                {
                    ["token"] = stored.Token,
                    ["path"] = stored.Path,
                    ["source"] = stored.Source,
                };
                steps.Insert(0, $"Publish the token stored in {stored.Path} (token above); you do not need a new "
                    + "one. A new token from verification_token would replace it.");
            }
            payload["nextSteps"] = steps;
            return Json(payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(UpstreamError, $"The verification check could not be completed: {ex.Message}");
        }
    }

    // ── verification_token ───────────────────────────────────────────────────

    /// <summary>
    /// Issues the verification token and returns it once, with the instructions to publish it.
    /// </summary>
    /// <remarks>
    /// Reads the state first: re-issuing replaces the token and makes a proxy verified by a live
    /// method unverified and unlisted, so that needs <paramref name="reissue"/>.
    /// </remarks>
    public static async Task<string> TokenAsync(
        string? proxyId,
        string? method,
        bool reissue,
        ILightningEnableApiService? apiService,
        CancellationToken cancellationToken,
        string? configPath = null)
    {
        var gate = Gate(apiService, proxyId, method, out var slug, out var filter);
        if (gate is not null)
        {
            return gate;
        }

        try
        {
            var before = await apiService!.GetProxyVerificationAsync(slug, cancellationToken);
            if (!before.Success)
            {
                return Error(before, slug, apiService);
            }

            var prior = before.Data ?? new JsonObject();
            var priorMethod = Text(prior, "method");
            var liveVerified = Bool(prior, "verified") == true
                && priorMethod is not null && LiveMethods.Contains(priorMethod);
            if (liveVerified && !reissue)
            {
                var listed = Bool(prior, "listed") == true ? " and listed" : string.Empty;
                return Fail(ReissueRequired,
                    $"Proxy {slug} is already verified by {priorMethod}{listed}, so it needs no new token. A new token "
                    + "replaces the published one and makes the proxy unverified and unlisted until you publish the "
                    + "new token and verify again. Call verification_token with reissue=true only to rotate it.");
            }

            var issued = await apiService.IssueProxyVerificationTokenAsync(slug, cancellationToken);
            if (!issued.Success)
            {
                return Error(issued, slug, apiService);
            }

            var state = issued.Data ?? new JsonObject();
            var token = Text(state, "token");
            if (string.IsNullOrEmpty(token))
            {
                return Fail(UpstreamError,
                    "The server answered without a token. Call verification_token again to issue one.");
            }

            var storage = StoreToken(configPath, slug, token, "verification_token");
            var payload = new Dictionary<string, object?>
            {
                ["success"] = true,
                ["proxyId"] = Text(state, "proxyId") ?? slug,
                ["token"] = token,
                ["tokenNote"] = "This token is shown only this once here, and Lightning Enable keeps only a hash of "
                    + $"it. {StorageNote(storage)} Publish it by one method below, replacing <token>, then call "
                    + $"l402_producer action=verify proxy_id={slug}. Issuing another token replaces this one.",
                ["tokenStorage"] = StorageJson(storage),
            };
            foreach (var (key, value) in StatePayload(state, slug, filter))
            {
                payload.TryAdd(key, value);
            }

            var warnings = new List<string>();
            if (Bool(prior, "tokenIssued") == true)
            {
                warnings.Add("This replaced the earlier token, which no longer verifies. Publish this one in its place.");
            }
            if (liveVerified)
            {
                warnings.Add($"The proxy was verified by {priorMethod}; it is now unverified and unlisted until verify "
                    + "succeeds with this token, and listing must then be turned on again with publish.");
            }
            else if (priorMethod is "grandfathered" or "platform")
            {
                warnings.Add($"The proxy keeps its {priorMethod} standing until a check with this token succeeds.");
            }
            if (warnings.Count > 0)
            {
                payload["warnings"] = warnings;
            }

            payload["nextSteps"] = new List<string>
            {
                "Pick one method from methods, publish the token there in place of <token>, then call "
                + $"l402_producer action=verify proxy_id={slug}.",
                "Once verified, turn listing on with l402_producer action=publish.",
            };
            return Json(payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(UpstreamError, $"The verification token could not be issued: {ex.Message}");
        }
    }

    /// <summary>
    /// The <c>verify</c> action carries two meanings: a payer's macaroon and preimage, or a
    /// <c>proxyId</c> to check ownership of. Returns true when this call is the ownership one.
    /// </summary>
    public static bool IsOwnershipVerify(string? proxyId, string? macaroon, string? preimage)
        => !string.IsNullOrWhiteSpace(proxyId)
            && string.IsNullOrWhiteSpace(macaroon)
            && string.IsNullOrWhiteSpace(preimage);

    /// <summary>The refusal for a <c>verify</c> call that names both a payment and a proxy.</summary>
    public static bool IsAmbiguousVerify(string? proxyId, string? macaroon, string? preimage)
        => !string.IsNullOrWhiteSpace(proxyId)
            && (!string.IsNullOrWhiteSpace(macaroon) || !string.IsNullOrWhiteSpace(preimage));

    public static string AmbiguousVerifyResult() => Fail(AmbiguousVerify,
        "verify takes either macaroon and preimage (check a payment) or proxy_id (check ownership of your API), "
        + "not both. Call it once for each.");

    // ── Token store ──────────────────────────────────────────────────────────
    //
    // The server shows a verification token once. Like sell_url's API key, it is kept in an
    // owner-only file beside config.json, verification-<proxyId>.json, so verify can point at
    // it later instead of issuing a new one (which would replace the published token).

    public const string TokenFilePrefix = "verification-";

    /// <summary>Where a token was written, or why it was not.</summary>
    internal sealed record TokenStorage(bool Written, string Path, string? Error);

    /// <summary>A token read back from its file.</summary>
    internal sealed record StoredToken(string Token, string Path, string? Source);

    /// <summary>
    /// <c>verification-&lt;proxyId&gt;.json</c> beside config.json; the id lowercased, anything but
    /// <c>a-z0-9-</c> replaced by <c>-</c> (the same rule as <c>claim-&lt;proxyId&gt;.key</c>).
    /// </summary>
    internal static string TokenFilePath(string configPath, string proxyId)
    {
        var safeId = System.Text.RegularExpressions.Regex.Replace(proxyId.ToLowerInvariant(), "[^a-z0-9-]", "-");
        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(configPath) ?? ".", $"{TokenFilePrefix}{safeId}.json");
    }

    internal static string ConfigPathOrDefault(string? configPath) => !string.IsNullOrWhiteSpace(configPath)
        ? configPath
        : System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lightning-enable", "config.json");

    /// <summary>Writes the token owner-only. Never throws; reports where it went or why it did not.</summary>
    internal static TokenStorage StoreToken(string? configPath, string proxyId, string token, string source)
    {
        var path = TokenFilePath(ConfigPathOrDefault(configPath), proxyId);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
            var contents = new JsonObject
            {
                ["proxyId"] = proxyId,
                ["token"] = token,
                ["source"] = source,
                ["storedAt"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            }.ToJsonString(Indented);
            // Create the file empty, restrict it to the owner, THEN write the secret.
            File.WriteAllText(path, string.Empty);
            try { BudgetConfigurationService.RestrictFilePermissions(path); } catch { /* best effort */ }
            File.WriteAllText(path, contents);
            return new TokenStorage(true, path, null);
        }
        catch (Exception ex)
        {
            return new TokenStorage(false, path, ex.Message);
        }
    }

    /// <summary>The stored token for <paramref name="proxyId"/>, or null.</summary>
    internal static StoredToken? LoadToken(string? configPath, string proxyId)
    {
        var path = TokenFilePath(ConfigPathOrDefault(configPath), proxyId);
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject data)
            {
                return null;
            }
            var token = Text(data, "token");
            return string.IsNullOrEmpty(token) ? null : new StoredToken(token, path, Text(data, "source"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The sentence that says where the token went, sell_url-key style.</summary>
    internal static string StorageNote(TokenStorage storage) => storage.Written
        ? $"It is stored in {storage.Path} (readable only by you), where l402_producer action=verify finds it."
        : $"It could not be saved ({storage.Error}). Save it now.";

    internal static Dictionary<string, object?> StorageJson(TokenStorage storage) => new()
    {
        ["written"] = storage.Written,
        ["path"] = storage.Path,
        ["error"] = storage.Error,
    };

    // ── Shared ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Normalizes the advisory method filter: <c>well-known</c>, <c>dns-txt</c> or <c>header</c>,
    /// also accepting underscores and <c>dns</c>/<c>txt</c>. Null for none; empty string for invalid.
    /// </summary>
    internal static string? NormalizeMethod(string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return null;
        }

        var value = method.Trim().ToLowerInvariant().Replace('_', '-');
        if (value is "dns" or "txt" or "dns-txt-record")
        {
            value = "dns-txt";
        }
        return LiveMethods.Contains(value) ? value : string.Empty;
    }

    /// <summary>
    /// The plain-language next steps for a failed check's reason. Each method gets the first hint
    /// that matches it; a <paramref name="method"/> filter drops the other methods' hints.
    /// </summary>
    internal static List<string> HintsFor(string? reason, string? method)
    {
        var hints = new List<string>();
        if (string.IsNullOrWhiteSpace(reason))
        {
            return hints;
        }

        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in ReasonHints)
        {
            if (!reason.Contains(entry.Contains, StringComparison.Ordinal))
            {
                continue;
            }
            if (entry.Method is not null)
            {
                if ((method is not null && entry.Method != method) || !covered.Add(entry.Method))
                {
                    continue;
                }
            }
            if (!hints.Contains(entry.Hint))
            {
                hints.Add(entry.Hint);
            }
        }

        hints.Add(ReasonHintFallback);
        return hints;
    }

    private static List<string> NextSteps(JsonObject state, string slug, string? filter, int attempts)
    {
        var steps = new List<string>();
        var method = Text(state, "method");

        if (Bool(state, "pending") == true)
        {
            var retry = Number(state, "retryAfterSeconds") ?? MaxRetryAfterSeconds;
            steps.Add($"The check ran past the server's time limit {attempts} times and recorded nothing, so it does "
                + $"not count as a failure. Call verify again in {retry} seconds.");
            steps.AddRange(HintsFor(Text(state, "reason"), filter).Where(h => h != ReasonHintFallback));
            steps.Add("If the target is slow, a dns-txt record does not depend on your server's response time.");
            return steps;
        }

        if (Bool(state, "verified") == true)
        {
            var info = Methods.FirstOrDefault(m => m.Method == method);
            if (method == "grandfathered")
            {
                steps.Add("This proxy was listed before ownership verification existed, so it stays verified "
                    + "(grandfathered) and the daily re-check skips it. To replace that with a real method, call "
                    + "verification_token, publish the token, and call verify.");
            }
            else if (method == "platform")
            {
                steps.Add("Lightning Enable vouches for this proxy (platform); there is nothing to publish.");
            }
            else
            {
                steps.Add(info is null
                    ? $"Verified by {method}."
                    : $"Verified by {method}, which proves {info.Proves}.");
                steps.Add("Leave the token in place: verified proxies are re-checked daily, and three failed re-checks "
                    + "in a row make the proxy unverified and unlisted.");
            }

            steps.Add(Bool(state, "listed") == true
                ? "It is listed publicly."
                : $"Listing is not on yet. To list it, call l402_producer action=publish proxy_id={slug}.");
            return steps;
        }

        if (Bool(state, "tokenIssued") == false)
        {
            steps.Add($"No token exists yet. Call l402_producer action=verification_token proxy_id={slug}.");
            return steps;
        }

        steps.AddRange(HintsFor(Text(state, "reason"), filter));
        if (steps.Count == 0)
        {
            steps.Add(ReasonHintFallback);
        }
        return steps;
    }

    /// <summary>The allowlisted state plus the method instructions, filtered by the advisory method.</summary>
    private static Dictionary<string, object?> StatePayload(JsonObject state, string slug, string? filter)
    {
        var payload = new Dictionary<string, object?> { ["success"] = true };
        foreach (var field in StateFields)
        {
            if (state.ContainsKey(field))
            {
                payload[field] = state[field]?.DeepClone();
            }
        }
        payload.TryAdd("proxyId", slug);
        payload["about"] = About;

        var instructions = state["instructions"] as JsonObject;
        var all = MethodsFrom(instructions);

        var shown = filter is null ? all : all.Where(m => (string?)m["method"] == filter).ToList();
        if (filter is not null && shown.Count == 0)
        {
            payload["methodNote"] = $"{filter} is not available for this proxy's target; every available method is shown.";
            shown = all;
        }
        payload["methods"] = shown;
        if (Text(instructions, "tokenPlaceholder") is { } placeholder)
        {
            payload["tokenPlaceholder"] = placeholder;
        }
        return payload;
    }

    /// <summary>
    /// The server's per-method instructions, each with what the method proves. Falls back to the
    /// static table when the server sent none, so the agent still learns what each one proves.
    /// </summary>
    internal static List<Dictionary<string, object?>> MethodsFrom(JsonObject? instructions)
    {
        var all = (instructions?["methods"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Select(Instruction)
            .ToList();
        if (all.Count == 0)
        {
            all = Methods.Select(m => new Dictionary<string, object?>
            {
                ["method"] = m.Method,
                ["proves"] = m.Proves,
                ["chooseWhen"] = m.ChooseWhen,
            }).ToList();
        }
        return all;
    }

    private static Dictionary<string, object?> Instruction(JsonObject source)
    {
        var name = Text(source, "method");
        var info = Methods.FirstOrDefault(m => m.Method == name);
        var instruction = new Dictionary<string, object?> { ["method"] = name };
        if (info is not null)
        {
            instruction["proves"] = info.Proves;
            instruction["chooseWhen"] = info.ChooseWhen;
        }
        foreach (var field in InstructionFields)
        {
            if (Text(source, field) is { } value)
            {
                instruction[field] = value;
            }
        }
        return instruction;
    }

    private static string? Gate(
        ILightningEnableApiService? apiService, string? proxyId, string? method, out string slug, out string? filter)
    {
        slug = string.Empty;
        filter = null;
        if (apiService is null)
        {
            return Fail(UpstreamError, "Lightning Enable API service not available");
        }
        if (!apiService.IsConfigured)
        {
            return ProducerSetupTools.Fail(LightningEnableApiService.ProducerApiKeyRequired);
        }
        if (string.IsNullOrWhiteSpace(proxyId))
        {
            return Fail(ProxyIdRequired,
                "proxy_id is required: the id create_proxy or sell_url returned for the proxy whose API you own.");
        }

        filter = NormalizeMethod(method);
        if (filter == string.Empty)
        {
            return Fail(InvalidMethod,
                $"method must be one of: {string.Join(", ", LiveMethods)}. It only filters the instructions shown; "
                + "omit it to see all three.");
        }

        slug = proxyId.Trim();
        return null;
    }

    /// <summary>Maps a failed verification call to an error code an agent can branch on.</summary>
    private static string Error(ApiCallResult result, string slug, ILightningEnableApiService apiService)
    {
        string code;
        string message;
        List<string>? steps = null;
        var apiMessage = result.ErrorMessage;

        switch (result.HttpStatus)
        {
            case null:
                code = UpstreamError;
                message = $"The Lightning Enable API could not be reached: {apiMessage}";
                break;
            case 401 or 403:
                code = Unauthorized;
                message = "The Lightning Enable API refused the merchant API key. Check LIGHTNING_ENABLE_API_KEY.";
                break;
            case 404 when (apiMessage ?? string.Empty).Contains("proxy not found", StringComparison.OrdinalIgnoreCase):
                code = ProxyNotFound;
                message = $"This account has no proxy {slug}. Use the proxy id create_proxy or sell_url returned.";
                break;
            case 404:
                code = FeatureNotEnabled;
                message = $"The Lightning Enable API at {apiService.BaseUrl} answered 404 without naming the proxy, so "
                    + "it has no ownership-verification routes yet. Try again after it is updated, or check that "
                    + "LIGHTNING_ENABLE_API_URL points at the right server.";
                break;
            case 409 when result.ErrorCode == TokenRequired:
                code = TokenRequired;
                message = $"Proxy {slug} has no verification token yet.";
                steps = new List<string>
                {
                    $"Call l402_producer action=verification_token proxy_id={slug}, publish the token by one method, "
                    + "then call verify again.",
                };
                break;
            case 429:
                code = RateLimited;
                message = "The verification rate limit for this API key was reached."
                    + (result.RetryAfterSeconds is { } wait ? $" Wait {wait} seconds and try again." : " Wait a minute and try again.");
                break;
            case >= 500:
                code = UpstreamError;
                message = $"The Lightning Enable API failed ({result.HttpStatus}). Try again shortly.";
                break;
            default:
                code = RequestRejected;
                message = $"The Lightning Enable API rejected the request ({result.HttpStatus}).";
                break;
        }

        var payload = new Dictionary<string, object?>
        {
            ["success"] = false,
            ["errorCode"] = code,
            ["error"] = message,
            ["retryable"] = ErrorCodes[code],
        };
        if (!string.IsNullOrWhiteSpace(apiMessage) && apiMessage != message) payload["apiError"] = apiMessage;
        if (result.ErrorType is not null) payload["errorType"] = result.ErrorType;
        if (result.HttpStatus is not null) payload["httpStatus"] = result.HttpStatus;
        if (result.RetryAfterSeconds is not null) payload["retryAfterSeconds"] = result.RetryAfterSeconds;
        if (steps is not null) payload["nextSteps"] = steps;
        return Json(payload);
    }

    private static string Fail(string code, string error) => Json(new Dictionary<string, object?>
    {
        ["success"] = false,
        ["errorCode"] = code,
        ["error"] = error,
        ["retryable"] = ErrorCodes[code],
    });

    private static string? Text(JsonObject? node, string key)
        => node?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool? Bool(JsonObject? node, string key)
        => node?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static int? Number(JsonObject? node, string key)
        => node?[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static string Json(object payload) => JsonSerializer.Serialize(payload, Indented);
}

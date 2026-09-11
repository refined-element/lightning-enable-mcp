using System.Text.Json;
using System.Text.Json.Nodes;
using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Which wallet this server would use, and where that choice came from. Carries the
/// PROVIDER and the SOURCE — never the credential itself.
/// </summary>
/// <param name="Configured">Whether any wallet credential was found at all.</param>
/// <param name="Provider">The wallet that wins the priority order (LND / NWC / Strike / OpenNode).</param>
/// <param name="Source">Where its credential came from: an env var name, or the config file.</param>
/// <param name="FromEnvironment">
/// True when the winning credential came from the environment. This is the flag that makes
/// <c>setup_wallet</c> refuse to write: env beats config, so writing the config file would
/// silently do nothing.
/// </param>
/// <param name="ConfiguredProviders">Every provider with a credential, in priority order.</param>
/// <param name="ConfigFilePath">Where the config file lives.</param>
public sealed record WalletSetupState(
    bool Configured,
    string? Provider,
    string? Source,
    bool FromEnvironment,
    IReadOnlyList<string> ConfiguredProviders,
    string ConfigFilePath);

/// <summary>
/// What a live NIP-47 probe learned about a wallet. Deliberately holds nothing that could
/// identify the credential.
/// </summary>
/// <param name="Success">Whether the wallet answered.</param>
/// <param name="Error">Why it did not, phrased for the operator. Never quotes the connection string.</param>
/// <param name="Methods">The NIP-47 methods the wallet declares (from <c>get_info</c>).</param>
/// <param name="BalanceSats">Spendable balance, when the wallet answered <c>get_balance</c>.</param>
/// <param name="Alias">The wallet's own name for itself, when it publishes one.</param>
public sealed record NwcProbeResult(
    bool Success,
    string? Error,
    IReadOnlyList<string> Methods,
    long? BalanceSats,
    string? Alias)
{
    public static NwcProbeResult Failed(string error) =>
        new(false, error, Array.Empty<string>(), null, null);
}

/// <summary>
/// The wallet-onboarding seam: report what is configured, prove a pasted NWC connection
/// string actually works, and persist it.
///
/// <para>Separated from the tool so <c>setup_wallet</c> can be tested without a relay and
/// without touching the operator's real <c>~/.lightning-enable/config.json</c>.</para>
/// </summary>
public interface IWalletOnboardingService
{
    /// <summary>Which wallet is configured, and from where. Never returns a credential.</summary>
    WalletSetupState Describe();

    /// <summary>
    /// The NWC connection string THIS server pays with, or <c>null</c> when the wallet that
    /// wins the priority order is not an NWC wallet.
    /// </summary>
    /// <remarks>
    /// The one place a credential leaves this service, and it exists for exactly one caller:
    /// <c>l402_producer action=configure_receive</c>, which can hand the operator's own
    /// wallet to Lightning Enable as the RECEIVING wallet instead of making them paste the
    /// string again. LND, Strike and OpenNode credentials are not connection strings and
    /// cannot serve that purpose, so those return <c>null</c> and the caller refuses with a
    /// message naming the wallet. The value is never logged and never reaches a tool result.
    /// </remarks>
    string? ResolveOwnNwcConnectionString();

    /// <summary>
    /// Connects to the wallet the connection string names and asks it what it can do.
    /// Bounded by <see cref="WalletOnboardingService.ProbeTimeout"/> so a black-holed relay
    /// cannot hang the setup call.
    /// </summary>
    Task<NwcProbeResult> ProbeNwcAsync(string connectionString, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the connection string to <c>wallets.nwcConnectionString</c> in the config
    /// file, leaving every other key (limits, tiers, session) exactly as it was, and
    /// restricts the file's permissions.
    /// </summary>
    void SaveNwcConnectionString(string connectionString);
}

/// <inheritdoc />
public sealed class WalletOnboardingService : IWalletOnboardingService
{
    /// <summary>
    /// Total budget for a live wallet probe. Long enough for a relay handshake plus a
    /// NIP-47 round trip, short enough that a wallet which never answers fails the setup
    /// call with a clear message instead of stalling the agent.
    /// </summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly IBudgetConfigurationService? _configService;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly string _configFilePath;

    public WalletOnboardingService(
        IBudgetConfigurationService? configService = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        _configService = configService;
        _httpClientFactory = httpClientFactory;
        _configFilePath = configService?.ConfigFilePath ?? DefaultConfigFilePath();
    }

    // Test-only: an explicit config path, so a test never writes to the real home dir.
    internal WalletOnboardingService(string configFilePath)
    {
        _configFilePath = configFilePath;
    }

    private static string DefaultConfigFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".lightning-enable",
        "config.json");

    /// <inheritdoc />
    public WalletSetupState Describe()
    {
        var wallets = _configService?.Configuration?.Wallets;

        // (provider, env var that carries it, whether the env var is actually set,
        //  whether the config file carries it)
        var lndFromEnv = HasEnv("LND_REST_HOST") && HasEnv("LND_MACAROON_HEX");
        var lndFromConfig = !string.IsNullOrEmpty(wallets?.LndRestHost)
            && !string.IsNullOrEmpty(wallets?.LndMacaroonHex);

        var candidates = new List<(string Provider, string EnvVar, bool Env, bool Config)>
        {
            ("LND", "LND_REST_HOST + LND_MACAROON_HEX", lndFromEnv, lndFromConfig),
            ("NWC", "NWC_CONNECTION_STRING", HasEnv("NWC_CONNECTION_STRING"),
                !string.IsNullOrEmpty(wallets?.NwcConnectionString)),
            ("Strike", "STRIKE_API_KEY", HasEnv("STRIKE_API_KEY"),
                !string.IsNullOrEmpty(wallets?.StrikeApiKey)),
            ("OpenNode", "OPENNODE_API_KEY", HasEnv("OPENNODE_API_KEY"),
                !string.IsNullOrEmpty(wallets?.OpenNodeApiKey)),
        };

        // Mirrors Program's selection: an explicit WALLET_PRIORITY wins if that wallet is
        // actually configured, otherwise the L402-first default order LND > NWC > Strike >
        // OpenNode (the order `candidates` is already in).
        var priority = (Environment.GetEnvironmentVariable("WALLET_PRIORITY")
            ?? _configService?.Configuration?.Wallets?.Priority)?.Trim().ToLowerInvariant();

        var ordered = candidates.ToList();
        if (!string.IsNullOrEmpty(priority))
        {
            var preferred = ordered.FirstOrDefault(
                c => c.Provider.Equals(priority, StringComparison.OrdinalIgnoreCase));
            if (preferred.Provider is not null && (preferred.Env || preferred.Config))
            {
                ordered.Remove(preferred);
                ordered.Insert(0, preferred);
            }
        }

        var configured = ordered.Where(c => c.Env || c.Config).ToList();
        var winner = configured.FirstOrDefault();

        if (winner.Provider is null)
        {
            return new WalletSetupState(
                false, null, null, false, Array.Empty<string>(), _configFilePath);
        }

        return new WalletSetupState(
            Configured: true,
            Provider: winner.Provider,
            Source: winner.Env ? $"environment ({winner.EnvVar})" : $"config file ({_configFilePath})",
            FromEnvironment: winner.Env,
            ConfiguredProviders: configured.Select(c => c.Provider).ToList(),
            ConfigFilePath: _configFilePath);
    }

    /// <inheritdoc />
    public string? ResolveOwnNwcConnectionString()
    {
        // Only when NWC actually WINS the priority order. A server holding both an LND node
        // and a leftover NWC string is an LND server, and handing over the NWC wallet would
        // point the merchant's payouts somewhere they are not spending from.
        if (!string.Equals(Describe().Provider, "NWC", StringComparison.Ordinal))
        {
            return null;
        }

        // Env beats config, exactly as Program's own wallet selection does.
        var fromEnvironment = Environment.GetEnvironmentVariable("NWC_CONNECTION_STRING");
        if (!string.IsNullOrEmpty(fromEnvironment)
            && !fromEnvironment.StartsWith("${", StringComparison.Ordinal))
        {
            return fromEnvironment;
        }

        return _configService?.Configuration?.Wallets?.NwcConnectionString;
    }

    /// <summary>An env var counts only when set and actually expanded (not a literal "${...}").</summary>
    private static bool HasEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrEmpty(value) && !value.StartsWith("${", StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public async Task<NwcProbeResult> ProbeNwcAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        var http = _httpClientFactory?.CreateClient(nameof(WalletOnboardingService)) ?? new HttpClient();
        using var wallet = new NwcWalletService(http, connectionString);

        if (!wallet.IsConfigured)
        {
            return NwcProbeResult.Failed(
                "The connection string parsed but no signing key could be derived from its "
                + "'secret' parameter.");
        }

        var methods = new List<string>();
        string? alias = null;
        string? infoError = null;

        try
        {
            var info = await wallet.GetInfoAsync(timeout.Token);
            if (info["methods"] is JsonArray declared)
            {
                methods.AddRange(declared
                    .Select(m => m?.GetValue<string>())
                    .Where(m => !string.IsNullOrEmpty(m))!);
            }
            alias = info["alias"]?.GetValue<string>();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            return NwcProbeResult.Failed(
                $"The wallet did not answer within {ProbeTimeout.TotalSeconds:F0} seconds. Check the "
                + "relay URL in the connection string, and that the connection is still active in "
                + "your wallet app.");
        }
        catch (Exception ex)
        {
            // Some wallets do not implement get_info. Fall through to get_balance, which is
            // the more widely implemented method, and only report this if that fails too.
            infoError = ex.Message;
        }

        long? balanceSats = null;
        string? balanceError = null;
        try
        {
            var balance = await wallet.GetBalanceAsync(timeout.Token);
            balanceSats = balance.BalanceMsat / 1000;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            balanceError = $"the wallet did not answer get_balance within {ProbeTimeout.TotalSeconds:F0} seconds";
        }
        catch (Exception ex)
        {
            balanceError = ex.Message;
        }

        if (infoError != null && balanceError != null)
        {
            return NwcProbeResult.Failed(
                $"The wallet could not be reached: {infoError} (get_balance also failed: {balanceError})");
        }

        return new NwcProbeResult(true, balanceError, methods, balanceSats, alias);
    }

    /// <inheritdoc />
    public void SaveNwcConnectionString(string connectionString)
    {
        var directory = Path.GetDirectoryName(_configFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Merge into the EXISTING document rather than serializing a fresh
        // UserBudgetConfiguration: the file is the operator's, and it may carry limits,
        // tiers, or keys this build does not know about. Only wallets.nwcConnectionString
        // is touched.
        JsonObject root;
        try
        {
            root = File.Exists(_configFilePath)
                ? JsonNode.Parse(File.ReadAllText(_configFilePath)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch (JsonException)
        {
            // A hand-edited file that no longer parses must not silently lose the
            // operator's limits: refuse rather than overwrite.
            throw new InvalidOperationException(
                $"{_configFilePath} is not valid JSON, so it cannot be updated safely. "
                + "Fix or remove the file and run setup_wallet again.");
        }

        if (root["wallets"] is not JsonObject wallets)
        {
            wallets = new JsonObject();
            root["wallets"] = wallets;
        }
        wallets["nwcConnectionString"] = connectionString;

        File.WriteAllText(_configFilePath, root.ToJsonString(WriteOptions));

        // Same hardening as first-run config creation (F-12): the file now holds a live
        // wallet credential in plaintext.
        BudgetConfigurationService.RestrictFilePermissions(_configFilePath);

        _configService?.Reload();
    }
}

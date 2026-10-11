using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// Server-side analytics attribute MCP traffic by <c>User-Agent</c>, so it must carry the real
/// package version (<c>LightningEnable-MCP/&lt;version&gt;</c>), never a hand-maintained literal.
/// </summary>
public class UserAgentTests
{
    private static readonly Regex UserAgentShape = new(@"^LightningEnable-MCP/\d+\.\d+\.\d+$");

    private static string PackageVersion() =>
        typeof(UserAgent).Assembly.GetName().Version!.ToString(3);

    [Fact]
    public void Value_IsProductTokenSlashPackageVersion()
    {
        UserAgent.Value.Should().MatchRegex(UserAgentShape.ToString());
        UserAgent.Value.Should().Be($"LightningEnable-MCP/{PackageVersion()}");
    }

    [Fact]
    public void Version_StripsTheSourceRevisionSuffix()
    {
        var informational = typeof(UserAgent).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        UserAgent.Version.Should().NotContain("+");
        informational.Should().StartWith(UserAgent.Version);
    }

    [Fact]
    public void FallbackVersion_MatchesTheCsprojPackageVersion()
    {
        // The fallback is only reached when the assembly has no version, but it must still be
        // bumped with <Version>; this fails the release bump that forgets it.
        var csproj = Path.Combine(SourceRoot(), "LightningEnable.Mcp.csproj");
        var match = Regex.Match(File.ReadAllText(csproj), "<Version>([^<]+)</Version>");

        match.Success.Should().BeTrue();
        UserAgent.FallbackVersion.Should().Be(match.Groups[1].Value);
        PackageVersion().Should().Be(match.Groups[1].Value);
    }

    [Fact]
    public void LightningEnableApiService_SendsVersionedUserAgent()
    {
        var httpClient = new HttpClient();
        _ = new LightningEnableApiService(httpClient, ConfigService());

        httpClient.DefaultRequestHeaders.UserAgent.ToString().Should().Be(UserAgent.Value);
    }

    [Fact]
    public void AgentService_SendsVersionedUserAgent()
    {
        var httpClient = new HttpClient();
        _ = new AgentService(httpClient, ConfigService());

        httpClient.DefaultRequestHeaders.UserAgent.ToString().Should().Be(UserAgent.Value);
    }

    [Fact]
    public void DiscoverApiManifestClient_SendsVersionedUserAgent()
    {
        using var client = DiscoverApiTool.CreateGuardedManifestClient();

        client.DefaultRequestHeaders.UserAgent.ToString().Should().Be(UserAgent.Value);
    }

    [Fact]
    public void NoSourceFileHardCodesAUserAgentVersion()
    {
        // Anywhere in the file, comments included: a versioned token in a doc comment goes
        // stale on the next release just like a string literal does.
        var offenders = SourceFiles()
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"LightningEnable-MCP(-Python)?/[0-9]"))
            .Select(Path.GetFileName)
            .ToList();

        offenders.Should().BeEmpty("every User-Agent should come from UserAgent.Value");
    }

    // ----- Every HttpClient the server builds sends UserAgent.Value, or is allowlisted -----

    /// <summary>
    /// A line that creates or registers an HttpClient: <c>new HttpClient</c>, a target-typed
    /// <c>HttpClient x = new(</c> field, an <c>HttpClient X() =&gt;</c> factory, or
    /// <c>AddHttpClient</c>.
    /// </summary>
    private static readonly Regex HttpClientSite = new(
        @"new\s+HttpClient\b|HttpClient\s+\w+\s*=\s*new\s*\(|HttpClient\s+\w+\s*\(\s*\)\s*=>|\bAddHttpClient\b");

    /// <summary>The concrete client type of <c>AddHttpClient&lt;TClient&gt;</c> / <c>&lt;TI, TClient&gt;</c>.</summary>
    private static readonly Regex TypedRegistration = new(@"AddHttpClient<(?:\s*\w+\s*,)?\s*(\w+)\s*>");

    /// <summary>
    /// Sites that deliberately do not set <see cref="UserAgent.Value"/> themselves. Each entry is
    /// (file, a substring of the site's line, why). A stale entry fails the test too.
    /// </summary>
    private static readonly (string File, string Marker, string Reason)[] UserAgentAllowlist =
    {
        ("Program.cs", "AddHttpClient<TWallet>",
            "wallet providers (Strike, OpenNode, LND): third-party wallet APIs, not Lightning Enable"),
        ("Program.cs", "AddHttpClient<IPriceService, PriceService>",
            "public BTC price feeds, not Lightning Enable"),
        ("Program.cs", "AddHttpClient(ConfirmationChannelFactory.WebhookHttpClientName",
            "User-Agent is set per request in WebhookConfirmationChannel.DeliverAsync (ConfirmationChannels.cs)"),
        ("WalletOnboardingService.cs", "new HttpClient()",
            "NWC probe: NwcWalletService talks to the relay over WebSocket and sends no HTTP request"),
    };

    /// <summary>How many lines after a site its initializer may span.</summary>
    private const int SiteWindowLines = 12;

    [Fact]
    public void EveryHttpClientSite_SendsTheVersionedUserAgent_OrIsAllowlisted()
    {
        var root = SourceRoot();
        var offenders = new List<string>();
        var usedAllowlistEntries = new HashSet<int>();

        foreach (var file in SourceFiles())
        {
            var lines = File.ReadAllLines(file);
            var fileName = Path.GetFileName(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal) || !HttpClientSite.IsMatch(line))
                {
                    continue;
                }

                var allowIndex = Array.FindIndex(UserAgentAllowlist,
                    a => a.File == fileName && line.Contains(a.Marker, StringComparison.Ordinal));
                if (allowIndex >= 0)
                {
                    usedAllowlistEntries.Add(allowIndex);
                    continue;
                }

                var typed = TypedRegistration.Match(line);
                bool setsUserAgent;
                if (typed.Success)
                {
                    // A typed client sets the header in its own constructor.
                    var clientFile = Directory.EnumerateFiles(root, typed.Groups[1].Value + ".cs", SearchOption.AllDirectories)
                        .FirstOrDefault();
                    setsUserAgent = clientFile != null && File.ReadAllText(clientFile).Contains("UserAgent.Value");
                }
                else
                {
                    var window = string.Join("\n", lines.Skip(i).Take(SiteWindowLines + 1));
                    setsUserAgent = window.Contains("UserAgent.Value");
                }

                if (!setsUserAgent)
                {
                    offenders.Add($"{fileName}:{i + 1}: {line.Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "every HttpClient must send UserAgent.Value, or be allowlisted with a reason in UserAgentAllowlist");

        var stale = UserAgentAllowlist.Where((_, index) => !usedAllowlistEntries.Contains(index))
            .Select(a => $"{a.File}: {a.Marker}");
        stale.Should().BeEmpty("an allowlist entry that matches no site should be removed");

        // The webhook entry delegates to a per-request header; pin that it is still there.
        File.ReadAllText(Path.Combine(root, "Services", "ConfirmationChannels.cs"))
            .Should().Contain("\"User-Agent\", UserAgent.Value");
    }

    [Fact]
    public void StartupVersionCheck_SendsTheVersionedUserAgent()
    {
        var program = File.ReadAllText(Path.Combine(SourceRoot(), "Program.cs"));
        var versionCheck = program.IndexOf("/api/mcp/version-check", StringComparison.Ordinal);
        versionCheck.Should().BePositive();

        var clientCreated = program.LastIndexOf("new HttpClient", versionCheck, StringComparison.Ordinal);
        clientCreated.Should().BePositive();
        program[clientCreated..versionCheck].Should().Contain("UserAgent.Value");
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static IBudgetConfigurationService ConfigService()
    {
        var mock = new Mock<IBudgetConfigurationService>();
        mock.Setup(c => c.Configuration).Returns(new UserBudgetConfiguration());
        return mock.Object;
    }

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "LightningEnable.Mcp")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "LightningEnable.Mcp");
    }
}

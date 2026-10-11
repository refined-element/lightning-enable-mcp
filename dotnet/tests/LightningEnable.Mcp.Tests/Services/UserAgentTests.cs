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
/// package version (<c>LightningEnable-MCP/2.1.0</c>), never a hand-maintained literal.
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
        var offenders = Directory
            .EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), "\"LightningEnable-MCP/[0-9]"))
            .Select(Path.GetFileName)
            .ToList();

        offenders.Should().BeEmpty("every User-Agent should come from UserAgent.Value");
    }

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

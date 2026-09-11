using System.Text.RegularExpressions;
using LightningEnable.Mcp.Tools;

namespace LightningEnable.Mcp.Tests;

/// <summary>
/// Drift guard: no agent-facing string may steer an agent to a DIFFERENT, deprecated tool.
///
/// <para>The 2026-09 consolidation folded 16 single-purpose tools into five action verbs.
/// The old names still dispatch, so a hint that names one is not <em>broken</em> — it just
/// steers the agent onto the deprecated path, which comes back carrying a deprecation
/// marker and costs a round trip. Those hints outlived the rename once already; this stops
/// them coming back.</para>
///
/// <para>Scope, deliberately narrow — it fires only on a string a MODEL reads:</para>
/// <list type="bullet">
/// <item><description>Comments and XML doc are excluded: they are developer-facing, and a
/// file that documents its own history is not instructing anyone.</description></item>
/// <item><description>A file's own tool name is excluded. The confirmation flow tells the
/// agent to call the SAME tool again with a nonce; that is a re-call, not a cross-tool
/// hint, and it is correct whatever name the agent used to get there.</description></item>
/// <item><description>A bare mention is fine. The <c>Name = "..."</c> attribute, the alias
/// table and the profile lists all name deprecated tools legitimately — only a CALL shape
/// (<c>old_name(</c>) or an imperative (<c>use old_name</c>) counts.</description></item>
/// </list>
///
/// <para>Mirrors <c>python/lightning-enable-mcp/tests/test_agent_facing_hints.py</c>.</para>
/// </summary>
public class AgentFacingHintTests
{
    /// <summary>Files whose legacy-name mentions are structural, not guidance.</summary>
    private static readonly IReadOnlySet<string> ExemptFileNames = new HashSet<string>
    {
        "ToolProfiles.cs",         // the list of legacy names IS the point
        "ToolSurface.cs",          // routing, by name
        "DeprecatedAliasTools.cs", // the alias table maps old name -> new call
        "McpToolSurfaceExtensions.cs",
    };

    /// <summary>Verbs that turn a mention into an instruction.</summary>
    private static readonly string[] Imperatives = ["use", "call", "check", "run", "via", "see"];

    /// <summary>
    /// The file that DECLARES <paramref name="legacyName"/> as a tool — the one place a
    /// mention is a re-call of the same tool rather than a cross-tool hint. Derived from the
    /// source (<c>Name = "..."</c>) rather than hard-coded, so it cannot drift when a tool
    /// moves or a class is renamed; several legacy names share one file.
    /// </summary>
    private static string OwningFileFor(string legacyName) =>
        SourceFiles().FirstOrDefault(path =>
            File.ReadAllText(path).Contains($"Name = \"{legacyName}\"", StringComparison.Ordinal))
        is { } found
            ? Path.GetFileName(found)
            : string.Empty;

    /// <summary>A C# string literal, verbatim or regular, on a non-comment line.</summary>
    private static readonly Regex StringLiteral = new("\"((?:[^\"\\\\\\n]|\\\\.)*)\"", RegexOptions.Compiled);

    private static string SourceRoot()
    {
        // Walk up from the test binary to the repo, then into the shipped source.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "LightningEnable.Mcp")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "LightningEnable.Mcp");
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !ExemptFileNames.Contains(Path.GetFileName(f)));

    /// <summary>String literals on lines that are not comments, with line numbers.</summary>
    private static IEnumerable<(int Line, string Text)> ModelFacingLiterals(string path)
    {
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (System.Text.RegularExpressions.Match match in StringLiteral.Matches(lines[i]))
            {
                yield return (i + 1, match.Groups[1].Value);
            }
        }
    }

    private static bool IsHint(string literal, string legacyName)
    {
        if (Regex.IsMatch(literal, $@"\b{Regex.Escape(legacyName)}\s*\("))
        {
            return true;
        }
        return Imperatives.Any(verb => Regex.IsMatch(
            literal, $@"\b{verb}\s+{Regex.Escape(legacyName)}\b", RegexOptions.IgnoreCase));
    }

    public static TheoryData<string> LegacyNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in ToolProfiles.LegacyToolNames)
        {
            data.Add(name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(LegacyNames))]
    public void NoStringLiteralSteersAnAgentToADeprecatedTool(string legacyName)
    {
        var owner = OwningFileFor(legacyName);
        owner.Should().NotBeEmpty(
            $"'{legacyName}' must be declared by some tool in the assembly — if it is not, this "
            + "guard is scanning the wrong tree");

        var offenders = new List<string>();
        foreach (var path in SourceFiles())
        {
            if (string.Equals(Path.GetFileName(path), owner, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (line, literal) in ModelFacingLiterals(path))
            {
                if (IsHint(literal, legacyName))
                {
                    var preview = literal.Length > 110 ? literal[..110] : literal;
                    offenders.Add($"{Path.GetFileName(path)}:{line}: {preview}");
                }
            }
        }

        offenders.Should().BeEmpty(
            $"'{legacyName}' is deprecated, so no string may tell an agent to call it — point "
            + "it at the consolidated verb instead (see the 'Old name -> new call' table in the "
            + "root README). Offending strings:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheGuardWouldActuallyCatchARegression()
    {
        // A guard that cannot fail is not a guard.
        IsHint("Use settle_agent_service(l402Endpoint=\\\"x\\\") to pay.", "settle_agent_service")
            .Should().BeTrue();
        IsHint("settle via settle_agent_service.", "settle_agent_service").Should().BeTrue();

        // A bare mention is fine — that is how the alias table and the schemas name the tool.
        IsHint("settle_agent_service", "settle_agent_service").Should().BeFalse();
    }

    [Fact]
    public void TheGuardActuallyReadsSomeFiles()
    {
        // Guard the guard: an exemption typo must not silently empty the scan.
        var files = SourceFiles().ToList();
        files.Should().HaveCountGreaterThan(20);
        files.Should().Contain(f => ModelFacingLiterals(f).Any());
    }
}

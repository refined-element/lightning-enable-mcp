using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace LightningEnable.Mcp.Tests.Tools;

/// <summary>
/// The <c>docs</c> tool: search and read the Lightning Enable documentation.
///
/// Parser, ranking, snippets and truncation are pinned by the shared golden file
/// <c>shared/docs/docs-tool-cases.json</c> at the repo root, which the Python suite
/// (<c>tests/test_docs.py</c>) asserts on too, so the two ports cannot drift.
/// </summary>
public class DocsToolTests
{
    private static readonly string Divider = new('=', 78);

    private static readonly string Mini = string.Join("\n",
        "preamble", Divider, "# Page One", "Source: https://docs.example.test/one", Divider, "",
        "# Page One", "", "Alpha beta gamma.", "");

    private static string SharedDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "shared", "docs", "docs-tool-cases.json");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }
        }

        throw new FileNotFoundException("shared/docs/docs-tool-cases.json not found above the test output directory.");
    }

    private static JsonObject Cases()
        => JsonNode.Parse(File.ReadAllText(Path.Combine(SharedDir(), "docs-tool-cases.json")))!.AsObject();

    private static IReadOnlyList<DocsPage> SamplePages()
        => DocsCorpus.Parse(File.ReadAllText(Path.Combine(SharedDir(), "llms-full.sample.txt")));

    private static void ShouldEqualJson(JsonNode? actual, JsonNode? expected, string because)
        => JsonNode.DeepEquals(actual, expected).Should().BeTrue(
            $"{because}\nexpected: {expected?.ToJsonString()}\nactual:   {actual?.ToJsonString()}");

    private sealed class Fetcher
    {
        public string Text { get; set; } = Mini;

        public Exception? Error { get; set; }

        public int Calls { get; private set; }

        public Task<string> FetchAsync(string url, CancellationToken ct)
        {
            Calls++;
            return Error is null ? Task.FromResult(Text) : Task.FromException<string>(Error);
        }
    }

    private sealed class Clock
    {
        public double Now { get; set; } = 1000;
    }

    private static (DocsService Service, Fetcher Fetch, Clock Clock) NewService(string url = "https://docs.example.test/llms-full.txt")
    {
        var fetch = new Fetcher();
        var clock = new Clock();
        return (new DocsService(url, fetch.FetchAsync, () => clock.Now), fetch, clock);
    }

    // ── Shared golden cases (identical expectations in the Python suite) ──────

    [Fact]
    public void Parser_FindsEveryPageAndHeading()
    {
        var actual = new JsonArray(SamplePages().Select(p => (JsonNode)new JsonObject
        {
            ["path"] = p.Path,
            ["title"] = p.Title,
            ["url"] = p.Url,
            ["headings"] = new JsonArray(p.Sections.Where(s => s.Heading != null)
                .Select(s => (JsonNode)JsonValue.Create(s.Heading)!).ToArray()),
        }).ToArray());

        ShouldEqualJson(actual, Cases()["pages"], "the parser must agree with the Python port");
    }

    [Fact]
    public void AHeadingInsideACodeFence_IsNotASection()
    {
        var auth = SamplePages().Single(p => p.Path == "/api-reference/authentication");
        auth.Sections.Should().NotContain(s => s.Heading != null && s.Heading.Contains("not a heading"));
    }

    public static TheoryData<int> SearchCaseIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Cases()["search"]!.AsArray().Count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SearchCaseIndexes))]
    public void Search_MatchesTheSharedGoldenResult(int index)
    {
        var c = Cases()["search"]![index]!.AsObject();
        int? limit = c.TryGetPropertyValue("limit", out var l) && l != null ? l.GetValue<int>() : null;

        var result = DocsCorpus.Search(SamplePages(), c["query"]!.GetValue<string>(), limit);

        result["success"]!.GetValue<bool>().Should().BeTrue();
        ShouldEqualJson(result["results"], c["expected"], $"search '{c["query"]}'");
        result["count"]!.GetValue<int>().Should().Be(c["expected"]!.AsArray().Count);
    }

    public static TheoryData<int> ReadCaseIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Cases()["read"]!.AsArray().Count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ReadCaseIndexes))]
    public void Read_MatchesTheSharedGoldenResult(int index)
    {
        var c = Cases()["read"]![index]!.AsObject();
        var section = c.TryGetPropertyValue("section", out var s) ? s?.GetValue<string>() : null;
        var maxBytes = c.TryGetPropertyValue("maxBytes", out var m) && m != null
            ? m.GetValue<int>()
            : DocsCorpus.MaxReadBytes;

        var result = DocsCorpus.Read(SamplePages(), c["path"]!.GetValue<string>(), section, maxBytes);

        var expected = c["expected"]!.DeepClone().AsObject();
        if (expected.TryGetPropertyValue("errorContains", out var contains))
        {
            expected.Remove("errorContains");
            result["success"]!.GetValue<bool>().Should().BeFalse();
            result["error"]!.GetValue<string>().Should().Contain(contains!.GetValue<string>());
            result.Remove("error");
        }

        result.Remove("hint");
        ShouldEqualJson(result, expected, $"read '{c["path"]}' section '{section}'");
    }

    [Fact]
    public async Task AFileUrlSource_IsReadFromDisk()
    {
        var url = new Uri(Path.Combine(SharedDir(), "llms-full.sample.txt")).AbsoluteUri;
        var service = new DocsService(url);

        var result = await service.SearchAsync("NIP-04", null);

        result["success"]!.GetValue<bool>().Should().BeTrue(result.ToJsonString());
        result["results"]![0]!["path"]!.GetValue<string>().Should().Be("/nwc-setup/account-setup");
        result["source"]!.GetValue<string>().Should().StartWith("file:");
    }

    // ── Ranking and truncation rules ──────────────────────────────────────────

    [Fact]
    public void TitleMatches_OutrankBodyMatches()
    {
        var text = string.Join("\n",
            Divider, "# Body page", "Source: https://d.test/body", Divider, "", "relay relay relay relay",
            Divider, "# Relay", "Source: https://d.test/relay", Divider, "", "nothing to see");

        var result = DocsCorpus.Search(DocsCorpus.Parse(text), "relay", null);

        result["results"]!.AsArray().Select(r => r!["path"]!.GetValue<string>())
            .Should().Equal("/relay", "/body");
    }

    [Fact]
    public void MatchingMoreQueryTerms_BeatsAHigherScore()
    {
        var text = string.Join("\n",
            Divider, "# A", "Source: https://d.test/a", Divider, "", "wallet wallet wallet wallet wallet wallet",
            Divider, "# B", "Source: https://d.test/b", Divider, "", "wallet relay");

        DocsCorpus.Search(DocsCorpus.Parse(text), "wallet relay", null)["results"]![0]!["path"]!
            .GetValue<string>().Should().Be("/b");
    }

    [Fact]
    public void Limit_IsClampedTo20()
    {
        var body = string.Join("\n\n", Enumerable.Range(0, 30).Select(n => $"## Topic {n}\n\nkeyword"));
        var text = string.Join("\n", Divider, "# P", "Source: https://d.test/p", Divider, "", body);

        DocsCorpus.Search(DocsCorpus.Parse(text), "keyword", 500)["count"]!.GetValue<int>().Should().Be(20);
    }

    [Fact]
    public void AQueryWithoutLettersOrDigits_IsAnError()
    {
        var result = DocsCorpus.Search(DocsCorpus.Parse(Mini), "?!", null);
        result["success"]!.GetValue<bool>().Should().BeFalse();
        result["error"]!.GetValue<string>().Should().Contain("letters or digits");
    }

    [Fact]
    public void Read_CapsAt12000Bytes_OnALineBoundary()
    {
        var lines = Enumerable.Range(0, 300).Select(n => $"line {n:D5} " + new string('x', 90));
        var text = string.Join("\n", new[] { Divider, "# Big", "Source: https://d.test/big", Divider, "" }.Concat(lines));

        var result = DocsCorpus.Read(DocsCorpus.Parse(text), "/big", null);

        result["truncated"]!.GetValue<bool>().Should().BeTrue();
        var body = result["text"]!.GetValue<string>();
        Encoding.UTF8.GetByteCount(body).Should().BeLessThanOrEqualTo(DocsCorpus.MaxReadBytes);
        body.Should().EndWith("x", "the cut lands on a whole line");
        result.ContainsKey("hint").Should().BeTrue();
    }

    [Fact]
    public void Truncation_NeverSplitsAMultibyteCharacter()
    {
        DocsCorpus.TruncateUtf8("ab€c", 4).Should().Be(("ab", true));
        DocsCorpus.TruncateUtf8("a\U0001F600b", 3).Should().Be(("a", true), "a surrogate pair is one character");
    }

    [Fact]
    public void ACrlfCorpus_ParsesLikeLf()
        => DocsCorpus.Parse(Mini.Replace("\n", "\r\n")).Select(p => p.Path).Should().Equal("/one");

    [Fact]
    public void ADividerInsideAPage_IsNotAPageBoundary()
    {
        var pages = DocsCorpus.Parse(Mini + "\n" + Divider + "\nnot a header\n");
        pages.Should().ContainSingle();
        string.Join("\n", pages[0].Lines).Should().Contain("not a header");
    }

    // ── Cache and failure modes ───────────────────────────────────────────────

    [Fact]
    public async Task ASecondCallWithinAnHour_UsesTheCache()
    {
        var (service, fetch, clock) = NewService();
        await service.SearchAsync("alpha", null);
        clock.Now += DocsService.CacheTtlSeconds - 1;
        await service.ReadAsync("/one", null);
        fetch.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AnExpiredCache_IsRefreshed()
    {
        var (service, fetch, clock) = NewService();
        await service.SearchAsync("alpha", null);
        clock.Now += DocsService.CacheTtlSeconds;
        fetch.Text = Mini.Replace("Alpha", "Delta");

        var result = await service.SearchAsync("delta", null);

        fetch.Calls.Should().Be(2);
        result["count"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task AFailedRefresh_ServesTheStaleCopy_AndBacksOff()
    {
        var (service, fetch, clock) = NewService();
        await service.SearchAsync("alpha", null);
        clock.Now += DocsService.CacheTtlSeconds + 1;
        fetch.Error = new HttpRequestException("connection refused");

        var result = await service.SearchAsync("alpha", null);
        result["success"]!.GetValue<bool>().Should().BeTrue();
        result["stale"]!.GetValue<bool>().Should().BeTrue();
        result["warning"]!.GetValue<string>().Should().Contain("connection refused");
        fetch.Calls.Should().Be(2);

        clock.Now += DocsService.RetryAfterFailureSeconds - 1;
        (await service.ReadAsync("/one", null))["stale"]!.GetValue<bool>().Should().BeTrue();
        fetch.Calls.Should().Be(2, "the stale copy is served without refetching inside the back-off");

        clock.Now += 2;
        fetch.Error = null;
        var fresh = await service.ReadAsync("/one", null);
        fetch.Calls.Should().Be(3);
        fresh.ContainsKey("stale").Should().BeFalse();
    }

    [Fact]
    public async Task AFailureWithNoCache_NamesTheUrl()
    {
        var (service, fetch, _) = NewService();
        fetch.Error = new HttpRequestException("HTTP 503");

        var result = await service.SearchAsync("alpha", null);

        result["success"]!.GetValue<bool>().Should().BeFalse();
        var error = result["error"]!.GetValue<string>();
        error.Should().Contain("https://docs.example.test/llms-full.txt").And.Contain("HTTP 503")
            .And.Contain(DocsService.SourceEnvironmentVariable);
    }

    [Fact]
    public async Task AFileWithNoPages_IsAFailure_NotAnEmptyCorpus()
    {
        var (service, fetch, _) = NewService();
        fetch.Text = "<html>nope</html>";

        var result = await service.SearchAsync("alpha", null);

        result["success"]!.GetValue<bool>().Should().BeFalse();
        result["error"]!.GetValue<string>().Should().Contain("no pages found");
    }

    [Theory]
    [InlineData("http://docs.example.test/llms-full.txt")]
    [InlineData("ftp://docs.example.test/x")]
    [InlineData("not a url")]
    public async Task NonHttpsSources_AreRefused(string url)
    {
        var (service, fetch, _) = NewService(url);

        var result = await service.SearchAsync("alpha", null);

        result["success"]!.GetValue<bool>().Should().BeFalse();
        result["error"]!.GetValue<string>().Should().Contain(DocsService.SourceEnvironmentVariable);
        fetch.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("http://localhost:8080/x")]
    [InlineData("http://127.0.0.1/x")]
    public async Task LoopbackHttp_IsAllowed(string url)
    {
        var (service, _, _) = NewService(url);
        (await service.SearchAsync("alpha", null))["success"]!.GetValue<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TheDefaultSource_IsThePublishedCorpus(string? env)
        => DocsService.ResolveSourceUrl(env).Should().Be("https://docs.lightningenable.com/llms-full.txt")
            .And.Be(DocsService.DefaultSourceUrl);

    [Fact]
    public void TheEnvironmentVariable_OverridesTheSource()
        => DocsService.ResolveSourceUrl(" file:///tmp/llms-full.txt ").Should().Be("file:///tmp/llms-full.txt");

    [Fact]
    public async Task AMissingFileUrl_IsAClearError()
    {
        var url = new Uri(Path.Combine(Path.GetTempPath(), "le-docs-missing-" + Guid.NewGuid().ToString("N") + ".txt")).AbsoluteUri;

        var result = await new DocsService(url).SearchAsync("alpha", null);

        result["success"]!.GetValue<bool>().Should().BeFalse();
        result["error"]!.GetValue<string>().Should().Contain(url);
    }

    // ── Tool surface ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchWithoutAQuery_IsAnError_WithoutFetching()
    {
        var (service, fetch, _) = NewService();

        var result = JsonNode.Parse(await DocsTool.Docs(DocsAction.search, docsService: service))!;

        result["success"]!.GetValue<bool>().Should().BeFalse();
        result["error"]!.GetValue<string>().Should().Contain("query");
        fetch.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ReadWithoutAPath_IsAnError_WithoutFetching()
    {
        var (service, fetch, _) = NewService();

        var result = JsonNode.Parse(await DocsTool.Docs(DocsAction.read, docsService: service))!;

        result["success"]!.GetValue<bool>().Should().BeFalse();
        result["error"]!.GetValue<string>().Should().Contain("path");
        fetch.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AnOutOfRangeAction_ListsTheValidOnes()
    {
        var (service, _, _) = NewService();

        var result = JsonNode.Parse(await DocsTool.Docs((DocsAction)42, docsService: service))!;

        result["success"]!.GetValue<bool>().Should().BeFalse();
        result["error"]!.GetValue<string>().Should().Contain("search").And.Contain("read");
    }

    [Fact]
    public async Task WithNoService_ItSaysSo()
    {
        var result = JsonNode.Parse(await DocsTool.Docs(DocsAction.search, query: "x", docsService: null))!;
        result["success"]!.GetValue<bool>().Should().BeFalse();
        result["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TheToolIsReadOnly_Idempotent_AndOpenWorld()
    {
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var annotations = (await host.AdvertisedToolsAsync()).Single(t => t.Name == "docs").ProtocolTool.Annotations!;

        annotations.ReadOnlyHint.Should().BeTrue();
        annotations.IdempotentHint.Should().BeTrue();
        annotations.OpenWorldHint.Should().BeTrue();
    }

    [Fact]
    public async Task LiteDoesNotAdvertiseDocs()
    {
        await using var host = await McpToolHost.StartAsync(ToolProfile.Lite);
        (await host.AdvertisedNamesAsync()).Should().NotContain("docs");
    }

    [Fact]
    public async Task OverTheProtocol_ReadReturnsThePage()
    {
        var (service, _, _) = NewService();
        await using var host = await McpToolHost.StartAsync(
            ToolProfile.Standard, services => services.AddSingleton<IDocsService>(service));

        var text = await host.CallTextAsync("docs", new Dictionary<string, object?>
        {
            ["action"] = "read",
            ["path"] = "/one",
        });

        var result = JsonNode.Parse(text)!;
        result["success"]!.GetValue<bool>().Should().BeTrue(text);
        result["title"]!.GetValue<string>().Should().Be("Page One");
    }
}

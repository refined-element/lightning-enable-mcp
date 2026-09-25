using System.Text.Json;
using FluentAssertions;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;

namespace LightningEnable.Mcp.Tests.Tools;

/// <summary>
/// A3: access_l402_resource is a GENERIC paid fetch to a caller-chosen URL, so it is
/// restricted to the safe, idempotent methods GET and HEAD. A model-supplied POST /
/// PUT / PATCH / DELETE must be refused BEFORE the budget check, BEFORE the initial
/// request, and BEFORE any payment — with a descriptive error, never a blank one.
/// </summary>
public class AccessL402ResourceMethodTests
{
    private const string Url = "https://api.example.com/data";

    private readonly Mock<IL402HttpClient> _client = new(MockBehavior.Strict);
    private readonly Mock<IBudgetService> _budget = new(MockBehavior.Strict);

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    [InlineData("TRACE")]
    [InlineData("CONNECT")]
    [InlineData("post")]
    [InlineData("Post")]
    [InlineData(" post ")]
    [InlineData("\tDELETE\n")]
    [InlineData("GET POST")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UnsafeOrMalformedMethod_IsRefusedBeforeBudgetAndNetwork(string method)
    {
        var result = await AccessL402ResourceTool.AccessL402Resource(
            url: Url,
            method: method,
            body: "{\"x\":1}",
            l402Client: _client.Object,
            budgetService: _budget.Object);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();

        var error = json.GetProperty("error").GetString();
        error.Should().NotBeNullOrWhiteSpace("engineering standard: never a blank error");
        error.Should().Contain("GET").And.Contain("HEAD", "the error must say which methods are allowed");
        error.Should().Contain("access_l402_resource");

        // Strict mocks: ANY call to the client or the budget service throws, so reaching
        // here proves zero HTTP calls, zero payments and zero approval checks happened.
        _client.VerifyNoOtherCalls();
        _budget.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("GET", "GET")]
    [InlineData("HEAD", "HEAD")]
    [InlineData("get", "GET")]
    [InlineData(" head ", "HEAD")]
    public async Task GetAndHead_ReachTheClientNormalised(string method, string expected)
    {
        _client.Setup(c => c.FetchWithL402Async(
                Url, expected, null, null, 1000L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(L402FetchResult.Succeeded(Url, "ok", 200, "text/plain"));

        var result = await AccessL402ResourceTool.AccessL402Resource(
            url: Url, method: method, l402Client: _client.Object);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        _client.Verify(c => c.FetchWithL402Async(
            Url, expected, null, null, 1000L, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Get_KeepsL402PaymentBehaviour()
    {
        _client.Setup(c => c.FetchWithL402Async(
                Url, "GET", null, null, 1000L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(L402FetchResult.Succeeded(Url, "paid", 200, "text/plain",
                paidAmountSats: 21, l402Token: "mac:pre", protocol: "L402"));

        var result = await AccessL402ResourceTool.AccessL402Resource(
            url: Url, method: "GET", l402Client: _client.Object);

        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("payment").GetProperty("paid").GetBoolean().Should().BeTrue();
        json.GetProperty("payment").GetProperty("amountSats").GetInt64().Should().Be(21);
    }
}

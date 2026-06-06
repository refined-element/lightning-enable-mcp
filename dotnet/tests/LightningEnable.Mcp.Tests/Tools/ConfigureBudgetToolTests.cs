using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Moq;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Tools;

public class ConfigureBudgetToolTests
{
    [Fact]
    public async Task ConfigureBudget_NullBudgetService_ReturnsError()
    {
        var result = await ConfigureBudgetTool.ConfigureBudget(
            perRequest: 200, perSession: 2000, budgetService: null);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("Budget service not available");
    }

    [Fact]
    public async Task ConfigureBudget_Success_ReturnsEffectiveCapsJson()
    {
        var budget = new Mock<IBudgetService>();
        budget.Setup(b => b.ConfigureBudgetAsync(200, 2000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfigureBudgetResult.Ok(200, 2000));

        var result = await ConfigureBudgetTool.ConfigureBudget(
            perRequest: 200, perSession: 2000, budgetService: budget.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        var limits = json.RootElement.GetProperty("limits");
        limits.GetProperty("perRequestSats").GetInt64().Should().Be(200);
        limits.GetProperty("perSessionSats").GetInt64().Should().Be(2000);
        json.RootElement.GetProperty("message").GetString().Should().Contain("lowered");
    }

    [Fact]
    public async Task ConfigureBudget_TightenOnlyRejection_SurfacesErrorJson()
    {
        // The service refuses an attempt to RAISE limits; the tool must surface that.
        var budget = new Mock<IBudgetService>();
        budget.Setup(b => b.ConfigureBudgetAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfigureBudgetResult.Fail("configure_budget can only LOWER spending limits, not raise them."));

        var result = await ConfigureBudgetTool.ConfigureBudget(
            perRequest: 9_999_999, perSession: 9_999_999, budgetService: budget.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("can only LOWER");
    }

    [Fact]
    public async Task ConfigureBudget_InvalidInputRejection_SurfacesErrorJson()
    {
        var budget = new Mock<IBudgetService>();
        budget.Setup(b => b.ConfigureBudgetAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfigureBudgetResult.Fail("per_request cannot exceed per_session."));

        var result = await ConfigureBudgetTool.ConfigureBudget(
            perRequest: 6000, perSession: 5000, budgetService: budget.Object);

        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("error").GetString().Should().Contain("exceed");
    }
}

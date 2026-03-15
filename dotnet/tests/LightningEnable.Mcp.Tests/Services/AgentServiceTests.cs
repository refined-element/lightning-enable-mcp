using System.Net;
using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using Moq;
using Moq.Protected;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Services;

public class AgentServiceTests
{
    private readonly Mock<IBudgetConfigurationService> _configServiceMock;

    public AgentServiceTests()
    {
        _configServiceMock = new Mock<IBudgetConfigurationService>();
        _configServiceMock.Setup(c => c.Configuration).Returns(new UserBudgetConfiguration
        {
            LightningEnableApiKey = "test-api-key-123"
        });
    }

    private (AgentService service, Mock<HttpMessageHandler> handler) CreateServiceWithHandler(
        HttpStatusCode statusCode, string responseContent, string? apiKey = "test-api-key-123")
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(responseContent, System.Text.Encoding.UTF8, "application/json")
            });

        if (apiKey != null)
        {
            _configServiceMock.Setup(c => c.Configuration).Returns(new UserBudgetConfiguration
            {
                LightningEnableApiKey = apiKey
            });
        }
        else
        {
            _configServiceMock.Setup(c => c.Configuration).Returns(new UserBudgetConfiguration());
        }

        var httpClient = new HttpClient(handlerMock.Object);
        var service = new AgentService(httpClient, _configServiceMock.Object);

        return (service, handlerMock);
    }

    [Fact]
    public async Task DiscoverCapabilities_SuccessfulResponse_ParsesCorrectly()
    {
        // Arrange
        var apiResponse = JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new
                {
                    eventId = "evt-001",
                    serviceId = "translate-svc",
                    pubkey = "npub1abc",
                    content = "Translation service",
                    categories = new[] { "ai", "translation" },
                    hashtags = new[] { "nlp" },
                    priceSats = 100,
                    l402Endpoint = "https://api.example.com/l402/translate",
                    createdAt = 1700000000L
                }
            },
            total = 1
        });

        var (service, _) = CreateServiceWithHandler(HttpStatusCode.OK, apiResponse);

        // Act
        var result = await service.DiscoverCapabilitiesAsync("ai", null, null, 20, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Capabilities.Should().HaveCount(1);
        result.Total.Should().Be(1);

        var cap = result.Capabilities[0];
        cap.EventId.Should().Be("evt-001");
        cap.ServiceId.Should().Be("translate-svc");
        cap.Content.Should().Be("Translation service");
        cap.Categories.Should().Contain("ai");
        cap.Categories.Should().Contain("translation");
        cap.PriceSats.Should().Be(100);
        cap.L402Endpoint.Should().Be("https://api.example.com/l402/translate");
    }

    [Fact]
    public async Task DiscoverCapabilities_ApiError_FallsBackToRegistry()
    {
        // Arrange - first call returns 500, second call (fallback) returns 200
        var registryResponse = JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new
                {
                    name = "fallback-svc",
                    description = "Fallback service",
                    parsedCategories = new[] { "data" },
                    defaultPriceSats = 50,
                    proxyBaseUrl = "https://fallback.example.com"
                }
            },
            total = 1
        });

        var callCount = 0;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // First call: capabilities endpoint fails
                    return new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.InternalServerError,
                        Content = new StringContent("{\"error\":\"Server error\"}")
                    };
                }

                // Second call: fallback registry succeeds
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(registryResponse, System.Text.Encoding.UTF8, "application/json")
                };
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var service = new AgentService(httpClient, _configServiceMock.Object);

        // Act
        var result = await service.DiscoverCapabilitiesAsync("data", null, null, 20, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Capabilities.Should().HaveCount(1);
        result.Capabilities[0].ServiceId.Should().Be("fallback-svc");
        result.Capabilities[0].L402Endpoint.Should().Be("https://fallback.example.com");
        callCount.Should().Be(2, "should have made two HTTP calls (original + fallback)");
    }

    [Fact]
    public async Task PublishCapability_NotConfigured_ReturnsError()
    {
        // Arrange - no API key
        var (service, _) = CreateServiceWithHandler(HttpStatusCode.OK, "{}", apiKey: null);

        // Act
        var result = await service.PublishCapabilityAsync(
            "my-svc", new[] { "ai" }, "A service", 100,
            null, null, null, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("API key not configured");
    }

    [Fact]
    public async Task RequestService_ValidRequest_ReturnsSuccess()
    {
        // Arrange
        var apiResponse = JsonSerializer.Serialize(new
        {
            requestEventId = "req-evt-001",
            l402Endpoint = "https://api.example.com/l402/service"
        });

        var (service, _) = CreateServiceWithHandler(HttpStatusCode.OK, apiResponse);

        // Act
        var result = await service.RequestServiceAsync(
            "cap-evt-001", 500, "{\"text\":\"Hello\"}", CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.RequestEventId.Should().Be("req-evt-001");
        result.L402Endpoint.Should().Be("https://api.example.com/l402/service");
    }
}

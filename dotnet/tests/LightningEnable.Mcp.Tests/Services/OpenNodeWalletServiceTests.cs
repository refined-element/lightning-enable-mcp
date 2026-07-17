using System.Net;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using FluentAssertions;
using Moq;
using Moq.Protected;

namespace LightningEnable.Mcp.Tests.Services;

public class OpenNodeWalletServiceTests
{
    private Mock<HttpMessageHandler> CreateMockHandler(HttpStatusCode statusCode, string responseContent)
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(responseContent)
            });
        return mockHandler;
    }

    #region Configuration Tests

    [Fact]
    public void IsConfigured_WithApiKey_ReturnsTrue()
    {
        // Arrange - Set environment variable for this test
        var originalKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", "test-api-key");
            var httpClient = new HttpClient();
            var service = new OpenNodeWalletService(httpClient);

            // Act & Assert
            service.IsConfigured.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", originalKey);
        }
    }

    [Fact]
    public void IsConfigured_WithoutApiKey_ReturnsFalse()
    {
        // Arrange
        var originalKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", null);
            var httpClient = new HttpClient();
            var service = new OpenNodeWalletService(httpClient);

            // Act & Assert
            service.IsConfigured.Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", originalKey);
        }
    }

    [Fact]
    public void Constructor_DevEnvironment_UsesDevUrl()
    {
        // Arrange
        var originalKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        var originalEnv = Environment.GetEnvironmentVariable("OPENNODE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", "test-key");
            Environment.SetEnvironmentVariable("OPENNODE_ENVIRONMENT", "dev");

            var httpClient = new HttpClient();
            var service = new OpenNodeWalletService(httpClient);

            // Assert - Check via GetConfig that returns the base URL
            var config = service.GetConfig();
            config.Should().NotBeNull();
            config!.RelayUrl.Should().Contain("dev-api.opennode.com");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", originalKey);
            Environment.SetEnvironmentVariable("OPENNODE_ENVIRONMENT", originalEnv);
        }
    }

    [Fact]
    public void Constructor_ProductionEnvironment_UsesProdUrl()
    {
        // Arrange
        var originalKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        var originalEnv = Environment.GetEnvironmentVariable("OPENNODE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", "test-key");
            Environment.SetEnvironmentVariable("OPENNODE_ENVIRONMENT", "production");

            var httpClient = new HttpClient();
            var service = new OpenNodeWalletService(httpClient);

            // Assert
            var config = service.GetConfig();
            config.Should().NotBeNull();
            config!.RelayUrl.Should().Contain("api.opennode.com");
            config.RelayUrl.Should().NotContain("dev-api");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", originalKey);
            Environment.SetEnvironmentVariable("OPENNODE_ENVIRONMENT", originalEnv);
        }
    }

    #endregion

    #region PayInvoiceAsync Tests

    [Fact]
    public async Task PayInvoiceAsync_NotConfigured_ReturnsFailure()
    {
        // Arrange
        var originalKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", null);
            var httpClient = new HttpClient();
            var service = new OpenNodeWalletService(httpClient);

            // Act
            var result = await service.PayInvoiceAsync("lnbc100n1p3abcdef");

            // Assert
            result.Success.Should().BeFalse();
            result.ErrorCode.Should().Be("NOT_CONFIGURED");
            result.ErrorMessage.Should().Contain("not configured");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", originalKey);
        }
    }

    [Fact]
    public async Task PayInvoiceAsync_SuccessfulPayment_ReturnsPreimage()
    {
        // Arrange
        var originalKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", "test-key");

            var responseJson = JsonSerializer.Serialize(new
            {
                data = new
                {
                    id = "withdrawal-123",
                    status = "paid",
                    preimage = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20"
                }
            });

            var mockHandler = CreateMockHandler(HttpStatusCode.OK, responseJson);
            var httpClient = new HttpClient(mockHandler.Object)
            {
                BaseAddress = new Uri("https://api.opennode.com/v2/")
            };
            var service = new OpenNodeWalletService(httpClient);

            // Act
            var result = await service.PayInvoiceAsync("lnbc100n1p3abcdef");

            // Assert
            result.Success.Should().BeTrue();
            result.PreimageHex.Should().Be("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", originalKey);
        }
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("processing")]
    public async Task PayInvoiceAsync_PendingStatus_IsNotReportedAsSuccess(string status)
    {
        // This test previously asserted Success == true for a pending payment,
        // enshrining the bug: an in-flight payment may still FAIL, and a caller
        // that reads it as success proceeds believing it paid.

        // Arrange
        var originalKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", "test-key");

            var responseJson = JsonSerializer.Serialize(new
            {
                data = new
                {
                    id = "withdrawal-456",
                    status
                }
            });

            var mockHandler = CreateMockHandler(HttpStatusCode.OK, responseJson);
            var httpClient = new HttpClient(mockHandler.Object)
            {
                BaseAddress = new Uri("https://api.opennode.com/v2/")
            };
            var service = new OpenNodeWalletService(httpClient);

            // Act
            var result = await service.PayInvoiceAsync("lnbc100n1p3abcdef");

            // Assert
            result.Success.Should().BeFalse("a payment that may still fail is not a settled payment");
            result.IsPending.Should().BeTrue();
            result.PreimageHex.Should().BeNull();
            result.HasPreimage.Should().BeFalse();
            // The withdrawal ID is for tracking only — never as proof of payment.
            result.TrackingId.Should().Be("withdrawal-456");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", originalKey);
        }
    }

    [Fact]
    public async Task PayInvoiceAsync_SettledWithoutPreimage_IsSuccessButUnprovable()
    {
        // Arrange
        var originalKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", "test-key");

            var responseJson = JsonSerializer.Serialize(new
            {
                data = new
                {
                    id = "withdrawal-123",
                    status = "paid"
                }
            });

            var mockHandler = CreateMockHandler(HttpStatusCode.OK, responseJson);
            var httpClient = new HttpClient(mockHandler.Object)
            {
                BaseAddress = new Uri("https://api.opennode.com/v2/")
            };
            var service = new OpenNodeWalletService(httpClient);

            // Act
            var result = await service.PayInvoiceAsync("lnbc100n1p3abcdef");

            // Assert — the funds are gone, so this IS a success; it just cannot be proven.
            result.Success.Should().BeTrue();
            result.IsPending.Should().BeFalse();
            result.HasPreimage.Should().BeFalse();
            result.PreimageHex.Should().BeNull("a withdrawal ID must never occupy the preimage field");
            result.TrackingId.Should().Be("withdrawal-123");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", originalKey);
        }
    }

    [Theory]
    [InlineData("lnbc100n1p3abcdef")]      // OpenNode echoing the invoice back
    [InlineData("withdrawal-123")]          // an internal identifier
    [InlineData("not-a-preimage")]
    public async Task PayInvoiceAsync_NonPreimageInPreimageField_IsNotTreatedAsProof(string bogus)
    {
        // Arrange
        var originalKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", "test-key");

            var responseJson = JsonSerializer.Serialize(new
            {
                data = new
                {
                    id = "withdrawal-123",
                    status = "paid",
                    preimage = bogus
                }
            });

            var mockHandler = CreateMockHandler(HttpStatusCode.OK, responseJson);
            var httpClient = new HttpClient(mockHandler.Object)
            {
                BaseAddress = new Uri("https://api.opennode.com/v2/")
            };
            var service = new OpenNodeWalletService(httpClient);

            // Act
            var result = await service.PayInvoiceAsync("lnbc100n1p3abcdef");

            // Assert
            result.Success.Should().BeTrue();
            result.HasPreimage.Should().BeFalse();
            result.PreimageHex.Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", originalKey);
        }
    }

    [Fact]
    public async Task PayInvoiceAsync_FailedStatus_ReturnsError()
    {
        // Arrange
        var originalKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", "test-key");

            var responseJson = JsonSerializer.Serialize(new
            {
                data = new
                {
                    id = "withdrawal-789",
                    status = "failed"
                }
            });

            var mockHandler = CreateMockHandler(HttpStatusCode.OK, responseJson);
            var httpClient = new HttpClient(mockHandler.Object)
            {
                BaseAddress = new Uri("https://api.opennode.com/v2/")
            };
            var service = new OpenNodeWalletService(httpClient);

            // Act
            var result = await service.PayInvoiceAsync("lnbc100n1p3abcdef");

            // Assert
            result.Success.Should().BeFalse();
            result.ErrorCode.Should().Be("PAYMENT_FAILED");
            result.ErrorMessage.Should().Contain("failed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", originalKey);
        }
    }

    [Fact]
    public async Task PayInvoiceAsync_HttpError_ReturnsError()
    {
        // Arrange
        var originalKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", "test-key");

            var mockHandler = CreateMockHandler(HttpStatusCode.BadRequest, "{\"error\": \"Invalid invoice\"}");
            var httpClient = new HttpClient(mockHandler.Object)
            {
                BaseAddress = new Uri("https://api.opennode.com/v2/")
            };
            var service = new OpenNodeWalletService(httpClient);

            // Act
            var result = await service.PayInvoiceAsync("invalid-invoice");

            // Assert
            result.Success.Should().BeFalse();
            result.ErrorCode.Should().Be("API_ERROR");
            result.ErrorMessage.Should().Contain("API error");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", originalKey);
        }
    }

    [Fact]
    public async Task PayInvoiceAsync_InvalidJsonResponse_ReturnsError()
    {
        // Arrange
        var originalKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", "test-key");

            var mockHandler = CreateMockHandler(HttpStatusCode.OK, "not valid json");
            var httpClient = new HttpClient(mockHandler.Object)
            {
                BaseAddress = new Uri("https://api.opennode.com/v2/")
            };
            var service = new OpenNodeWalletService(httpClient);

            // Act
            var result = await service.PayInvoiceAsync("lnbc100n1p3abcdef");

            // Assert
            result.Success.Should().BeFalse();
            result.ErrorCode.Should().Be("JSON_ERROR");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", originalKey);
        }
    }

    [Fact]
    public async Task PayInvoiceAsync_EmptyDataResponse_ReturnsError()
    {
        // Arrange
        var originalKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", "test-key");

            var responseJson = JsonSerializer.Serialize(new { data = (object?)null });
            var mockHandler = CreateMockHandler(HttpStatusCode.OK, responseJson);
            var httpClient = new HttpClient(mockHandler.Object)
            {
                BaseAddress = new Uri("https://api.opennode.com/v2/")
            };
            var service = new OpenNodeWalletService(httpClient);

            // Act
            var result = await service.PayInvoiceAsync("lnbc100n1p3abcdef");

            // Assert
            result.Success.Should().BeFalse();
            result.ErrorCode.Should().Be("INVALID_RESPONSE");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", originalKey);
        }
    }

    #endregion

    #region GetBalanceAsync Tests

    [Fact]
    public async Task GetBalanceAsync_NotConfigured_ThrowsException()
    {
        // Arrange
        var originalKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", null);
            var httpClient = new HttpClient();
            var service = new OpenNodeWalletService(httpClient);

            // Act
            var act = async () => await service.GetBalanceAsync();

            // Assert
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*not configured*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", originalKey);
        }
    }

    [Fact]
    public async Task GetBalanceAsync_Success_ReturnsBalance()
    {
        // Arrange
        var originalKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", "test-key");

            var responseJson = JsonSerializer.Serialize(new
            {
                data = new
                {
                    balance = new { BTC = 0.001m } // 100,000 sats
                }
            });

            var mockHandler = CreateMockHandler(HttpStatusCode.OK, responseJson);
            var httpClient = new HttpClient(mockHandler.Object)
            {
                BaseAddress = new Uri("https://api.opennode.com/v2/")
            };
            var service = new OpenNodeWalletService(httpClient);

            // Act
            var result = await service.GetBalanceAsync();

            // Assert
            result.BalanceMsat.Should().Be(100_000_000); // 100,000 sats = 100,000,000 msats
            result.BalanceSats.Should().Be(100_000);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", originalKey);
        }
    }

    [Fact]
    public async Task GetBalanceAsync_Error_ReturnsNegativeOne()
    {
        // Arrange
        var originalKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", "test-key");

            var mockHandler = CreateMockHandler(HttpStatusCode.NotFound, "{}");
            var httpClient = new HttpClient(mockHandler.Object)
            {
                BaseAddress = new Uri("https://api.opennode.com/v2/")
            };
            var service = new OpenNodeWalletService(httpClient);

            // Act
            var result = await service.GetBalanceAsync();

            // Assert
            result.BalanceMsat.Should().Be(-1000); // -1 sats = -1000 msats
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", originalKey);
        }
    }

    #endregion

    #region GetConfig Tests

    [Fact]
    public void GetConfig_NotConfigured_ReturnsNull()
    {
        // Arrange
        var originalKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", null);
            var httpClient = new HttpClient();
            var service = new OpenNodeWalletService(httpClient);

            // Act
            var config = service.GetConfig();

            // Assert
            config.Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", originalKey);
        }
    }

    [Fact]
    public void GetConfig_Configured_ReturnsPlaceholderConfig()
    {
        // Arrange
        var originalKey = Environment.GetEnvironmentVariable("OPENNODE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", "test-key");
            var httpClient = new HttpClient();
            var service = new OpenNodeWalletService(httpClient);

            // Act
            var config = service.GetConfig();

            // Assert
            config.Should().NotBeNull();
            config!.WalletPubkey.Should().Be("opennode");
            config.Secret.Should().Be("opennode");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENNODE_API_KEY", originalKey);
        }
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var httpClient = new HttpClient();
        var service = new OpenNodeWalletService(httpClient);

        // Act & Assert - should not throw
        service.Dispose();
        service.Dispose();
    }

    #endregion
}

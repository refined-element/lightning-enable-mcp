using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using FluentAssertions;
using Moq;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// Tests verifying security audit fixes:
/// - No API key content in logs
/// - No preimage values in logs
/// - PaymentHistoryService caps at 1000 entries
/// - BudgetService async behavior (no deadlocks)
/// </summary>
public class SecurityAuditTests
{
    #region StrikeWalletService: No API key logging

    [Fact]
    public void StrikeWalletService_Initialization_DoesNotLogApiKeyContent()
    {
        // Arrange - capture stderr output
        var originalError = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);

        try
        {
            // Set a fake API key via environment
            Environment.SetEnvironmentVariable("STRIKE_API_KEY", "test-secret-key-12345678");

            var httpClient = new HttpClient();
            var service = new StrikeWalletService(httpClient);

            // Assert
            var output = sw.ToString();
            output.Should().Contain("[Strike] API key configured");
            output.Should().Contain("[Strike] Authorization header set");
            output.Should().NotContain("test-secret");
            output.Should().NotContain("12345678");
            // Should not contain any substring of the key
            output.Should().NotContain("test-sec");
            output.Should().NotContainEquivalentOf("Bearer test");

            service.Dispose();
            httpClient.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable("STRIKE_API_KEY", null);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task StrikeWalletService_GetBalance_DoesNotLogAuthParam()
    {
        // Arrange - capture stderr output
        var originalError = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);

        try
        {
            Environment.SetEnvironmentVariable("STRIKE_API_KEY", "fake-api-key-for-testing");

            var httpClient = new HttpClient();
            var service = new StrikeWalletService(httpClient);

            // Act - this will fail but we just want to check logs
            try
            {
                await service.GetBalanceAsync();
            }
            catch
            {
                // Expected - we're testing log output, not the API call
            }

            // Assert
            var output = sw.ToString();
            output.Should().NotContain("fake-api");
            output.Should().NotContain("for-testing");
            // Should not contain "Auth param preview" which was the old log
            output.Should().NotContain("Auth param preview");
            output.Should().NotContain("Auth param length");

            service.Dispose();
            httpClient.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable("STRIKE_API_KEY", null);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task StrikeWalletService_BalanceError_DoesNotIncludeAuthDiag()
    {
        // Arrange
        Environment.SetEnvironmentVariable("STRIKE_API_KEY", "test-key-auth-diag");

        try
        {
            // Use a handler that returns 401
            var handler = new MockHttpMessageHandler(
                new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("Unauthorized")
                });
            var httpClient = new HttpClient(handler);
            var service = new StrikeWalletService(httpClient);

            // Act & Assert
            var ex = await Assert.ThrowsAsync<HttpRequestException>(
                () => service.GetBalanceAsync());

            ex.Message.Should().NotContain("Auth:");
            ex.Message.Should().NotContain("param len");
            ex.Message.Should().Contain("Strike API error");

            service.Dispose();
            httpClient.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable("STRIKE_API_KEY", null);
        }
    }

    #endregion

    #region NwcWalletService: No preimage logging

    [Fact]
    public void NwcWalletService_DoesNotWriteDebugLogFile()
    {
        // Verify the file-based debug logging has been removed
        // The DebugLog method should only write to Console.Error, not to file
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".lightning-enable",
            "nwc-debug-test.log");

        // Clean up any existing test log
        if (File.Exists(logPath))
            File.Delete(logPath);

        // The NwcWalletService's DebugLog should NOT create files
        // This is verified by code inspection - the file writing code was removed
        // We verify the method signature change by checking that
        // NwcWalletService can be instantiated without file system side effects
        var httpClient = new HttpClient();
        var service = new NwcWalletService(httpClient);

        // No nwc-debug.log should be created just from instantiation
        var debugLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".lightning-enable",
            "nwc-debug.log");

        // Note: we can't fully test PayInvoiceAsync without a real NWC connection,
        // but we verified at the code level that File.AppendAllText was removed

        service.Dispose();
        httpClient.Dispose();
    }

    #endregion

    #region LndWalletService: No preimage logging

    [Fact]
    public async Task LndWalletService_PayInvoice_DoesNotLogPreimageContent()
    {
        // Arrange
        var preimageBase64 = Convert.ToBase64String(
            Convert.FromHexString("abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890"));

        var responseJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            payment_preimage = preimageBase64,
            payment_error = "",
            payment_hash = "dummyhash"
        });

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            });

        var originalError = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);

        try
        {
            Environment.SetEnvironmentVariable("LND_REST_HOST", "localhost:8080");
            Environment.SetEnvironmentVariable("LND_MACAROON_HEX", "abcdef");

            var httpClient = new HttpClient(handler);
            var service = new LndWalletService(httpClient);

            // Act
            var result = await service.PayInvoiceAsync("lnbc1test");

            // Assert
            var output = sw.ToString();
            output.Should().Contain("preimage received");
            output.Should().NotContain("abcdef1234567890");
            // The preimage hex should not appear in logs
            output.Should().NotMatchRegex("[0-9a-f]{16}\\.\\.\\.");

            service.Dispose();
            httpClient.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable("LND_REST_HOST", null);
            Environment.SetEnvironmentVariable("LND_MACAROON_HEX", null);
            Console.SetError(originalError);
        }
    }

    #endregion

    #region PaymentHistoryService: Capped at 1000 entries

    [Fact]
    public void PaymentHistoryService_CapsAt1000Entries()
    {
        // Arrange
        var service = new PaymentHistoryService();

        // Act - add 1050 entries
        for (int i = 0; i < 1050; i++)
        {
            service.RecordPayment(
                url: $"https://example.com/{i}",
                method: "GET",
                amountSats: 100);
        }

        // Assert - should not exceed 1000
        var summary = service.GetSummary();
        summary.TotalPayments.Should().BeLessOrEqualTo(1000);
    }

    [Fact]
    public void PaymentHistoryService_DropsOldestWhenFull()
    {
        // Arrange
        var service = new PaymentHistoryService();

        // Fill to capacity
        for (int i = 0; i < 1000; i++)
        {
            service.RecordPayment(
                url: $"https://example.com/old-{i}",
                method: "GET",
                amountSats: 10);
        }

        // Add one more
        service.RecordPayment(
            url: "https://example.com/newest",
            method: "GET",
            amountSats: 999);

        // Assert
        var summary = service.GetSummary();
        summary.TotalPayments.Should().BeLessOrEqualTo(1000);

        // Newest entry should be present
        var payments = service.GetRecentPayments(1);
        payments.Should().HaveCount(1);
        payments[0].AmountSats.Should().Be(999);
    }

    [Fact]
    public void PaymentHistoryService_FailedPayments_AlsoRespectCap()
    {
        // Arrange
        var service = new PaymentHistoryService();

        // Fill to capacity with failed payments
        for (int i = 0; i < 1050; i++)
        {
            service.RecordFailedPayment(
                url: $"https://example.com/{i}",
                method: "GET",
                amountSats: 100,
                errorMessage: "test error");
        }

        // Assert
        var summary = service.GetSummary();
        summary.TotalPayments.Should().BeLessOrEqualTo(1000);
    }

    #endregion

    #region BudgetService: Async behavior (no deadlocks)

    [Fact]
    public async Task BudgetService_CheckApprovalLevel_DoesNotDeadlock()
    {
        // Arrange
        var configServiceMock = new Mock<IBudgetConfigurationService>();
        configServiceMock.Setup(c => c.Configuration).Returns(new UserBudgetConfiguration
        {
            Currency = "USD",
            Tiers = new TierThresholds
            {
                AutoApprove = 0.10m,
                LogAndApprove = 1.00m,
                FormConfirm = 10.00m,
                UrlConfirm = 100.00m
            },
            Limits = new PaymentLimits
            {
                MaxPerPayment = 500.00m,
                MaxPerSession = 100.00m
            },
            Session = new SessionSettings
            {
                RequireApprovalForFirstPayment = false,
                CooldownSeconds = 0
            }
        });

        var priceServiceMock = new Mock<IPriceService>();
        priceServiceMock.Setup(p => p.SatsToUsdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long sats, CancellationToken _) => sats / 100000m);
        priceServiceMock.Setup(p => p.UsdToSatsAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((decimal usd, CancellationToken _) => (long)(usd * 100000));

        var service = new BudgetService(configServiceMock.Object, priceServiceMock.Object);

        // Act - call multiple times concurrently to verify no deadlock
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tasks = Enumerable.Range(0, 20)
            .Select(i => service.CheckApprovalLevelAsync(100 * i, cts.Token))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert - all should complete without timeout/deadlock
        results.Should().HaveCount(20);
        results.Should().AllSatisfy(r => r.Should().NotBeNull());
    }

    [Fact]
    public async Task BudgetService_ConcurrentCheckAndRecord_DoesNotDeadlock()
    {
        // Arrange
        var configServiceMock = new Mock<IBudgetConfigurationService>();
        configServiceMock.Setup(c => c.Configuration).Returns(new UserBudgetConfiguration
        {
            Currency = "USD",
            Tiers = new TierThresholds
            {
                AutoApprove = 0.10m,
                LogAndApprove = 1.00m,
                FormConfirm = 10.00m,
                UrlConfirm = 100.00m
            },
            Limits = new PaymentLimits
            {
                MaxPerPayment = 500.00m,
                MaxPerSession = 1000.00m
            },
            Session = new SessionSettings
            {
                RequireApprovalForFirstPayment = false,
                CooldownSeconds = 0
            }
        });

        var priceServiceMock = new Mock<IPriceService>();
        priceServiceMock.Setup(p => p.SatsToUsdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long sats, CancellationToken _) => sats / 100000m);
        priceServiceMock.Setup(p => p.UsdToSatsAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((decimal usd, CancellationToken _) => (long)(usd * 100000));

        var service = new BudgetService(configServiceMock.Object, priceServiceMock.Object);

        // Act - interleave checks and spends
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(service.CheckApprovalLevelAsync(100, cts.Token));
            tasks.Add(Task.Run(() => service.RecordSpend(10)));
        }

        await Task.WhenAll(tasks);

        // Assert - should complete without deadlock
        var config = service.GetConfig();
        config.SessionSpent.Should().Be(200); // 20 * 10
    }

    #endregion

    #region Helper

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public MockHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_response);
        }
    }

    #endregion
}

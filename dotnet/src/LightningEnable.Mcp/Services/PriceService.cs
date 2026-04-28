using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Service for converting between USD and satoshis using current BTC/USD price.
///
/// Design:
/// • Three independent public sources: CoinGecko, Coinbase, Kraken.
/// • On cache miss, all three are queried in parallel and the first successful
///   response wins (resilient to a single slow/failing source).
/// • Cache duration is short (60 seconds) so price stays close to spot while
///   absorbing burst traffic and avoiding rate-limit pressure.
/// • If every source fails, the service THROWS — there is no hardcoded
///   fallback price. A wrong fake price would silently mis-evaluate budgets.
/// • Every fetch attempt is logged (source, latency, success/failure).
/// </summary>
public interface IPriceService
{
    /// <summary>
    /// Gets the current BTC/USD price. Throws if all sources fail and no
    /// recent cached value is available.
    /// </summary>
    Task<decimal> GetBtcPriceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts USD to satoshis using the current BTC price.
    /// </summary>
    Task<long> UsdToSatsAsync(decimal usd, CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts satoshis to USD using the current BTC price.
    /// </summary>
    Task<decimal> SatsToUsdAsync(long sats, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the most recent successfully fetched price along with the
    /// source name and timestamp. Returns null if no fetch has succeeded yet.
    /// Does not trigger a fresh fetch — use GetBtcPriceAsync for that.
    /// </summary>
    PriceSnapshot? GetLastSnapshot();
}

/// <summary>
/// A point-in-time price snapshot with provenance.
/// </summary>
public sealed record PriceSnapshot(decimal BtcUsd, string Source, DateTime FetchedAtUtc);

/// <summary>
/// Thrown when every price source fails and no recent cached value is available.
/// </summary>
public sealed class PriceUnavailableException : InvalidOperationException
{
    public PriceUnavailableException(string message) : base(message) { }
    public PriceUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}

public class PriceService : IPriceService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PerSourceTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly ILogger<PriceService>? _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private PriceSnapshot? _cached;

    public PriceService(HttpClient httpClient, ILogger<PriceService>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<decimal> GetBtcPriceAsync(CancellationToken cancellationToken = default)
    {
        // Fast path: cache is fresh.
        var snapshot = _cached;
        if (snapshot != null && DateTime.UtcNow - snapshot.FetchedAtUtc < CacheDuration)
        {
            return snapshot.BtcUsd;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check after acquiring lock — another caller may have refreshed.
            snapshot = _cached;
            if (snapshot != null && DateTime.UtcNow - snapshot.FetchedAtUtc < CacheDuration)
            {
                return snapshot.BtcUsd;
            }

            var fresh = await FetchFirstSuccessfulAsync(cancellationToken);
            _cached = fresh;
            return fresh.BtcUsd;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<long> UsdToSatsAsync(decimal usd, CancellationToken cancellationToken = default)
    {
        var price = await GetBtcPriceAsync(cancellationToken);
        var sats = usd / price * 100_000_000m;
        return (long)Math.Ceiling(sats);
    }

    public async Task<decimal> SatsToUsdAsync(long sats, CancellationToken cancellationToken = default)
    {
        var price = await GetBtcPriceAsync(cancellationToken);
        return Math.Round(sats / 100_000_000m * price, 2);
    }

    public PriceSnapshot? GetLastSnapshot() => _cached;

    /// <summary>
    /// Fires CoinGecko, Coinbase, and Kraken in parallel and returns the first
    /// successful result. Throws PriceUnavailableException if all fail.
    /// </summary>
    private async Task<PriceSnapshot> FetchFirstSuccessfulAsync(CancellationToken cancellationToken)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var tasks = new List<Task<PriceSnapshot?>>
        {
            TryFetchAsync("CoinGecko", FetchCoinGeckoAsync, attemptCts.Token),
            TryFetchAsync("Coinbase",  FetchCoinbaseAsync,  attemptCts.Token),
            TryFetchAsync("Kraken",    FetchKrakenAsync,    attemptCts.Token)
        };

        var failures = new List<string>();
        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks);
            tasks.Remove(completed);

            var snapshot = await completed;
            if (snapshot != null)
            {
                // First success wins — cancel the rest to free sockets.
                attemptCts.Cancel();
                _logger?.LogInformation(
                    "BTC price fetched: ${Price} from {Source}",
                    snapshot.BtcUsd,
                    snapshot.Source);
                return snapshot;
            }
        }

        var message =
            "BTC price unavailable: CoinGecko, Coinbase, and Kraken all failed. " +
            "Cannot evaluate budget safely.";
        _logger?.LogError("{Message}", message);
        throw new PriceUnavailableException(message);
    }

    private async Task<PriceSnapshot?> TryFetchAsync(
        string source,
        Func<CancellationToken, Task<decimal>> fetch,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(PerSourceTimeout);

        try
        {
            var price = await fetch(timeoutCts.Token);
            if (price <= 0)
            {
                _logger?.LogWarning(
                    "BTC price fetch from {Source} returned non-positive value {Price}",
                    source,
                    price);
                return null;
            }

            return new PriceSnapshot(price, source, DateTime.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancelled (a sibling source already succeeded) — not a failure.
            return null;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning(
                "BTC price fetch from {Source} timed out after {ElapsedMs}ms",
                source,
                (DateTime.UtcNow - startedAt).TotalMilliseconds);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "BTC price fetch from {Source} failed after {ElapsedMs}ms: {Message}",
                source,
                (DateTime.UtcNow - startedAt).TotalMilliseconds,
                ex.Message);
            return null;
        }
    }

    private async Task<decimal> FetchCoinGeckoAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            "https://api.coingecko.com/api/v3/simple/price?ids=bitcoin&vs_currencies=usd",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("bitcoin", out var btc) &&
            btc.TryGetProperty("usd", out var usd))
        {
            return usd.GetDecimal();
        }
        throw new InvalidOperationException("CoinGecko response missing bitcoin.usd");
    }

    private async Task<decimal> FetchCoinbaseAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            "https://api.coinbase.com/v2/prices/BTC-USD/spot",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("amount", out var amount))
        {
            var raw = amount.GetString();
            if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
            {
                return price;
            }
            throw new InvalidOperationException($"Coinbase amount unparseable: {raw}");
        }
        throw new InvalidOperationException("Coinbase response missing data.amount");
    }

    private async Task<decimal> FetchKrakenAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            "https://api.kraken.com/0/public/Ticker?pair=XBTUSD",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        // Kraken response shape: { "error":[], "result":{ "XXBTZUSD":{ "c":["76793.00","..."], ... } } }
        if (doc.RootElement.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.Array &&
            error.GetArrayLength() > 0)
        {
            var firstError = error[0].GetString() ?? "unknown";
            throw new InvalidOperationException($"Kraken error: {firstError}");
        }

        if (!doc.RootElement.TryGetProperty("result", out var result))
        {
            throw new InvalidOperationException("Kraken response missing result");
        }

        // The pair key may be "XXBTZUSD" or "XBTUSD" depending on Kraken's mood.
        foreach (var pair in result.EnumerateObject())
        {
            if (pair.Value.TryGetProperty("c", out var close) &&
                close.ValueKind == JsonValueKind.Array &&
                close.GetArrayLength() > 0)
            {
                var raw = close[0].GetString();
                if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
                {
                    return price;
                }
                throw new InvalidOperationException($"Kraken close price unparseable: {raw}");
            }
        }

        throw new InvalidOperationException("Kraken response missing close price");
    }
}

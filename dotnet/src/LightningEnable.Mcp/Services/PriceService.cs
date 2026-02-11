using System.Text.Json;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Service for converting between USD and satoshis using current BTC price.
/// </summary>
public interface IPriceService
{
    /// <summary>
    /// Gets the current BTC/USD price.
    /// </summary>
    Task<decimal> GetBtcPriceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts USD to satoshis using current price.
    /// </summary>
    Task<long> UsdToSatsAsync(decimal usd, CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts satoshis to USD using current price.
    /// </summary>
    Task<decimal> SatsToUsdAsync(long sats, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the cached BTC price (may be stale if cache expired).
    /// Returns fallback price if no cached value available.
    /// </summary>
    decimal GetCachedBtcPrice();
}

/// <summary>
/// Price service that fetches BTC/USD from multiple sources with caching.
/// </summary>
public class PriceService : IPriceService
{
    private readonly HttpClient _httpClient;
    private readonly IWalletService? _walletService;
    private decimal _cachedPrice = 100000m; // Fallback price
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim _priceLock = new(1, 1);

    // Fallback price if all sources fail (conservative estimate)
    private const decimal FallbackPrice = 100000m;

    public PriceService(HttpClient httpClient, IWalletService? walletService = null)
    {
        _httpClient = httpClient;
        _walletService = walletService;
    }

    public async Task<decimal> GetBtcPriceAsync(CancellationToken cancellationToken = default)
    {
        // Check cache first
        if (DateTime.UtcNow < _cacheExpiry)
        {
            return _cachedPrice;
        }

        await _priceLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (DateTime.UtcNow < _cacheExpiry)
            {
                return _cachedPrice;
            }

            // Try to get price from various sources
            var price = await TryGetPriceAsync(cancellationToken);

            if (price.HasValue && price.Value > 0)
            {
                _cachedPrice = price.Value;
                _cacheExpiry = DateTime.UtcNow.Add(_cacheDuration);
                return _cachedPrice;
            }

            // Return cached price even if expired, or fallback
            return _cachedPrice > 0 ? _cachedPrice : FallbackPrice;
        }
        finally
        {
            _priceLock.Release();
        }
    }

    public async Task<long> UsdToSatsAsync(decimal usd, CancellationToken cancellationToken = default)
    {
        var btcPrice = await GetBtcPriceAsync(cancellationToken);
        var btc = usd / btcPrice;
        var sats = btc * 100_000_000m;
        return (long)Math.Ceiling(sats);
    }

    public async Task<decimal> SatsToUsdAsync(long sats, CancellationToken cancellationToken = default)
    {
        var btcPrice = await GetBtcPriceAsync(cancellationToken);
        var btc = sats / 100_000_000m;
        var usd = btc * btcPrice;
        return Math.Round(usd, 2);
    }

    public decimal GetCachedBtcPrice()
    {
        return _cachedPrice > 0 ? _cachedPrice : FallbackPrice;
    }

    private async Task<decimal?> TryGetPriceAsync(CancellationToken cancellationToken)
    {
        // Try Strike first if we have Strike wallet configured
        var strikePrice = await TryGetStrikePriceAsync(cancellationToken);
        if (strikePrice.HasValue)
        {
            return strikePrice.Value;
        }

        // Try CoinGecko as fallback (free, no API key)
        var coingeckoPrice = await TryGetCoinGeckoPriceAsync(cancellationToken);
        if (coingeckoPrice.HasValue)
        {
            return coingeckoPrice.Value;
        }

        // Try Coinbase as another fallback
        var coinbasePrice = await TryGetCoinbasePriceAsync(cancellationToken);
        if (coinbasePrice.HasValue)
        {
            return coinbasePrice.Value;
        }

        return null;
    }

    private async Task<decimal?> TryGetStrikePriceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var strikeApiKey = Environment.GetEnvironmentVariable("STRIKE_API_KEY");
            if (string.IsNullOrEmpty(strikeApiKey))
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.strike.me/v1/rates/ticker");
            request.Headers.Add("Authorization", $"Bearer {strikeApiKey}");
            request.Headers.Add("Accept", "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            // Strike returns an array of rate objects
            foreach (var rate in doc.RootElement.EnumerateArray())
            {
                var source = rate.GetProperty("sourceCurrency").GetString();
                var target = rate.GetProperty("targetCurrency").GetString();

                if (source == "BTC" && target == "USD")
                {
                    var amount = rate.GetProperty("amount").GetString();
                    if (decimal.TryParse(amount, out var price))
                    {
                        return price;
                    }
                }
            }
        }
        catch
        {
            // Silently fail and try next source
        }

        return null;
    }

    private async Task<decimal?> TryGetCoinGeckoPriceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.coingecko.com/api/v3/simple/price?ids=bitcoin&vs_currencies=usd");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("bitcoin", out var btc) &&
                btc.TryGetProperty("usd", out var usdElement))
            {
                return usdElement.GetDecimal();
            }
        }
        catch
        {
            // Silently fail and try next source
        }

        return null;
    }

    private async Task<decimal?> TryGetCoinbasePriceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.coinbase.com/v2/prices/BTC-USD/spot");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("amount", out var amountElement))
            {
                var amount = amountElement.GetString();
                if (decimal.TryParse(amount, out var price))
                {
                    return price;
                }
            }
        }
        catch
        {
            // Silently fail
        }

        return null;
    }
}

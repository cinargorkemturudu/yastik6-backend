using Yastık6.Api.Application.Features.Prices;

namespace Yastık6.Api.Infrastructure.Providers.Binance;

public sealed class BinanceClient : IBinanceClient
{
    private readonly HttpClient _client;
    private readonly ILogger<BinanceClient> _logger;

    public string ProviderName => "Binance";

    private List<BinanceTicker24HrRaw>? _bulkCache;
    private DateTime _bulkCacheTime;

    // Crypto markets operate 24/7/365
    public bool IsMarketOpen() => true;

    // Binance rate limits allow frequent polling; fetching every 3 seconds is safe
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(3);
    
    public TimeSpan MarketCloseTime => TimeSpan.Zero;

    public BinanceClient(HttpClient client, ILogger<BinanceClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<List<LatestPriceResponse>> GetMarketOverviewAsync(CancellationToken ct = default)
    {
        try
        {
            // 1. Fetch real-time Best Bid/Ask prices via the BookTicker endpoint
            var response = await _client.GetAsync("/api/v3/ticker/bookTicker", ct);
            response.EnsureSuccessStatusCode();
            
            var rawData = await response.Content.ReadFromJsonAsync<List<BinanceBookTickerRaw>>(cancellationToken: ct);

            if (rawData is null || rawData.Count == 0)
            {
                _logger.LogWarning("Binance API returned empty or invalid data for BookTicker.");
                return new List<LatestPriceResponse>();
            }

            // 2. Fetch 24-hour statistics to provide a fallback change rate for new databases
            var stats24h = await GetBulk24HrTickersAsync(ct);
            var statsDict = stats24h.ToDictionary(x => x.Symbol);

            var results = new List<LatestPriceResponse>();

            // 3. Map and merge both datasets into the standard response
            foreach (var item in rawData)
            {
                // Attempt to retrieve the corresponding 24h stat for the symbol
                statsDict.TryGetValue(item.Symbol, out var stat);

                var mappedItem = BinanceMapper.MapToResponse(item, stat);
                if (mappedItem != null)
                {
                    results.Add(mappedItem);
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while fetching market overview data from Binance API.");
            // Rethrow to let the Polly retry policy handle it
            throw; 
        }
    }
    
    public async Task<List<BinanceTicker24HrRaw>> GetBulk24HrTickersAsync(CancellationToken ct = default)
    {
        // Implement a 5-minute RAM cache to prevent rate-limiting the 24hr endpoint
        if (_bulkCache != null && (DateTime.UtcNow - _bulkCacheTime).TotalMinutes < 5)
        {
            return _bulkCache;
        }

        // Fetch data for all symbols in a single request without parameters
        var response = await _client.GetAsync("/api/v3/ticker/24hr", ct);
        if (response.IsSuccessStatusCode)
        {
            _bulkCache = await response.Content.ReadFromJsonAsync<List<BinanceTicker24HrRaw>>(cancellationToken: ct);
            _bulkCacheTime = DateTime.UtcNow;
            return _bulkCache ?? new();
        }
        
        return new();
    }
}
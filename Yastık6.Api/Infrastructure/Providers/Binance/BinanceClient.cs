using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Infrastructure.Alerts; // [YENİ] Akıllı Defter Kütüphanesi

namespace Yastık6.Api.Infrastructure.Providers.Binance;

public sealed class BinanceClient : IBinanceClient
{
    private readonly HttpClient _client;
    private readonly ILogger<BinanceClient> _logger;
    private readonly AlertTracker _tracker; // [YENİ]

    public string ProviderName => "Binance";

    private List<BinanceTicker24HrRaw>? _bulkCache;
    private DateTime _bulkCacheTime;

    public bool IsMarketOpen() => true;

    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(3);
    
    public TimeSpan MarketCloseTime => TimeSpan.Zero;

    // [GÜNCELLENDİ]: Constructor'a AlertTracker eklendi
    public BinanceClient(HttpClient client, ILogger<BinanceClient> logger, AlertTracker tracker)
    {
        _client = client;
        _logger = logger;
        _tracker = tracker;
    }

    public async Task<List<LatestPriceResponse>> GetMarketOverviewAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetAsync("/api/v3/ticker/bookTicker", ct);
            response.EnsureSuccessStatusCode();
            
            var rawData = await response.Content.ReadFromJsonAsync<List<BinanceBookTickerRaw>>(cancellationToken: ct);

            if (rawData is null || rawData.Count == 0)
            {
                _logger.LogWarning("Binance API returned empty or invalid data for BookTicker.");
                
                // [VIP ALTYAPI KRİZİ]
                _tracker.ShouldSendAlert("SYS_Binance_Empty_Data");
                return new List<LatestPriceResponse>();
            }

            var stats24h = await GetBulk24HrTickersAsync(ct);
            var statsDict = stats24h.ToDictionary(x => x.Symbol);

            var results = new List<LatestPriceResponse>();

            foreach (var item in rawData)
            {
                statsDict.TryGetValue(item.Symbol, out var stat);

                var mappedItem = BinanceMapper.MapToResponse(item, stat);
                if (mappedItem != null)
                {
                    results.Add(mappedItem);
                }
            }

            // [MÜJDE]: Bütün döngü başarılı şekilde atlatıldıysa, sabıkayı sil.
            if (_tracker.MarkAsResolved("SYS_Binance_Api_Down") || _tracker.MarkAsResolved("SYS_Binance_Empty_Data"))
            {
                _logger.LogInformation("✅ Binance API bağlantısı tekrar kuruldu ve veri çekimi başarılı.");
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while fetching market overview data from Binance API.");
            
            // [VIP ALTYAPI KRİZİ]
            _tracker.ShouldSendAlert("SYS_Binance_Api_Down");
            throw; 
        }
    }
    
    public async Task<List<BinanceTicker24HrRaw>> GetBulk24HrTickersAsync(CancellationToken ct = default)
    {
        if (_bulkCache != null && (DateTime.UtcNow - _bulkCacheTime).TotalMinutes < 5)
        {
            return _bulkCache;
        }

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
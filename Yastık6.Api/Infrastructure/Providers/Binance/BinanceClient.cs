using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Infrastructure.Alerts;
using Yastık6.Api.Infrastructure.Providers.Base;

namespace Yastık6.Api.Infrastructure.Providers.Binance;

// [DÜZELTİLDİ]: Primary Constructor ile bağımlılıklar tek satıra indirildi ve BasePriceClient'a bağlandı.
public sealed class BinanceClient(
    HttpClient client, 
    ILogger<BinanceClient> logger, 
    AlertTracker tracker, 
    ISystemClock clock) 
    : BasePriceClient(logger, tracker, clock)
{
    public override string ProviderName => "Binance";

    // Kripto piyasası 7/24 açık olduğu için IsMarketOpen ve MarketCloseTime metotlarını yazmaya gerek kalmadı.
    // Evrensel anakarttaki (BasePriceClient) varsayılan değerler zaten "hep açık" olarak ayarlı.
    
    public override TimeSpan RefreshInterval => TimeSpan.FromSeconds(30);

    private List<BinanceTicker24HrRaw>? _bulkCache;
    private DateTime _bulkCacheTime;

    // [DÜZELTİLDİ]: FetchDataAsync olarak eklendi, try-catch ve AlertTracker kriz yönetimi kodları silindi.
    protected override async Task<List<LatestPriceResponse>> FetchDataAsync(CancellationToken ct)
    {
        var response = await client.GetAsync("/api/v3/ticker/bookTicker", ct);
        
        // Hata verirse direkt patlayacak, anakart bunu havada yakalayıp 2 kez Polly ile deneyecek.
        response.EnsureSuccessStatusCode();
        
        var rawData = await response.Content.ReadFromJsonAsync<List<BinanceBookTickerRaw>>(cancellationToken: ct);

        // [DÜZELTİLDİ]: Pattern matching ile çok daha temiz bir boşluk kontrolü. Alarmı anakart çalacak.
        if (rawData is null or [])
        {
            return [];
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

        return results;
    }
    
    public async Task<List<BinanceTicker24HrRaw>> GetBulk24HrTickersAsync(CancellationToken ct = default)
    {
        // [KRİTİK DÜZELTME]: DateTime.UtcNow silindi. Binance'in yerel önbelleği artık evrensel zaman motoruna bağlı.
        if (_bulkCache != null && (Clock.UtcNow - _bulkCacheTime).TotalMinutes < 5)
        {
            return _bulkCache;
        }

        var response = await client.GetAsync("/api/v3/ticker/24hr", ct);
        if (response.IsSuccessStatusCode)
        {
            _bulkCache = await response.Content.ReadFromJsonAsync<List<BinanceTicker24HrRaw>>(cancellationToken: ct);
            
            // [KRİTİK DÜZELTME]: Önbellek zaman damgası da evrensel saatle güncelleniyor.
            _bulkCacheTime = Clock.UtcNow; 
            
            return _bulkCache ?? [];
        }
        
        return [];
    }
}
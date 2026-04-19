using Microsoft.Extensions.Caching.Memory;
using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Infrastructure.Caching;

namespace Yastık6.Api.Application.Features.Prices;

public sealed class PriceService : IPriceService
{
    private readonly IPriceHistoryRepository _repository; 
    private readonly ISystemClock _clock;
    private readonly IMemoryCache _cache;
    private static readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

    public PriceService(
        IPriceHistoryRepository repository, 
        ISystemClock clock,
        IMemoryCache cache) 
    {
        _repository = repository; 
        _clock = clock; 
        _cache = cache;
    }

    public async Task<PriceHistoryResponse> GetHistoryAsync(
        string productCode,
        PriceHistoryRange range,
        CancellationToken ct = default)
    {
        // 1. RAM'deki vitrin anahtarı
        var cacheKey = $"HistoryChart_{productCode}_{range}";

        if (!_cache.TryGetValue(cacheKey, out IReadOnlyList<PricePointDto>? points))        {
            // 3. Vitrin boşsa kapıda sıraya gir (Asenkron Kilit)
            await _cacheLock.WaitAsync(ct);
            try
            {
                // 4. Kilidi alınca tekrar bak (Biz beklerken başkası doldurmuş olabilir)
                if (!_cache.TryGetValue(cacheKey, out points))
                {
                    // 5. Hala boşsa DB'ye in (Sadece 1 şanslı thread buraya girebilir)
                    var since = CalculateSinceDate(range);
                    var bucketSize = GetBucketResolution(range);
                
                    points = await _repository.GetBucketedHistoryAsync(productCode, since, bucketSize, ct);

                    // 6. Gelen datayı dinamik süreyle vitrine koy
                    var cacheOptions = new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = GetCacheDuration(range)
                    };
                    _cache.Set(cacheKey, points, cacheOptions);
                }
            }
            finally
            {
                // 7. Ne olursa olsun kilidi aç ki sistem tıkanmasın
                _cacheLock.Release();
            }
        }

        // 8. İster 1 milisaniyede Cache'den, ister 2 saniyede DB'den gelmiş olsun; sonucu dön
        return new PriceHistoryResponse(
            ProductCode: productCode,
            Range: range,
            Points: points ?? new List<PricePointDto>()
        );
    }


    private TimeSpan GetCacheDuration(PriceHistoryRange range)
    {
        // Dinamik Önbellek Stratejisi: 
        // Veri ne kadar geniş bir zamanı kapsıyorsa (Year/FiveYears), uç noktadaki günlük değişimler 
        // grafiğin genel şeklini o kadar az etkiler. Bu yüzden geniş aralıklar RAM'de daha uzun tutulur.
        return range switch
        {
            PriceHistoryRange.Day => TimeSpan.FromMinutes(5),          // Anlık hareketler önemli
            PriceHistoryRange.Week => TimeSpan.FromMinutes(30),        // Nispeten taze kalmalı
            PriceHistoryRange.Month => TimeSpan.FromHours(2),          // Bugünün mumu çok bayatlamasın
            PriceHistoryRange.ThreeMonths => TimeSpan.FromHours(6),
            PriceHistoryRange.SixMonths => TimeSpan.FromHours(12),
            PriceHistoryRange.Year => TimeSpan.FromHours(24),          // Makro trend, günde 1 kez yenilense yeter
            PriceHistoryRange.FiveYears => TimeSpan.FromHours(24),     // Tamamen statik tarihsel veri
            PriceHistoryRange.All => TimeSpan.FromHours(24),
            _ => TimeSpan.FromMinutes(5)
        };
    }

    private DateTimeOffset CalculateSinceDate(PriceHistoryRange range)
    {
        var now = _clock.UtcNow;

        return range switch
        {
            PriceHistoryRange.Day => now.AddDays(-1),
            PriceHistoryRange.Week => now.AddDays(-7),
            PriceHistoryRange.Month => now.AddMonths(-1),
            PriceHistoryRange.ThreeMonths => now.AddMonths(-3),
            PriceHistoryRange.SixMonths => now.AddMonths(-6),
            PriceHistoryRange.Year => now.AddYears(-1),
            PriceHistoryRange.FiveYears => now.AddYears(-5),
            PriceHistoryRange.All => DateTimeOffset.MinValue,
            _ => throw new ArgumentOutOfRangeException(nameof(range), "Geçersiz tarih aralığı")
        };
    }

    private TimeSpan GetBucketResolution(PriceHistoryRange range)
    {
        return range switch
        {
            PriceHistoryRange.Day => TimeSpan.FromMinutes(15),
            PriceHistoryRange.Week => TimeSpan.FromHours(2),
            PriceHistoryRange.Month => TimeSpan.FromHours(12),
            _ => TimeSpan.FromDays(1)
        };
    }
}
using Microsoft.Extensions.Caching.Memory;
using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Infrastructure.Caching;

namespace Yastık6.Api.Application.Features.Prices;

public sealed class PriceService : IPriceService
{
    private readonly IPriceHistoryRepository _repository; 
    private readonly ISystemClock _clock;
    private readonly IMemoryCache _cache;

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
        // 1. Her ürün ve zaman aralığı için benzersiz bir RAM anahtarı (Örn: HistoryChart_BTC-USD_Month)
        var cacheKey = $"HistoryChart_{productCode}_{range}";

        // 2. Veritabanını koruyan Zırh (GetOrCreateAsync)
        var points = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            // Zaman aralığına göre (Day, Month, Year) ne kadar süre RAM'de tutulacağını dinamik hesapla
            entry.AbsoluteExpirationRelativeToNow = GetCacheDuration(range);

            var since = CalculateSinceDate(range);
            var bucketSize = GetBucketResolution(range);

            // Veritabanına sadece önbellek boşsa veya süresi dolmuşsa gidilir
            return await _repository.GetBucketedHistoryAsync(productCode, since, bucketSize, ct);
        });

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
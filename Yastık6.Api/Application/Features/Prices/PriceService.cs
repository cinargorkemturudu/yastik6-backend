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
        var cacheKey = $"HistoryChart_{productCode}_{range}";

        if (!_cache.TryGetValue(cacheKey, out IReadOnlyList<PricePointDto>? points))
        {
            await _cacheLock.WaitAsync(ct);
            try
            {
                if (!_cache.TryGetValue(cacheKey, out points))
                {
                    var since = CalculateSinceDate(range);
                    var bucketSize = GetBucketResolution(range);
                    var now = _clock.UtcNow;
            
                    // 1. Veritabanından mevcut (seyrek/sparse) veriyi çek
                    var rawPoints = await _repository.GetBucketedHistoryAsync(productCode, since, bucketSize, ct);
                
                    // 2. LOCF algoritmasının referans alacağı 'Çapa' noktasını geçmişten çek
                    var anchorPoint = await _repository.GetLastPriceBeforeAsync(productCode, since, ct);

                    // 3. LOCF (Fill-Forward) algoritmasını uygulayarak boşlukları doldur
                    points = ApplyFillForward(rawPoints, anchorPoint, since, now, bucketSize);

                    var cacheOptions = new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = GetCacheDuration(range)
                    };
                    _cache.Set(cacheKey, points, cacheOptions);
                }
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        return new PriceHistoryResponse(
            ProductCode: productCode,
            Range: range,
            Points: points ?? new List<PricePointDto>()
        );
    }

    // --- YENİ EKLENEN LOCF (FILL-FORWARD) ALGORİTMASI ---
    private IReadOnlyList<PricePointDto> ApplyFillForward(IReadOnlyList<PricePointDto> rawPoints,
        PricePointDto? anchorPoint,
        DateTimeOffset since,
        DateTimeOffset until,
        TimeSpan bucketSize)
    {
        // Eğer veritabanında hiç veri yoksa yapacak bir şey yok
        if (rawPoints == null || rawPoints.Count == 0)
            return new List<PricePointDto>();

        var densePoints = new List<PricePointDto>();
        
        // Veritabanındaki ilk veri 'since'den sonraysa oradan başla, aksi halde 'since'den başla
        var currentBucket = rawPoints[0].TimestampUtc > since ? rawPoints[0].TimestampUtc : since;
        var lastKnownPrice = rawPoints[0].Price;
        
        int i = 0;
        
        // İstenen bitiş zamanına (Şu an) kadar kovaları adım adım doldur
        while (currentBucket <= until)
        {
            // O anki zaman dilimine (bucket) kadar olan gerçek verileri tara 
            // ve en güncel fiyatı 'lastKnownPrice' olarak akılda tut.
            while (i < rawPoints.Count && rawPoints[i].TimestampUtc <= currentBucket)
            {
                lastKnownPrice = rawPoints[i].Price;
                i++;
            }
            
            // O anki zaman dilimi için (gerçek veri olmasa bile) bir nokta ekle
            densePoints.Add(new PricePointDto(currentBucket, lastKnownPrice));
            
            // Bir sonraki kovaya (bucket) geç
            currentBucket = currentBucket.Add(bucketSize);
        }

        return densePoints;
    }

    private TimeSpan GetCacheDuration(PriceHistoryRange range)
    {
        return range switch
        {
            PriceHistoryRange.Day => TimeSpan.FromMinutes(5),
            PriceHistoryRange.Week => TimeSpan.FromMinutes(30),
            PriceHistoryRange.Month => TimeSpan.FromHours(2),
            PriceHistoryRange.ThreeMonths => TimeSpan.FromHours(6),
            PriceHistoryRange.SixMonths => TimeSpan.FromHours(12),
            PriceHistoryRange.Year => TimeSpan.FromHours(24),
            PriceHistoryRange.FiveYears => TimeSpan.FromHours(24),
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
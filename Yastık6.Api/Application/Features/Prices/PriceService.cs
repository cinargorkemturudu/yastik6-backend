using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Yastık6.Api.Application.Common.Interfaces;

namespace Yastık6.Api.Application.Features.Prices;

public sealed class PriceService : IPriceService
{
    private readonly IPriceHistoryRepository _repository;
    private readonly ISystemClock _clock;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PriceService> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    // Thundering Herd (Sürü Psikolojisi) önlemi için kilit mekanizması
    private static readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

    public PriceService(
        IPriceHistoryRepository repository,
        ISystemClock clock,
        IMemoryCache cache,
        ILogger<PriceService> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _repository = repository;
        _clock = clock;
        _cache = cache;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<PriceHistoryResponse> GetHistoryAsync(
        string productCode,
        PriceHistoryRange range,
        CancellationToken ct = default)
    {
        var cacheKey = $"HistoryChart_{productCode}_{range}";

        // Only log at Information for a real incoming request - the background
        // HistoryCacheWarmer sweeps hundreds of symbols on a timer and would
        // otherwise flood Seq with routine noise. Same events, quieter by default
        // when nothing is actually watching this specific call.
        var logLevel = _httpContextAccessor.HttpContext != null ? LogLevel.Information : LogLevel.Debug;

        // 1. Önbellekte (Cache) varsa direkt dön
        if (!_cache.TryGetValue(cacheKey, out IReadOnlyList<PricePointDto>? points))
        {
            _logger.Log(logLevel, "History cache miss for {ProductCode} ({Range}), querying repository", productCode, range);
            var stopwatch = Stopwatch.StartNew();

            // 2. Kilit: Aynı anda sadece 1 istek veritabanına inebilir
            await _cacheLock.WaitAsync(ct);
            try
            {
                // Double-check: Kapıda beklerken başka bir istek cache'i doldurmuş olabilir
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

                    stopwatch.Stop();
                    _logger.Log(logLevel, "History computed for {ProductCode} ({Range}): {PointCount} points, DurationMs={DurationMs}",
                        productCode, range, points.Count, stopwatch.ElapsedMilliseconds);
                }
            }
            finally
            {
                _cacheLock.Release();
            }
        }
        else
        {
            _logger.Log(logLevel, "History cache hit for {ProductCode} ({Range})", productCode, range);
        }

        return new PriceHistoryResponse(
            ProductCode: productCode,
            Range: range,
            Points: points ?? new List<PricePointDto>()
        );
    }

    // --- LOCF (LAST OBSERVATION CARRIED FORWARD) ALGORİTMASI ---
    // --- LOCF (LAST OBSERVATION CARRIED FORWARD) ALGORİTMASI ---
    private IReadOnlyList<PricePointDto> ApplyFillForward(
        IReadOnlyList<PricePointDto> rawPoints,
        PricePointDto? anchorPoint,
        DateTimeOffset since,
        DateTimeOffset until,
        TimeSpan bucketSize)
    {
        var densePoints = new List<PricePointDto>();
        var currentBucket = since;

        // [MİMARİ DÜZELTME 1]: lastKnownPrice artık Nullable (decimal?).
        // Eski koddaki "anchor yoksa rawPoints[0]'ı al" mantığını (Backfill) TAMAMEN SİLDİK.
        // Sadece geçmişte KESİN olarak bildiğimiz bir çapa (anchor) varsa başlatıyoruz.
        decimal? lastKnownPrice = anchorPoint?.Price;

        int i = 0;

        // İstenen bitiş zamanına (Şu an) kadar kovaları adım adım ilerlet
        while (currentBucket <= until)
        {
            // Zaman tünelinde ilerlerken, o anki kovaya (bucket) düşen GERÇEK bir veri var mı diye bak.
            // Varsa, lastKnownPrice'ı bu KANITLANMIŞ yeni fiyatla güncelle.
            while (rawPoints != null && i < rawPoints.Count && rawPoints[i].TimestampUtc <= currentBucket)
            {
                lastKnownPrice = rawPoints[i].Price;
                i++;
            }

            // [MİMARİ DÜZELTME 2]: Noktayı sadece elimizde gerçekliğinden emin olduğumuz bir fiyat varsa çiz.
            // Eğer lastKnownPrice hala null ise (henüz o tarihlerde sistemde veri toplanmamışsa), 
            // sessizce pas geç ve grafiği boş bırak. Asla geleceği geçmişe kopyalama.
            if (lastKnownPrice.HasValue && lastKnownPrice.Value > 0)
            {
                densePoints.Add(new PricePointDto(currentBucket, lastKnownPrice.Value));
            }

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

using Polly;
using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Infrastructure.Alerts;

namespace Yastık6.Api.Infrastructure.Providers.Base;

/// <summary>
/// Sistemdeki tüm Fiyat Sağlayıcıların (Client) uyması gereken EVRENSEL ANAKART.
/// Bu sınıf; hata yakalama, Polly (tekrar deneme), AlertTracker (sabıka) ve Saat işlerini standartlaştırır.
/// </summary>
public abstract class BasePriceClient : IPriceProvider
{
    protected readonly ILogger Logger;
    protected readonly AlertTracker Tracker;
    protected readonly ISystemClock Clock; 

    protected BasePriceClient(ILogger logger, AlertTracker tracker, ISystemClock clock)
    {
        Logger = logger;
        Tracker = tracker;
        Clock = clock;
    }

    public abstract string ProviderName { get; }
    
    public virtual bool IsMarketOpen() => true;
    
    public abstract TimeSpan RefreshInterval { get; }
    
    public virtual TimeSpan MarketCloseTime => TimeSpan.Zero;

    public async Task<List<LatestPriceResponse>> GetMarketOverviewAsync(CancellationToken ct = default)
    {
        // DİKKAT: "if (!IsMarketOpen()) return boş;" bloğu TAMAMEN SİLİNDİ!
        // Çünkü piyasa kapalıyken bile genişletilmiş RefreshInterval ile (örn: 30dk) verinin son halini yoklamak istiyoruz.

        var retryPolicy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(
                retryCount: 2, 
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(2 * attempt), 
                onRetry: (exception, timeSpan, retryCount) => 
                {
                    Logger.LogWarning("{Provider} API geçici hata verdi ({Message}). {Delay} sn sonra tekrar deneniyor (Deneme: {RetryCount}).", 
                        ProviderName, exception.Message, timeSpan.TotalSeconds, retryCount);
                });

        try
        {
            var results = await retryPolicy.ExecuteAsync(() => FetchDataAsync(ct));

            if (results == null || !results.Any())
            {
                Logger.LogWarning("{Provider} API'den boş veri döndü.", ProviderName);
                Tracker.ShouldSendAlert($"SYS_{ProviderName}_Empty_Data");
                return new List<LatestPriceResponse>();
            }

            if (Tracker.MarkAsResolved($"SYS_{ProviderName}_Api_Down") || 
                Tracker.MarkAsResolved($"SYS_{ProviderName}_Empty_Data"))
            {
                Logger.LogInformation("✅ {Provider} API bağlantısı tekrar kuruldu ve veri çekimi başarılı.", ProviderName);
            }

            // --- İŞTE SİHİR BURADA BAŞLIYOR ---
            
            // 1. Provider'ın O ANKİ çalışma aralığını al (Açıksa 15sn, kapalıysa 30dk/24s vb.)
            var currentInterval = RefreshInterval;
            
            // 2. Tolerans (Grace Period): Veri tam yenileneceği milisaniyede stale'e düşmesin diye 
            // interval süresine %20 esneklik payı ekliyoruz. (Örn: 30dk ise 36dk geçerli sayılacak)
            var gracePeriod = TimeSpan.FromSeconds(currentInterval.TotalSeconds * 0.2);
            var validityDuration = currentInterval.Add(gracePeriod);
            
            var nowUtc = Clock.UtcNow;

            // 3. Alt sınıfların (Mapper'ların) bastığı sahte/yanlış ValidUntilUtc değerlerini acımasızca EZ.
            // Verinin ömrünü doğrudan Provider'ın kendi nabzına (Interval) bağla.
            var definitiveResults = results.Select(r => r with 
            {
                ValidUntilUtc = nowUtc.Add(validityDuration)
            }).ToList();

            return definitiveResults;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Provider} üzerinden veri çekilirken kalıcı hata oluştu.", ProviderName);
            Tracker.ShouldSendAlert($"SYS_{ProviderName}_Api_Down");
            
            return new List<LatestPriceResponse>(); 
        }
    }

    protected abstract Task<List<LatestPriceResponse>> FetchDataAsync(CancellationToken ct);
}

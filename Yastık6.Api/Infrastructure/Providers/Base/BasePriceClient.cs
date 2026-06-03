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
    protected readonly ISystemClock Clock; // [ÖNEMLİ]: DateTime.Now yerine artık bunu kullanacağız.

    protected BasePriceClient(ILogger logger, AlertTracker tracker, ISystemClock clock)
    {
        Logger = logger;
        Tracker = tracker;
        Clock = clock;
    }

    // Alt sınıfların (Harem, Binance vs.) doldurmak ZORUNDA olduğu (abstract) veya
    // ezebileceği (virtual) standart kontrat özellikleri:
    public abstract string ProviderName { get; }
    
    // Varsayılan olarak her zaman açık (Kripto borsaları gibi). Borsa/Altın için alt sınıflarda "override" edilecek.
    public virtual bool IsMarketOpen() => true;
    
    public abstract TimeSpan RefreshInterval { get; }
    
    // Varsayılan olarak hiç kapanmaz. Borsa/Altın için alt sınıflarda "override" edilecek.
    public virtual TimeSpan MarketCloseTime => TimeSpan.Zero;

    /// <summary>
    /// Worker'ın (Arka plan servisi) doğrudan çağırdığı tek metottur.
    /// Alt sınıfların yazdığı FetchDataAsync metodunu "Try-Catch" ve "Polly" kalkanı içinde güvenle çalıştırır.
    /// </summary>
    public async Task<List<LatestPriceResponse>> GetMarketOverviewAsync(CancellationToken ct = default)
    {
        // 1. KORUMA: Piyasa kapalıysa hiç istek atma, boş dön.
        if (!IsMarketOpen())
        {
            Logger.LogDebug("{Provider} piyasası şu an kapalı. İstek atlanıyor.", ProviderName);
            return new List<LatestPriceResponse>();
        }

        // 2. KORUMA: Polly Retry Politikası (Hata alırsak hemen çökmek yerine biraz bekleyip tekrar dene)
        var retryPolicy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(
                retryCount: 2, // 2 kere daha dene
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(2 * attempt), // Bekleme süresini artır
                onRetry: (exception, timeSpan, retryCount) => 
                {
                    Logger.LogWarning("{Provider} API geçici hata verdi ({Message}). {Delay} sn sonra tekrar deneniyor (Deneme: {RetryCount}).", 
                        ProviderName, exception.Message, timeSpan.TotalSeconds, retryCount);
                });

        try
        {
            // 3. İŞLEM: Alt sınıfın (Harem, Binance) kendi mantığıyla yazacağı gerçek veri çekme metodunu çalıştır.
            var results = await retryPolicy.ExecuteAsync(() => FetchDataAsync(ct));

            if (results == null || !results.Any())
            {
                Logger.LogWarning("{Provider} API'den boş veri döndü.", ProviderName);
                Tracker.ShouldSendAlert($"SYS_{ProviderName}_Empty_Data");
                return new List<LatestPriceResponse>();
            }

            // 4. MÜJDE: Her şey yolundaysa sabıka kaydını temizle
            if (Tracker.MarkAsResolved($"SYS_{ProviderName}_Api_Down") || 
                Tracker.MarkAsResolved($"SYS_{ProviderName}_Empty_Data"))
            {
                Logger.LogInformation("✅ {Provider} API bağlantısı tekrar kuruldu ve veri çekimi başarılı.", ProviderName);
            }

            return results;
        }
        catch (Exception ex)
        {
            // 5. KRİZ YÖNETİMİ: Tüm denemelere rağmen patlarsa (throw YAPMA!), deftere fısılda ve boş dön.
            Logger.LogError(ex, "{Provider} üzerinden veri çekilirken kalıcı hata oluştu.", ProviderName);
            Tracker.ShouldSendAlert($"SYS_{ProviderName}_Api_Down");
            
            // "throw" kullanmıyoruz ki Worker (ana motor) çökmesin.
            return new List<LatestPriceResponse>(); 
        }
    }

    /// <summary>
    /// Sadece bu sınıfı miras alan (Harem, Binance) sınıfların doldurmak zorunda olduğu KORUMALI (protected) metot.
    /// </summary>
    protected abstract Task<List<LatestPriceResponse>> FetchDataAsync(CancellationToken ct);
}
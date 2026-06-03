using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Infrastructure.Alerts;

namespace Yastık6.Api.Infrastructure.Providers.FreeCurrency;

public sealed class FreeCurrencyClient : IFreeCurrencyClient
{
    private readonly HttpClient _client;
    private readonly string _apiKey;
    private readonly ILogger<FreeCurrencyClient> _logger;
    private readonly AlertTracker _tracker;

    public string ProviderName => "FreeCurrency";

    /// <summary>
    /// Forex piyasası Hafta Sonu (Cumartesi ve Pazar) kapalıdır.
    /// Pazartesi 00:00 ile Cuma 23:59:59 arası açıktır.
    /// </summary>
    public bool IsMarketOpen()
    {
        // İstanbul zaman dilimine göre (UTC+3) kontrol etmek istersen DateTime.UtcNow.AddHours(3) kullanabilirsin.
        // Şimdilik standart sunucu saati üzerinden haftanın gününü kontrol ediyoruz.
        var today = DateTime.Now.DayOfWeek;
        
        return today != DayOfWeek.Saturday && today != DayOfWeek.Sunday;
    }

    /// <summary>
    /// Eğer piyasa açıksa 30 dakikada bir yenile (böylece hafta içi kotayı dengeli harcarız).
    /// Piyasa kapalıysa (Hafta sonu) istek atmayı tamamen kesmek için interval'i çok yüksek tutabilirsin 
    /// ya da üst katmanda IsMarketOpen() false ise bu client'ı hiç tetiklemezsin.
    /// </summary>
    public TimeSpan RefreshInterval => IsMarketOpen() ? TimeSpan.FromMinutes(30) : TimeSpan.FromHours(24);
    
    /// <summary>
    /// Cuma gecesi kapanış saatine ne kadar kaldığını hesaplar (Opsiyonel / Üst katman logları veya cache için).
    /// </summary>
    public TimeSpan MarketCloseTime
    {
        get
        {
            if (!IsMarketOpen()) return TimeSpan.Zero;

            var now = DateTime.Now;
            if (now.DayOfWeek == DayOfWeek.Friday)
            {
                return TimeSpan.FromDays(1) - now.TimeOfDay; // Cuma gece yarısına kalan süre
            }

            // Cuma gününe kaç gün kaldığını hesapla
            int daysUntilFriday = ((int)DayOfWeek.Friday - (int)now.DayOfWeek + 7) % 7;
            var nextFridayMidnight = now.Date.AddDays(daysUntilFriday + 1);
            
            return nextFridayMidnight - now;
        }
    }

    public FreeCurrencyClient(HttpClient client, IConfiguration config, ILogger<FreeCurrencyClient> logger, AlertTracker tracker)
    {
        _client = client;
        _logger = logger;
        _tracker = tracker;
        _apiKey = config["FreeCurrency:ApiKey"] ?? throw new InvalidOperationException("FreeCurrency API Key eksik!");
    }

    public async Task<List<LatestPriceResponse>> GetMarketOverviewAsync(CancellationToken ct = default)
    {
        // [MİMARİ DOKUNUŞ]: Üst katman gözden kaçırırsa diye burada da bir savunma hattı (Guard) kuralım.
        if (!IsMarketOpen())
        {
            _logger.LogInformation("FreeCurrency: Piyasa kapalı olduğundan API isteği atlanıyor (Hafta sonu koruması).");
            return new List<LatestPriceResponse>(); // Veya cache'deki son veriyi fırlatan bir mekanizma
        }

        try
        {
            var response = await _client.GetAsync($"/v1/latest?apikey={_apiKey}", ct);
            response.EnsureSuccessStatusCode();

            var rawData = await response.Content.ReadFromJsonAsync<FreeCurrencyResponseRaw>(cancellationToken: ct);

            if (rawData?.Data == null)
            {
                _logger.LogWarning("FreeCurrency API boş veri döndü.");
                _tracker.ShouldSendAlert("SYS_FreeCurrency_Empty_Data");
                return new List<LatestPriceResponse>();
            }

            if (_tracker.MarkAsResolved("SYS_FreeCurrency_Api_Down") || _tracker.MarkAsResolved("SYS_FreeCurrency_Empty_Data"))
            {
                _logger.LogInformation("✅ FreeCurrency API bağlantısı tekrar kuruldu.");
            }

            return FreeCurrencyMapper.MapToResponses(rawData.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FreeCurrency verisi çekilirken hata oluştu.");
            _tracker.ShouldSendAlert("SYS_FreeCurrency_Api_Down");
            throw; 
        }
    }
}
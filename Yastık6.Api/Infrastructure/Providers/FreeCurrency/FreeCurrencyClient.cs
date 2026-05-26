using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Infrastructure.Alerts; // [YENİ] Akıllı Defter Kütüphanesi

namespace Yastık6.Api.Infrastructure.Providers.FreeCurrency;

public sealed class FreeCurrencyClient : IFreeCurrencyClient
{
    private readonly HttpClient _client;
    private readonly string _apiKey;
    private readonly ILogger<FreeCurrencyClient> _logger;
    private readonly AlertTracker _tracker; // [YENİ]

    public string ProviderName => "FreeCurrency";

    public bool IsMarketOpen() => true; 

    public TimeSpan RefreshInterval => TimeSpan.FromMinutes(30);
    
    public TimeSpan MarketCloseTime => TimeSpan.Zero;

    // [GÜNCELLENDİ]: Constructor'a AlertTracker eklendi
    public FreeCurrencyClient(HttpClient client, IConfiguration config, ILogger<FreeCurrencyClient> logger, AlertTracker tracker)
    {
        _client = client;
        _logger = logger;
        _tracker = tracker;
        _apiKey = config["FreeCurrency:ApiKey"] ?? throw new InvalidOperationException("FreeCurrency API Key eksik!");
    }

    public async Task<List<LatestPriceResponse>> GetMarketOverviewAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetAsync($"/v1/latest?apikey={_apiKey}", ct);
            response.EnsureSuccessStatusCode();

            var rawData = await response.Content.ReadFromJsonAsync<FreeCurrencyResponseRaw>(cancellationToken: ct);

            if (rawData?.Data == null)
            {
                _logger.LogWarning("FreeCurrency API boş veri döndü.");
                
                // [VIP ALTYAPI KRİZİ]: Veri boş geliyorsa
                _tracker.ShouldSendAlert("SYS_FreeCurrency_Empty_Data");
                return new List<LatestPriceResponse>();
            }

            // [MÜJDE]: Veri başarıyla çekildiyse sabıkayı sil
            if (_tracker.MarkAsResolved("SYS_FreeCurrency_Api_Down") || _tracker.MarkAsResolved("SYS_FreeCurrency_Empty_Data"))
            {
                _logger.LogInformation("✅ FreeCurrency API bağlantısı tekrar kuruldu.");
            }

            return FreeCurrencyMapper.MapToResponses(rawData.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FreeCurrency verisi çekilirken hata oluştu.");
            
            // [VIP ALTYAPI KRİZİ]: API patlarsa deftere fısılda
            _tracker.ShouldSendAlert("SYS_FreeCurrency_Api_Down");
            throw; 
        }
    }
}
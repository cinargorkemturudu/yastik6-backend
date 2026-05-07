using Yastık6.Api.Application.Features.Prices;

namespace Yastık6.Api.Infrastructure.Providers.FreeCurrency;

public sealed class FreeCurrencyClient : IFreeCurrencyClient
{
    private readonly HttpClient _client;
    private readonly string _apiKey;
    private readonly ILogger<FreeCurrencyClient> _logger;

    public string ProviderName => "FreeCurrency";

    public bool IsMarketOpen() => true; // Forex globalde 24 saat işlem görür (Haftaiçi)

    // Ücretsiz paket saatlik/günlük veri verdiği için sık istek atmaya gerek yok
    public TimeSpan RefreshInterval => TimeSpan.FromMinutes(30);
    
    public TimeSpan MarketCloseTime => TimeSpan.Zero;

    public FreeCurrencyClient(HttpClient client, IConfiguration config, ILogger<FreeCurrencyClient> logger)
    {
        _client = client;
        _logger = logger;
        _apiKey = config["FreeCurrency:ApiKey"] ?? throw new InvalidOperationException("FreeCurrency API Key eksik!");
    }

    public async Task<List<LatestPriceResponse>> GetMarketOverviewAsync(CancellationToken ct = default)
    {
        try
        {
            // API'den tüm kurları USD bazlı istiyoruz
            var response = await _client.GetAsync($"/v1/latest?apikey={_apiKey}", ct);
            response.EnsureSuccessStatusCode();

            var rawData = await response.Content.ReadFromJsonAsync<FreeCurrencyResponseRaw>(cancellationToken: ct);

            if (rawData?.Data == null)
            {
                _logger.LogWarning("FreeCurrency API boş veri döndü.");
                return new List<LatestPriceResponse>();
            }

            return FreeCurrencyMapper.MapToResponses(rawData.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FreeCurrency verisi çekilirken hata oluştu.");
            throw; // Polly yakalasın diye fırlatıyoruz
        }
    }
}
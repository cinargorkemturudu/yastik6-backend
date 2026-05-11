using Yastık6.Api.Application.Features.Prices;

namespace Yastık6.Api.Infrastructure.Providers.Binance;

public sealed class BinanceClient : IBinanceClient
{
    private readonly HttpClient _client;
    private readonly ILogger<BinanceClient> _logger;

    public string ProviderName => "Binance";

    private List<BinanceTicker24HrRaw>? _bulkCache;
    private DateTime _bulkCacheTime;

    // Kripto pazarı asla uyumaz
    public bool IsMarketOpen() => true;

    // Limitleri çok geniş olduğu için 3 saniyede bir çekebiliriz
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(3);
    
    public TimeSpan MarketCloseTime => TimeSpan.Zero;

    public BinanceClient(HttpClient client, ILogger<BinanceClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<List<LatestPriceResponse>> GetMarketOverviewAsync(CancellationToken ct = default)
    {
        try
        {
            // Binance V3 BookTicker Endpoint'i: Anlık en iyi alış/satış makasını verir
            var response = await _client.GetAsync("/api/v3/ticker/bookTicker", ct);
            response.EnsureSuccessStatusCode();
            
            var rawJsonString = await response.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("[RÖNTGEN] FreeCurrency'den Gelen Cevap: {Json}", rawJsonString);

            var rawData = await response.Content.ReadFromJsonAsync<List<BinanceBookTickerRaw>>(cancellationToken: ct);

            if (rawData is null || rawData.Count == 0)
            {
                _logger.LogWarning("Binance API boş veya geçersiz veri döndü.");
                return new List<LatestPriceResponse>();
            }

            var results = new List<LatestPriceResponse>();

            foreach (var item in rawData)
            {
                var mappedItem = BinanceMapper.MapToResponse(item);
                if (mappedItem != null)
                {
                    results.Add(mappedItem);
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Binance API üzerinden veri çekilirken hata oluştu.");
            // Polly'nin hatayı yakalayıp tekrar denemesi için fırlatıyoruz
            throw; 
        }
    }
    
    public async Task<List<BinanceTicker24HrRaw>> GetBulk24HrTickersAsync(CancellationToken ct = default)
    {
        // Yedek olarak çağrıldığında Binance'i yormamak için 5 dakikalık RAM Cache!
        if (_bulkCache != null && (DateTime.UtcNow - _bulkCacheTime).TotalMinutes < 5)
        {
            return _bulkCache;
        }

        // TAM İSTEDİĞİN GİBİ: Parametresiz, tüm piyasayı tek seferde çeken "Genel İstek"
        var response = await _client.GetAsync("/api/v3/ticker/24hr", ct);
        if (response.IsSuccessStatusCode)
        {
            _bulkCache = await response.Content.ReadFromJsonAsync<List<BinanceTicker24HrRaw>>(cancellationToken: ct);
            _bulkCacheTime = DateTime.UtcNow;
            return _bulkCache ?? new();
        }
        
        return new();
    }
}
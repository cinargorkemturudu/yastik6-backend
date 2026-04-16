using System.Text.Json.Serialization;
using Yastık6.Api.Application.Features.Prices;

namespace Yastık6.Api.Infrastructure.Providers.Harem;

public sealed class HaremClient : IHaremClient
{
    private readonly HttpClient _client;
    private readonly ILogger<HaremClient> _logger;
    private readonly string _marketPath;

    public string ProviderName => "Harem";
    public bool IsMarketOpen() => true;
    public TimeSpan RefreshInterval 
    {
        get 
        {
            var now = DateTime.UtcNow.AddHours(3); 
            var isWeekend = now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday;
            var isMarketHours = now.Hour >= 8 && now.Hour < 18;

            if (!isWeekend && isMarketHours)
            {
                return TimeSpan.FromSeconds(15);
            }

            return TimeSpan.FromMinutes(30);
        }
    }    public TimeSpan MarketCloseTime => TimeSpan.Zero;
    

    public HaremClient(HttpClient client, IConfiguration config, ILogger<HaremClient> logger)
    {
        _client = client;
        _logger = logger;
        _marketPath = config["Harem:MarketPath"] 
                      ?? throw new InvalidOperationException("Harem:MarketPath konfigürasyonu eksik!");
    }

    public async Task<List<LatestPriceResponse>> GetMarketOverviewAsync(CancellationToken ct = default)
    {
        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var requestUri = $"{_marketPath}?_cb={timestamp}";

            var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
            request.Headers.Add("Pragma", "no-cache");

            var response = await _client.SendAsync(request, ct); 
            response.EnsureSuccessStatusCode();

            var rawResponse = await response.Content.ReadFromJsonAsync<HaremBaseResponseRaw>(cancellationToken: ct);
            
            if (rawResponse?.Data is null || rawResponse.Data.Count == 0)
            {
                _logger.LogWarning("Harem API response data is empty or null.");
                return new List<LatestPriceResponse>();
            }

            var result = new List<LatestPriceResponse>();

            foreach (var item in rawResponse.Data)
            {
                // [YENİ]: Veriyi dönüştürme işini tamamen Mapper'a devrettik
                var mappedItem = HaremDataMapper.MapToResponse(item);
                if (mappedItem != null)
                {
                    result.Add(mappedItem);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Harem API verisi çekilirken hata oluştu.");
            throw;
        }
    }
    

    public sealed class HaremBaseResponseRaw
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("data")] public List<HaremItemRaw>? Data { get; set; }
    }

    public sealed class HaremItemRaw
    {
        [JsonPropertyName("key")] public string Key { get; set; } = ""; 
        [JsonPropertyName("code")] public string? Code { get; set; } 
        [JsonPropertyName("buy")] public string? Buy { get; set; }
        [JsonPropertyName("sell")] public string? Sell { get; set; }
        [JsonPropertyName("last_update")] public string? LastUpdate { get; set; }
    }
}
using System.Text.Json.Serialization;
using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Infrastructure.Alerts;
using Yastık6.Api.Infrastructure.Providers.Base;

namespace Yastık6.Api.Infrastructure.Providers.Harem;

// [DÜZELTİLDİ]: Primary Constructor (C# 12) kullanıldı. Sınıf gövdesindeki kalabalık silindi.
public sealed class HaremClient(
    HttpClient client,
    IConfiguration config,
    ILogger<HaremClient> logger,
    AlertTracker tracker,
    ISystemClock clock)
    : BasePriceClient(logger, tracker, clock)
{
    private readonly string _marketPath = config["Harem:MarketPath"] is { Length: > 0 } path
        ? path
        : throw new InvalidOperationException("Harem:MarketPath konfigürasyonu eksik!");

    public override string ProviderName => "Harem";

    public override bool IsMarketOpen()
    {
        var now = Clock.UtcNow.AddHours(3); // Türkiye Saati

        // [DÜZELTİLDİ]: IDE'nin "Merge into pattern" önerisiyle or/and desenleri kullanıldı.
        var isWeekend = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var isMarketHours = now.Hour is >= 8 and < 18;

        return !isWeekend && isMarketHours;
    }

    public override TimeSpan RefreshInterval => IsMarketOpen() ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(30);

    // Harem 18:00'da tamamen durmuyor, sadece yoklama aralığımızı genişletiyoruz (bkz. RefreshInterval).
    // O yüzden günlük % değişim çapasını "piyasa kapanışı" gibi davranmayan diğer sağlayıcılarla
    // (YahooFinanceBase) aynı şekilde gece yarısına yakın sabit bir saate bağlıyoruz.
    public override TimeSpan MarketCloseTime => TimeSpan.FromHours(23.99);

    // [DÜZELTİLDİ]: Opsiyonel '= default' parametresi silindi, base metodun imzasıyla birebir aynı yapıldı.
    protected override async Task<List<LatestPriceResponse>> FetchDataAsync(CancellationToken ct)
    {
        var timestamp = new DateTimeOffset(Clock.UtcNow).ToUnixTimeMilliseconds();
        var requestUri = $"{_marketPath}?_cb={timestamp}";

        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
        request.Headers.Add("Pragma", "no-cache");

        // Artık "client" parametresi doğrudan Primary Constructor'dan geliyor, _client kullanmaya gerek kalmadı.
        var response = await client.SendAsync(request, ct);

        response.EnsureSuccessStatusCode();

        var rawResponse = await response.Content.ReadFromJsonAsync<HaremBaseResponseRaw>(cancellationToken: ct);

        // [DÜZELTİLDİ]: "Merge into pattern" (is null or []) kullanıldı.
        if (rawResponse?.Data is null or [])
        {
            return []; // [YENİ]: C# 12 Collection Expression ile 'new List<>()' yazımından kurtulduk.
        }

        var result = new List<LatestPriceResponse>();

        foreach (var item in rawResponse.Data)
        {
            var mappedItem = HaremDataMapper.MapToResponse(item);
            if (mappedItem != null)
            {
                result.Add(mappedItem);
            }
        }

        return result;
    }

    // [DÜZELTİLDİ]: Class yerine "sealed record" yapıldı. 
    // Property'ler "get; set;" yerine "get; init;" yapıldı. Tüm Instantiate, Auto-property ve Init-only uyarıları susturuldu.
    public sealed record HaremBaseResponseRaw
    {
        [JsonPropertyName("success")] public bool Success { get; init; }
        [JsonPropertyName("data")] public List<HaremItemRaw>? Data { get; init; }
    }

    public sealed record HaremItemRaw
    {
        [JsonPropertyName("key")] public string Key { get; init; } = "";
        [JsonPropertyName("code")] public string? Code { get; init; }
        [JsonPropertyName("buy")] public string? Buy { get; init; }
        [JsonPropertyName("sell")] public string? Sell { get; init; }
        [JsonPropertyName("last_update")] public string? LastUpdate { get; init; }
    }
}

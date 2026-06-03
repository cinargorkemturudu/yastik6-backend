using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Infrastructure.Alerts;
using Yastık6.Api.Infrastructure.Providers.Base;
using Yastık6.Api.Infrastructure.Time;

namespace Yastık6.Api.Infrastructure.Providers.FreeCurrency;

public sealed class FreeCurrencyClient(
    HttpClient client, 
    IConfiguration config, 
    ILogger<FreeCurrencyClient> logger, 
    AlertTracker tracker, 
    ISystemClock clock) 
    : BasePriceClient(logger, tracker, clock)
{
    private readonly string _apiKey = config["FreeCurrency:ApiKey"] 
                                      ?? throw new InvalidOperationException("FreeCurrency API Key eksik!");

    public override string ProviderName => "FreeCurrency";

    public override bool IsMarketOpen()
    {
        var today = Clock.UtcNow.DayOfWeek;
        return today is not DayOfWeek.Saturday and not DayOfWeek.Sunday;
    }

    public override TimeSpan RefreshInterval => IsMarketOpen() ? TimeSpan.FromMinutes(30) : TimeSpan.FromHours(24);
    
    public override TimeSpan MarketCloseTime
    {
        get
        {
            if (!IsMarketOpen()) return TimeSpan.Zero;

            var now = Clock.UtcNow;
            if (now.DayOfWeek == DayOfWeek.Friday)
            {
                return TimeSpan.FromDays(1) - now.TimeOfDay;
            }

            int daysUntilFriday = ((int)DayOfWeek.Friday - (int)now.DayOfWeek + 7) % 7;
            var nextFridayMidnight = now.Date.AddDays(daysUntilFriday + 1);
            
            return nextFridayMidnight - now;
        }
    }

    protected override async Task<List<LatestPriceResponse>> FetchDataAsync(CancellationToken ct)
    {
        var response = await client.GetAsync($"/v1/latest?apikey={_apiKey}", ct);
        response.EnsureSuccessStatusCode();

        var rawData = await response.Content.ReadFromJsonAsync<FreeCurrencyResponseRaw>(cancellationToken: ct);

        // Veri boşsa sadece boş liste dönüyoruz, alarmı BasePriceClient çalacak.
        if (rawData?.Data is null)
        {
            return [];
        }

        return FreeCurrencyMapper.MapToResponses(rawData.Data);
    }
}
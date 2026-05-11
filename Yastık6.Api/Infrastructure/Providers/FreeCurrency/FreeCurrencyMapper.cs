using System.Globalization;
using Yastık6.Api.Application.Features.Prices;

namespace Yastık6.Api.Infrastructure.Providers.FreeCurrency;

public static class FreeCurrencyMapper
{
    public static List<LatestPriceResponse> MapToResponses(Dictionary<string, decimal> rates)
    {
        var results = new List<LatestPriceResponse>();
        
        // TRY kuru yoksa hesaplama yapılamaz
        if (!rates.TryGetValue("TRY", out var tryRate) || tryRate <= 0) return results;

        var targets = new Dictionary<string, (string Currency, string PrettyName)>
        {
            { "USDTRY=X", ("USD", "Amerikan Doları") }, 
            { "EURTRY=X", ("EUR", "Euro") },
            { "GBPTRY=X", ("GBP", "İngiliz Sterlini") },
            { "CHFTRY=X", ("CHF", "İsviçre Frangı") },
            { "CADTRY=X", ("CAD", "Kanada Doları") },
            { "AUDTRY=X", ("AUD", "Avustralya Doları") },
            { "JPYTRY=X", ("JPY", "Japon Yeni") }
        };

        var asOfUtc = DateTimeOffset.UtcNow;
        bool isForexWeekend = asOfUtc.DayOfWeek == DayOfWeek.Saturday || 
                             (asOfUtc.DayOfWeek == DayOfWeek.Sunday && asOfUtc.Hour < 21);
        
        var validUntilUtc = isForexWeekend ? asOfUtc.AddDays(1) : asOfUtc.AddMinutes(60);

        if (!rates.ContainsKey("USD")) rates["USD"] = 1m;

        foreach (var kvp in targets)
        {
            if (rates.TryGetValue(kvp.Value.Currency, out var baseRate) && baseRate > 0)
            {
                var globalRate = tryRate / baseRate; 
                
                // 12 Parametreli DTO'yu eksiksiz dolduruyoruz (Hata vermemesi için)
                results.Add(new LatestPriceResponse(
                    ProductCode: kvp.Key,
                    Name: kvp.Value.PrettyName,
                    Type: "CURRENCY",
                    Bid: globalRate,
                    Ask: globalRate,
                    LastPrice: globalRate, // Global kurda işlem fiyatı = orta kur kabul edilir
                    ChangeRate: 0m,
                    FormattedBid: globalRate.ToString("N4", CultureInfo.InvariantCulture),
                    FormattedAsk: globalRate.ToString("N4", CultureInfo.InvariantCulture),
                    AsOfUtc: asOfUtc,
                    ValidUntilUtc: validUntilUtc,
                    Source: "FreeCurrencyAPI"
                ));
            }
        }
        return results;
    }
}
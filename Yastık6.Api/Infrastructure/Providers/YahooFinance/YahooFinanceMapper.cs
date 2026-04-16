using System.Globalization;
using YahooFinanceApi;
using Yastık6.Api.Application.Features.Prices;

namespace Yastık6.Api.Infrastructure.Providers.YahooFinance;

public static class YahooFinanceMapper
{
    // Sadece varlık kodları ve türleri kaldı
    private static readonly Dictionary<string, string> YahooRegistry = new()
    {
        { "USDTRY=X", "CURRENCY" },
        { "EURTRY=X", "CURRENCY" },
        { "GBPTRY=X", "CURRENCY" },
        { "CHFTRY=X", "CURRENCY" },
        { "CADTRY=X", "CURRENCY" },
        { "AUDTRY=X", "CURRENCY" },
        { "JPYTRY=X", "CURRENCY" },
        { "BTC-USD", "CRYPTO" },
        { "ETH-USD", "CRYPTO" }
    };

    public static LatestPriceResponse? MapToResponse(string symbolCode, Security security, string symbolType, string providerName)
    {
        decimal marketPrice = (decimal)security.RegularMarketPrice;
        if (marketPrice == 0) return null;

        decimal askPrice = security.Fields.ContainsKey("Ask") ? (decimal)security.Ask : marketPrice;
        decimal bidPrice = security.Fields.ContainsKey("Bid") ? (decimal)security.Bid : marketPrice;

        string finalType;

        if (YahooRegistry.TryGetValue(symbolCode, out var registryType))
        {
            finalType = registryType;
        }
        else
        {
            finalType = symbolType == "BIST" ? "STOCK" : symbolType;
        }

        if (symbolCode == "XAU=F") return null;

        string format = finalType == "CURRENCY" ? "N4" : "N2";
        var asOfUtc = DateTimeOffset.UtcNow;
        
        // --- AKILLI MOTOR: Geçerlilik Tarihi (ValidUntilUtc) Hesaplama ---
        DateTimeOffset validUntilUtc;
        var nowUtc = DateTimeOffset.UtcNow;
        var nowTr = nowUtc.AddHours(3); // Borsa saatleri için Türkiye Saati

        if (finalType == "CRYPTO")
        {
            // Kripto 7/24 aktiftir
            validUntilUtc = nowUtc.AddMinutes(5);
        }
        else if (finalType == "CURRENCY")
        {
            // Global Forex: Cumartesi tam gün, Pazar 21:00 UTC'ye kadar kapalıdır.
            bool isForexWeekend = nowUtc.DayOfWeek == DayOfWeek.Saturday || 
                                  (nowUtc.DayOfWeek == DayOfWeek.Sunday && nowUtc.Hour < 21);
            
            validUntilUtc = isForexWeekend 
                ? nowUtc.AddDays(1) // Hafta sonu boyunca uygulama uyarı vermesin
                : nowUtc.AddMinutes(5); // Hafta içi 5 dakika tolerans
        }
        else // STOCK (BIST)
        {
            bool isWeekendTr = nowTr.DayOfWeek == DayOfWeek.Saturday || nowTr.DayOfWeek == DayOfWeek.Sunday;
            // BIST kabaca 09:00 - 18:15 arası açıktır.
            if (isWeekendTr || nowTr.Hour < 9 || nowTr.Hour > 18)
            {
                validUntilUtc = nowUtc.AddDays(1); // Borsa kapalı, uyarıyı engelle
            }
            else
            {
                validUntilUtc = nowUtc.AddMinutes(25); // 15 dk çekim aralığı + 10 dk tolerans
            }
        }

        return new LatestPriceResponse(
            ProductCode: symbolCode,
            Name: symbolCode, // Frontend'de AssetCatalog ile Türkçeleştirilecek
            Type: finalType,
            Bid: bidPrice,
            Ask: askPrice,
            ChangeRate: 0m,
            FormattedBid: bidPrice.ToString(format, CultureInfo.InvariantCulture),
            FormattedAsk: askPrice.ToString(format, CultureInfo.InvariantCulture),
            AsOfUtc: asOfUtc,
            ValidUntilUtc: validUntilUtc,
            Source: providerName
        );
    }
}
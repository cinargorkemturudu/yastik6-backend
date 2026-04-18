using System.Globalization;
using YahooFinanceApi;
using Yastık6.Api.Application.Features.Prices;

namespace Yastık6.Api.Infrastructure.Providers.YahooFinance;

public static class YahooFinanceMapper
{
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

    // [YENİ EKLENDİ]: Flutter'daki zeki isim düzenleyiciyi Backend'e aldık!
    private static string GetPrettyName(string code)
    {
        var overrides = new Dictionary<string, string>
        {
            { "USDTRY=X", "Amerikan Doları" },
            { "EURTRY=X", "Euro" },
            { "GBPTRY=X", "İngiliz Sterlini" },
            { "CHFTRY=X", "İsviçre Frangı" },
            { "CADTRY=X", "Kanada Doları" },
            { "AUDTRY=X", "Avustralya Doları" },
            { "JPYTRY=X", "Japon Yeni" },
            { "BTC-USD", "Bitcoin" },
            { "ETH-USD", "Ethereum" }
        };

        if (overrides.TryGetValue(code, out var pretty)) return pretty;

        string cleanCode = code.Replace("=X", "").Replace(".IS", "");

        if (cleanCode.Length == 6 && cleanCode.EndsWith("TRY"))
        {
            cleanCode = cleanCode.Substring(0, 3);
        }

        // TitleCase işlemi ("GARANTİ BANKASI" -> "Garanti Bankası")
        var textInfo = new CultureInfo("tr-TR").TextInfo;
        return textInfo.ToTitleCase(cleanCode.Replace("_", " ").Replace("-", " ").ToLower());
    }

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
        
        DateTimeOffset validUntilUtc;
        var nowUtc = DateTimeOffset.UtcNow;
        var nowTr = nowUtc.AddHours(3);

        if (finalType == "CRYPTO")
        {
            validUntilUtc = nowUtc.AddMinutes(5);
        }
        else if (finalType == "CURRENCY")
        {
            bool isForexWeekend = nowUtc.DayOfWeek == DayOfWeek.Saturday || 
                                  (nowUtc.DayOfWeek == DayOfWeek.Sunday && nowUtc.Hour < 21);
            
            validUntilUtc = isForexWeekend 
                ? nowUtc.AddDays(1) 
                : nowUtc.AddMinutes(5);
        }
        else // STOCK (BIST)
        {
            bool isWeekendTr = nowTr.DayOfWeek == DayOfWeek.Saturday || nowTr.DayOfWeek == DayOfWeek.Sunday;
            if (isWeekendTr || nowTr.Hour < 9 || nowTr.Hour > 18)
            {
                validUntilUtc = nowUtc.AddDays(1);
            }
            else
            {
                validUntilUtc = nowUtc.AddMinutes(25);
            }
        }

        return new LatestPriceResponse(
            ProductCode: symbolCode,
            Name: GetPrettyName(symbolCode), // [KRİTİK]: Artık "THYAO.IS" yerine "Thyao" dönüyor.
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
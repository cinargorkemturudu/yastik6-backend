using System.Globalization;
using Yastık6.Api.Application.Features.Prices;

namespace Yastık6.Api.Infrastructure.Providers.Harem;

public static class HaremDataMapper
{
    private static readonly CultureInfo TrCulture = new("tr-TR");

    
    private static readonly Dictionary<string, (string Type, string TargetCode)> AllowedAssets = new()
    {
        { "GRAM ALTIN", ("METAL", "GRAM_ALTIN") },
        { "Has Altın", ("METAL", "HAS_ALTIN") },
        { "GÜMÜŞ TL", ("METAL", "GUMUS_TL") },     
        { "22 AYAR", ("METAL", "AYAR_22") },       
        { "14 AYAR", ("METAL", "AYAR_14") },      
        { "YENİ ÇEYREK", ("METAL", "YENI_CEYREK") }, 
        { "ESKİ ÇEYREK", ("METAL", "ESKI_CEYREK") }, 
        { "YENİ YARIM", ("METAL", "YENI_YARIM") },   
        { "ESKİ YARIM", ("METAL", "ESKI_YARIM") },  
        { "YENİ TAM", ("METAL", "YENI_TAM") },     
        { "ESKİ TAM", ("METAL", "ESKI_TAM") },      
        { "YENİ ATA", ("METAL", "YENI_ATA") },       
        { "ESKİ ATA", ("METAL", "ESKI_ATA") },       
        { "YENİ GREMSE", ("METAL", "YENI_GREMSE") }, 
        { "ESKİ GREMSE", ("METAL", "ESKI_GREMSE") }, 
        { "ONS", ("INDICATOR", "ONS") },
        { "GÜMÜŞ ONS", ("INDICATOR", "GUMUS_ONS") }  
    };

    public static LatestPriceResponse? MapToResponse(HaremClient.HaremItemRaw item)
    {
        if (!AllowedAssets.TryGetValue(item.Key, out var assetInfo))
        {
            return null;
        }

        if (!TryParseTurkishMoney(item.Buy, out var bid) || !TryParseTurkishMoney(item.Sell, out var ask))
        {
            return null;
        }

        // Doğrudan bizim belirlediğimiz %100 güvenli kodu alıyoruz
        var discoveryCode = assetInfo.TargetCode;
        var assetType = assetInfo.Type;
        
        string formattedBid = bid.ToString("N2", CultureInfo.InvariantCulture);
        string formattedAsk = ask.ToString("N2", CultureInfo.InvariantCulture);

        var asOfUtc = ParseTurkishDate(item.LastUpdate);
        
        var nowTr = DateTimeOffset.UtcNow.AddHours(3); 
        bool isWeekendTr = nowTr.DayOfWeek == DayOfWeek.Saturday || nowTr.DayOfWeek == DayOfWeek.Sunday;
        
        DateTimeOffset validUntilUtc;
        if (!isWeekendTr && nowTr.Hour >= 8 && nowTr.Hour < 18)
        {
            validUntilUtc = asOfUtc.AddMinutes(2);
        }
        else
        {
            validUntilUtc = asOfUtc.AddMinutes(35);
        }

        return new LatestPriceResponse(
            ProductCode: discoveryCode,
            Name: discoveryCode, 
            Type: assetType,
            Bid: bid,
            Ask: ask,
            ChangeRate: 0m, 
            FormattedBid: formattedBid,
            FormattedAsk: formattedAsk,
            AsOfUtc: asOfUtc,
            ValidUntilUtc: validUntilUtc,
            Source: "Harem"
        );
    }

    private static bool TryParseTurkishMoney(string? raw, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var sanitized = raw.Replace(".", "");
        return decimal.TryParse(sanitized, NumberStyles.Number, TrCulture, out value);
    }

    private static DateTimeOffset ParseTurkishDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DateTimeOffset.UtcNow;
        if (DateTime.TryParseExact(raw, "dd.MM.yyyy HH:mm:ss", TrCulture, DateTimeStyles.AssumeLocal, out var dt))
        {
            return dt.ToUniversalTime();
        }
        return DateTimeOffset.UtcNow;
    }
}
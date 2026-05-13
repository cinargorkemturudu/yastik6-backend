using System.Globalization;
using Yastık6.Api.Application.Features.Prices;

namespace Yastık6.Api.Infrastructure.Providers.Harem;

public static class HaremDataMapper
{
    private static readonly CultureInfo TrCulture = new("tr-TR");

    // [DÜZELTME 1]: Sözlük artık büyük/küçük harf duyarsız (Case-Insensitive) hale getirildi.
    private static readonly Dictionary<string, (string Type, string TargetCode, string PrettyName)> AllowedAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        { "GRAM ALTIN", ("METAL", "GRAM_ALTIN", "Gram Altın") },
        { "Has Altın", ("METAL", "HAS_ALTIN", "Has Altın") },
        { "GÜMÜŞ TL", ("METAL", "GUMUS_TL", "Gümüş (TL)") },     
        { "22 AYAR", ("METAL", "AYAR_22", "22 Ayar Altın") },       
        { "14 AYAR", ("METAL", "AYAR_14", "14 Ayar Altın") },      
        { "YENİ ÇEYREK", ("METAL", "YENI_CEYREK", "Yeni Çeyrek") }, 
        { "ESKİ ÇEYREK", ("METAL", "ESKI_CEYREK", "Eski Çeyrek") }, 
        { "YENİ YARIM", ("METAL", "YENI_YARIM", "Yeni Yarım") },   
        { "ESKİ YARIM", ("METAL", "ESKI_YARIM", "Eski Yarım") },  
        { "YENİ TAM", ("METAL", "YENI_TAM", "Yeni Tam") },     
        { "ESKİ TAM", ("METAL", "ESKI_TAM", "Eski Tam") },      
        { "YENİ ATA", ("METAL", "YENI_ATA", "Yeni Ata") },       
        { "ESKİ ATA", ("METAL", "ESKI_ATA", "Eski Ata") },       
        { "YENİ GREMSE", ("METAL", "YENI_GREMSE", "Yeni Gremse") }, 
        { "ESKİ GREMSE", ("METAL", "ESKI_GREMSE", "Eski Gremse") }, 
        { "ONS", ("INDICATOR", "ONS", "Ons Altın ($)") },
        { "GÜMÜŞ ONS", ("INDICATOR", "GUMUS_ONS", "Gümüş Ons ($)") }  
    };

    public static LatestPriceResponse? MapToResponse(HaremClient.HaremItemRaw item)
    {
        // Gelen key değerinin etrafındaki olası boşlukları Trim() ile temizliyoruz.
        if (string.IsNullOrWhiteSpace(item.Key) || !AllowedAssets.TryGetValue(item.Key.Trim(), out var assetInfo))
        {
            return null;
        }

        if (!TryParseTurkishMoney(item.Buy, out var bid) || !TryParseTurkishMoney(item.Sell, out var ask))
        {
            return null;
        }

        var discoveryCode = assetInfo.TargetCode;
        var assetType = assetInfo.Type;
        var prettyName = assetInfo.PrettyName; 
        
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
            Name: prettyName, 
            Type: assetType,
            Bid: bid,
            Ask: ask,
            LastPrice: null,
            ChangeRate: 0m, 
            FormattedBid: formattedBid,
            FormattedAsk: formattedAsk,
            AsOfUtc: asOfUtc,
            ValidUntilUtc: validUntilUtc,
            Source: "Harem"
        );
    }

    // [DÜZELTME 2]: Akıllı Fiyat Ayrıştırıcı
    private static bool TryParseTurkishMoney(string? raw, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        
        raw = raw.Trim();

        // Eğer Harem API uluslararası formata (örn: 3150.50) geçmişse:
        // İçinde virgül YOKSA ve nokta VARSA, noktanın ondalık ayracı olduğunu anla.
        if (!raw.Contains(',') && raw.Contains('.'))
        {
             return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        // Eğer geleneksel Türkçe formatındaysa (örn: 3.150,50 veya sadece 3150,50):
        var sanitized = raw.Replace(".", "");
        return decimal.TryParse(sanitized, NumberStyles.Any, TrCulture, out value);
    }

    private static DateTimeOffset ParseTurkishDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DateTimeOffset.UtcNow;
        if (DateTime.TryParseExact(raw.Trim(), "dd.MM.yyyy HH:mm:ss", TrCulture, DateTimeStyles.AssumeLocal, out var dt))
        {
            return dt.ToUniversalTime();
        }
        return DateTimeOffset.UtcNow;
    }
}
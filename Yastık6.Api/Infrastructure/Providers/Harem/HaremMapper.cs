using System.Globalization;
using Yastık6.Api.Application.Features.Prices;

namespace Yastık6.Api.Infrastructure.Providers.Harem;

public static class HaremDataMapper
{
    private static readonly CultureInfo TrCulture = new("tr-TR");

    // İzin verilen varlıklar ve sistemdeki Tipleri
    private static readonly Dictionary<string, string> AllowedAssets = new()
    {
        { "GRAM ALTIN", "METAL" },
        { "Has Altın", "METAL" },
        { "GÜMÜŞ TL", "METAL" },
        { "22 AYAR", "METAL" },
        { "14 AYAR", "METAL" },
        { "YENİ ÇEYREK", "METAL" },
        { "ESKİ ÇEYREK", "METAL" },
        { "YENİ YARIM", "METAL" },
        { "ESKİ YARIM", "METAL" },
        { "YENİ TAM", "METAL" },
        { "ESKİ TAM", "METAL" },
        { "YENİ ATA", "METAL" },
        { "ESKİ ATA", "METAL" },
        { "YENİ GREMSE", "METAL" },
        { "ESKİ GREMSE", "METAL" },
        { "ONS", "INDICATOR" },
        { "GÜMÜŞ ONS", "INDICATOR" }
    };

    public static LatestPriceResponse? MapToResponse(HaremClient.HaremItemRaw item)
    {
        if (!AllowedAssets.TryGetValue(item.Key, out var assetType))
        {
            return null;
        }

        if (!TryParseTurkishMoney(item.Buy, out var bid) || !TryParseTurkishMoney(item.Sell, out var ask))
        {
            return null;
        }

        var discoveryCode = SanitizeAssetCode(item.Key);
        
        string formattedBid = bid.ToString("N2", CultureInfo.InvariantCulture);
        string formattedAsk = ask.ToString("N2", CultureInfo.InvariantCulture);

        var asOfUtc = ParseTurkishDate(item.LastUpdate);
        
        // --- AKILLI MOTOR: Geçerlilik Tarihi (ValidUntilUtc) Hesaplama ---
        var nowTr = DateTimeOffset.UtcNow.AddHours(3); // Türkiye Saati
        bool isWeekendTr = nowTr.DayOfWeek == DayOfWeek.Saturday || nowTr.DayOfWeek == DayOfWeek.Sunday;
        
        DateTimeOffset validUntilUtc;
        if (!isWeekendTr && nowTr.Hour >= 8 && nowTr.Hour < 18)
        {
            validUntilUtc = asOfUtc.AddMinutes(2); // Mesai saatlerinde 2 dk lisans
        }
        else
        {
            validUntilUtc = asOfUtc.AddMinutes(35); // Gece ve hafta sonu 35 dk lisans (Kota koruması)
        }

        return new LatestPriceResponse(
            ProductCode: discoveryCode,
            Name: discoveryCode, // Frontend'de AssetCatalog ile Türkçeleştirilecek
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

    private static string SanitizeAssetCode(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return "UNKNOWN";
        return rawKey.Trim().ToUpperInvariant().Replace(" ", "_").Replace("-", "_");
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
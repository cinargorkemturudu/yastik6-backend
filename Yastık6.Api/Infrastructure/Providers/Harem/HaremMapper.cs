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
        { "22 AYAR", ("METAL", "AYAR_22", "22 Ayar Altın (Gram)") },
        { "14 AYAR", ("METAL", "AYAR_14", "14 Ayar Altın (Gram)") },
        { "YENİ ÇEYREK", ("METAL", "YENI_CEYREK", "Çeyrek Altın (Yeni)") },
        { "ESKİ ÇEYREK", ("METAL", "ESKI_CEYREK", "Çeyrek Altın (Eski)") },
        { "YENİ YARIM", ("METAL", "YENI_YARIM", "Yarım Altın (Yeni)") },
        { "ESKİ YARIM", ("METAL", "ESKI_YARIM", "Yarım Altın (Eski)") },
        { "YENİ TAM", ("METAL", "YENI_TAM", "Tam Altın (Yeni)") },
        { "ESKİ TAM", ("METAL", "ESKI_TAM", "Tam Altın (Eski)") },
        { "YENİ ATA", ("METAL", "YENI_ATA", "Ata Altın (Yeni)") },
        { "ESKİ ATA", ("METAL", "ESKI_ATA", "Ata Altın (Eski)") },
        { "YENİ GREMSE", ("METAL", "YENI_GREMSE", "Gremse Altın (Yeni)") },
        { "ESKİ GREMSE", ("METAL", "ESKI_GREMSE", "Gremse Altın (Eski)") },
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

        // Vendorun kendi last_update alanına güvenmiyoruz: fiyat değişse bile bu alan
        // her zaman aynı hızda ilerlemeyebiliyor, bu da bizim ValidUntilUtc penceremizin
        // gerçekte taze veriyi "bayat" gibi göstermesine yol açıyordu. Diğer tüm
        // provider'lar gibi kendi fetch anımızı kullanıyoruz.
        var asOfUtc = DateTimeOffset.UtcNow;
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
    // [DÜZELTME 2]: Akıllı Fiyat Ayrıştırıcı
    // [DÜZELTME 2]: Akıllı Fiyat Ayrıştırıcı
    private static bool TryParseTurkishMoney(string? raw, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        raw = raw.Trim();

        int lastComma = raw.LastIndexOf(',');
        int lastDot = raw.LastIndexOf('.');

        // Durum 1: Hem nokta hem virgül var (Örn: 2.500,50 veya 2,500.50)
        if (lastComma > -1 && lastDot > -1)
        {
            if (lastDot > lastComma)
            {
                // Format: 2,500.50 (Uluslararası) -> Virgülü sil, Invariant ile oku
                var sanitized = raw.Replace(",", "");
                return decimal.TryParse(sanitized, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
            }
            else
            {
                // Format: 2.500,50 (Türkçe) -> Noktayı sil, TrCulture ile oku
                var sanitized = raw.Replace(".", "");
                return decimal.TryParse(sanitized, NumberStyles.Any, TrCulture, out value);
            }
        }
        // Durum 2: Sadece virgül var (Örn: 2500,50) -> Virgül kuruştur, TrCulture anlar
        else if (lastComma > -1)
        {
            return decimal.TryParse(raw, NumberStyles.Any, TrCulture, out value);
        }
        // Durum 3: Sadece nokta var (Örn: 2.500) -> HATA BURADAYDI!
        // Türk sisteminde bu ondalık değil binliktir (2.5 Lira değil, 2500 Liradır). Noktayı yokedip okuyoruz.
        else if (lastDot > -1)
        {
            var sanitized = raw.Replace(".", "");
            return decimal.TryParse(sanitized, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }
        // Durum 4: Hiçbiri yok (Örn: 2500) -> Dümdüz oku
        else
        {
            return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }
    }
}

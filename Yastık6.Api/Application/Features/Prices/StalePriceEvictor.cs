namespace Yastık6.Api.Application.Features.Prices;

/// <summary>
/// Snapshot'a yeni fiyatlar birleştirilirken eski kayıtlar silinmediği için, sağlayıcının artık
/// göndermediği varlıklar (örn: delist olan coinler) son fiyatlarıyla sonsuza dek snapshot'ta kalıyordu.
/// PriceProcessor'daki "dead asset filter" sadece yeni gelen veriye bakar; bu sınıf snapshot'taki eski kayıtları temizler.
/// </summary>
public static class StalePriceEvictor
{
    /// <summary>
    /// Piyasa açıkken kaydedilmiş ve geçerlilik süresi <paramref name="maxAge"/>'den uzun süre önce dolmuş
    /// kayıtları sözlükten siler, silinenleri döner. Piyasa kapalıyken kaydedilenlere dokunmaz.
    /// </summary>
    public static IReadOnlyList<LatestPriceResponse> Evict(
        IDictionary<string, LatestPriceResponse> items,
        DateTimeOffset nowUtc,
        TimeSpan maxAge)
    {
        var stale = items.Values
            .Where(p => p.IsMarketOpen && p.ValidUntilUtc < nowUtc - maxAge)
            .ToList();

        foreach (var price in stale)
        {
            items.Remove(price.ProductCode);
        }

        return stale;
    }
}

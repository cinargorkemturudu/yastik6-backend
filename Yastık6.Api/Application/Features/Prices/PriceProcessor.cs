using Microsoft.Extensions.Logging;
using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Infrastructure.Caching;

namespace Yastık6.Api.Application.Features.Prices;

/// <summary>
/// Gelen ham fiyatları işleyen, çeviren ve kalkanlardan geçiren ana iş hattı (Pipeline).
/// </summary>
public sealed class PriceProcessor : IPriceProcessor
{
    private readonly IPriceSnapshotStore _snapshotStore;
    private readonly IPreviousCloseStore _previousCloseStore;
    private readonly PriceShieldValidator _shieldValidator;
    private readonly ILogger<PriceProcessor> _logger;

    public PriceProcessor(
        IPriceSnapshotStore snapshotStore,
        IPreviousCloseStore previousCloseStore,
        PriceShieldValidator shieldValidator,
        ILogger<PriceProcessor> logger)
    {
        _snapshotStore = snapshotStore;
        _previousCloseStore = previousCloseStore;
        _shieldValidator = shieldValidator;
        _logger = logger;
    }

    public async Task<List<LatestPriceResponse>> ProcessPricesAsync(
        List<LatestPriceResponse> rawPrices, 
        string providerName, 
        CancellationToken ct = default)
    {
        var processedPrices = new List<LatestPriceResponse>();

        // BFF Stratejisi için Dolar/TL kurunu önbellekten (Cache) güvenle alıyoruz
        var currentUsdPrice = rawPrices.FirstOrDefault(p => p.ProductCode == "USDTRY=X")?.Ask
                              ?? _snapshotStore.Current?.Items.FirstOrDefault(p => p.ProductCode == "USDTRY=X")?.Ask 
                              ?? 0m;

        // Kalkan doğrulamalarında geçmiş durumla kıyaslama yapabilmek için mevcut RAM önbelleğini çekiyoruz
        var currentSnapshot = _snapshotStore.Current?.Items.ToDictionary(p => p.ProductCode)
                              ?? new Dictionary<string, LatestPriceResponse>();

        foreach (var price in rawPrices)
        {
            var finalPrice = price;

            // 1. ADIM (BFF STRATEJİSİ): Kripto varlıkları en başta, işlem yapmadan Türk Lirasına çeviriyoruz
            if (finalPrice.Type == "CRYPTO")
            {
                // Race Condition: Sistem yeni açıldıysa ve kur henüz gelmediyse yanlış çevrim yapmamak için ertele
                if (currentUsdPrice <= 0)
                {
                    _logger.LogWarning("[BFF Pipeline] USD/TRY oranı henüz hazır olmadığı için {Code} çevirimi ertelendi.", finalPrice.ProductCode);
                    continue; 
                }

                // C#'ın Record tipi özelliği (with) ile orijinal nesneyi bozmadan yeni değerlerle kopyalıyoruz
                finalPrice = finalPrice with 
                {
                    Bid = finalPrice.Bid * currentUsdPrice,
                    Ask = finalPrice.Ask * currentUsdPrice,
                    LastPrice = (finalPrice.LastPrice ?? 0m) * currentUsdPrice,
                    IsConvertedToTry = true
                };
            }

            // 2. ADIM (MATEMATİK): Artık TL olduğundan emin olduğumuz fiyata göre değişim oranını hesaplıyoruz
            var previousClose = _previousCloseStore.GetPreviousClose(finalPrice.ProductCode);
            decimal changeRate = 0m;

            if (previousClose.HasValue && previousClose.Value > 0)
            {
                // (Güncel - Eski) / Eski * 100
                changeRate = ((finalPrice.Ask - previousClose.Value) / previousClose.Value) * 100m;
            }
            else
            {
                // Eğer dünkü kapanış DB'de yoksa, sağlayıcıdan (örn: Yahoo) hazır gelen oranı kullan
                changeRate = finalPrice.ChangeRate;
            }

            // Hesaplanmış değişim oranını virgülden sonra 2 hane kalacak şekilde fiyat nesnemize işliyoruz
            finalPrice = finalPrice with { ChangeRate = Math.Round(changeRate, 2) };

            // 3. ADIM (KALKANLAR): Oluşturduğumuz kalkan servisine (Shield) gönderip doğrulamadan geçiriyoruz
            currentSnapshot.TryGetValue(finalPrice.ProductCode, out var existingPrice);
            
            if (await _shieldValidator.ValidatePriceAsync(finalPrice, existingPrice, ct))
            {
                // Fiyat temiz, onaylananlar listesine ekle
                processedPrices.Add(finalPrice);
            }
        }

        return processedPrices;
    }
}
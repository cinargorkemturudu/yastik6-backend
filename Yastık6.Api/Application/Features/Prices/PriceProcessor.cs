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
        bool isMarketOpen,
        CancellationToken ct = default)
    {
        var processedPrices = new List<LatestPriceResponse>();
        var currentSnapshot = _snapshotStore.Current?.Items.ToDictionary(p => p.ProductCode)
                              ?? new Dictionary<string, LatestPriceResponse>();

        // 1. PRE-FLIGHT SHIELD: USDTRY oranını kullanmadan ÖNCE Kalkan'dan geçiriyoruz
        var rawUsd = rawPrices.FirstOrDefault(p => p.ProductCode == "USDTRY=X");
        decimal currentUsdPrice = 0m;

        if (rawUsd != null)
        {
            currentSnapshot.TryGetValue("USDTRY=X", out var existingUsd);

            // Eğer USDTRY fiyatı geçerliyse kuru onayla, değilse pas geç
            if (await _shieldValidator.ValidatePriceAsync(rawUsd, existingUsd, ct))
            {
                currentUsdPrice = rawUsd.Ask;
            }
        }

        // Eğer güncel kur gelmediyse veya kalkan reddettiyse, RAM'deki son güvenli kuru kullan
        if (currentUsdPrice <= 0)
        {
            currentUsdPrice = currentSnapshot.TryGetValue("USDTRY=X", out var cachedUsd) ? cachedUsd.Ask : 0m;
        }

        foreach (var price in rawPrices)
        {
            // USDTRY'yi yukarıda zaten kalkanla doğruladık, listeye ekleyip geçiyoruz
            if (price.ProductCode == "USDTRY=X")
            {
                if (currentUsdPrice == price.Ask)
                {
                    processedPrices.Add(price with
                    {
                        IsMarketOpen = isMarketOpen,
                        AsOfUtc = price.AsOfUtc.ToUniversalTime(),
                        ValidUntilUtc = price.ValidUntilUtc.ToUniversalTime()
                    });
                }
                continue;
            }

            var finalPrice = price;

            // 2. CPU PROTECTION (FAIL FAST): Çöp veri için hafıza ayırmadan önce reddet
            if (finalPrice.Ask <= 0 || finalPrice.Bid <= 0)
            {
                // Sadece alert atsın diye kalkana yolluyoruz
                await _shieldValidator.ValidatePriceAsync(finalPrice, null, ct);
                continue;
            }

            // 3. DEAD ASSET FILTER: Piyasa açıkken süresi dolmuş ölü verileri (örn: delist olan coinleri) filtrele
            if (isMarketOpen && finalPrice.ValidUntilUtc < DateTimeOffset.UtcNow)
            {
                _logger.LogDebug("[BFF Pipeline] Expired/Dead asset filtered: {ProductCode}", finalPrice.ProductCode);
                continue;
            }

            // 4. APPLES-TO-APPLES: Kriptoları doğrulanmış kur ile TL'ye çevir
            if (finalPrice.Type == "CRYPTO")
            {
                if (currentUsdPrice <= 0)
                {
                    _logger.LogWarning("[BFF Pipeline] USD/TRY rate not ready yet, deferring conversion of {Code}.", finalPrice.ProductCode);
                    continue;
                }

                finalPrice = finalPrice with
                {
                    Bid = finalPrice.Bid * currentUsdPrice,
                    Ask = finalPrice.Ask * currentUsdPrice,
                    LastPrice = (finalPrice.LastPrice ?? 0m) * currentUsdPrice,
                    IsConvertedToTry = true
                };
            }

            // 5. FLASH CRASH SHIELD: TL ile TL kıyaslaması yapılarak Kalkan devreye sokulur
            currentSnapshot.TryGetValue(finalPrice.ProductCode, out var existingPrice);
            if (!await _shieldValidator.ValidatePriceAsync(finalPrice, existingPrice, ct))
            {
                continue;
            }

            // 6. AĞIR MATEMATİK: Sadece Kalkanı geçen tertemiz veriler için çalıştır
            var previousClose = _previousCloseStore.GetPreviousClose(finalPrice.ProductCode);
            decimal changeRate = previousClose.HasValue && previousClose.Value > 0
                ? ((finalPrice.Ask - previousClose.Value) / previousClose.Value) * 100m
                : finalPrice.ChangeRate;

            finalPrice = finalPrice with
            {
                ChangeRate = Math.Round(changeRate, 2),
                AsOfUtc = finalPrice.AsOfUtc.ToUniversalTime(),
                ValidUntilUtc = finalPrice.ValidUntilUtc.ToUniversalTime(),
                IsMarketOpen = isMarketOpen
            };

            processedPrices.Add(finalPrice);
        }

        return processedPrices;
    }
}

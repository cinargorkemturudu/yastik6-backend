using Yastık6.Api.Infrastructure.Alerts;

namespace Yastık6.Api.Application.Features.Prices;

/// <summary>
/// Sisteme girmeye çalışan fiyatları anormalliklere karşı denetleyen Güvenlik Kalkanı.
/// </summary>
public sealed class PriceShieldValidator
{
    private readonly ILogger<PriceShieldValidator> _logger;
    private readonly IAlertService _alertService;
    private readonly AlertTracker _tracker; // [YENİ]: Akıllı Defteri Kalkana Taktık

    public PriceShieldValidator(ILogger<PriceShieldValidator> logger, IAlertService alertService, AlertTracker tracker)
    {
        _logger = logger;
        _alertService = alertService;
        _tracker = tracker;
    }

    public async Task<bool> ValidatePriceAsync(
        LatestPriceResponse newPrice,
        LatestPriceResponse? existingPrice,
        CancellationToken ct)
    {
        // --- SHIELD 1: SIFIR VEYA NEGATİF FİYAT KALKANI ---
        if (newPrice.Ask <= 0 || newPrice.Bid <= 0)
        {
            var cacheKey = $"ZeroPrice_{newPrice.ProductCode}";

            // [ZEKA EKLENDİ]: Deftere soruyoruz, "Bunu 1 saat içinde hiç raporladık mı?"
            if (_tracker.ShouldSendAlert(cacheKey))
            {
                _logger.LogWarning("[SHIELD REJECT] {Code} returned a zero/negative price! Source: {Source}, Price: {Price}",
                    newPrice.ProductCode, newPrice.Source, newPrice.Ask);

                await _alertService.SendAlertAsync(
                    $"⚠️ *ZERO PRICE REJECTED*\nSymbol: `{newPrice.ProductCode}`\nSource: `{newPrice.Source}`\nPrice: `{newPrice.Ask}`\n_Sistem mevcut geçerli fiyatı korudu._",
                    cacheKey,
                    ct);
            }

            return false;
        }

        // --- SHIELD 2: FLASH CRASH (ANİ ÇÖKÜŞ) KALKANI ---
        if (existingPrice != null && existingPrice.Ask > 0)
        {
            var instantChange = Math.Abs((newPrice.Ask - existingPrice.Ask) / existingPrice.Ask * 100m);

            if (instantChange > 40m)
            {
                var cacheKey = $"FlashCrash_{newPrice.ProductCode}";

                // [ZEKA EKLENDİ]: Çöküş raporunu da 1 saatlik susturucuya bağladık
                if (_tracker.ShouldSendAlert(cacheKey))
                {
                    _logger.LogCritical("[SHIELD FLASH CRASH] Abnormal deviation detected for {Code}! Old: {Old}, New: {New}, Deviation: %{Rate}",
                        newPrice.ProductCode, existingPrice.Ask, newPrice.Ask, instantChange);

                    await _alertService.SendAlertAsync(
                        $"🚨 *FLASH CRASH DETECTED*\nSymbol: `{newPrice.ProductCode}`\nOld: `{existingPrice.Ask}`\nNew: `{newPrice.Ask}`\nDeviation: `%{instantChange:N2}`\nSource: `{newPrice.Source}`",
                        cacheKey,
                        ct);
                }

                return false;
            }
        }

        return true;
    }
}

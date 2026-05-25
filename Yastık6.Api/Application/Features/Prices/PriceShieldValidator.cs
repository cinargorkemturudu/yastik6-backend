using Yastık6.Api.Infrastructure.Alerts;

namespace Yastık6.Api.Application.Features.Prices;

/// <summary>
/// Sisteme girmeye çalışan fiyatları anormalliklere karşı denetleyen Güvenlik Kalkanı.
/// </summary>
public sealed class PriceShieldValidator
{
    private readonly ILogger<PriceShieldValidator> _logger;
    private readonly IAlertService _alertService;

    public PriceShieldValidator(ILogger<PriceShieldValidator> logger, IAlertService alertService)
    {
        _logger = logger;
        _alertService = alertService;
    }

    public async Task<bool> ValidatePriceAsync(
        LatestPriceResponse newPrice, 
        LatestPriceResponse? existingPrice, 
        CancellationToken ct)
    {
        // --- SHIELD 1: SIFIR VEYA NEGATİF FİYAT KALKANI ---
        if (newPrice.Ask <= 0 || newPrice.Bid <= 0)
        {
            _logger.LogWarning("[SHIELD REJECT] {Code} sıfır/negatif fiyat döndürdü! Kaynak: {Source}, Fiyat: {Price}", 
                newPrice.ProductCode, newPrice.Source, newPrice.Ask);
            
            // [DÜZELTİLDİ]: Sekretere "Hangi ürünün sıfır fiyatı" olduğunu parmak izi olarak verdik!
            await _alertService.SendAlertAsync(
                $"⚠️ *ZERO PRICE REJECTED*\nSymbol: `{newPrice.ProductCode}`\nSource: `{newPrice.Source}`\nPrice: `{newPrice.Ask}`\n_Sistem mevcut geçerli fiyatı korudu._", 
                $"ZeroPrice_{newPrice.ProductCode}", // <- EKLENEN PARMAK İZİ (Örn: ZeroPrice_GLD)
                ct);
            
            return false; 
        }

        // --- SHIELD 2: FLASH CRASH (ANİ ÇÖKÜŞ) KALKANI ---
        if (existingPrice != null && existingPrice.Ask > 0)
        {
            var instantChange = Math.Abs((newPrice.Ask - existingPrice.Ask) / existingPrice.Ask * 100m);
            
            if (instantChange > 40m)
            {
                _logger.LogCritical("[SHIELD FLASH CRASH] {Code} için anormal sapma detected! Eski: {Old}, Yeni: {New}, Sapma: %{Rate}", 
                    newPrice.ProductCode, existingPrice.Ask, newPrice.Ask, instantChange);
                
                // [DÜZELTİLDİ]: Sekretere "Hangi ürünün çöküşü" olduğunu parmak izi olarak verdik!
                await _alertService.SendAlertAsync(
                    $"🚨 *FLASH CRASH DETECTED*\nSymbol: `{newPrice.ProductCode}`\nOld: `{existingPrice.Ask}`\nNew: `{newPrice.Ask}`\nDeviation: `%{instantChange:N2}`\nSource: `{newPrice.Source}`", 
                    $"FlashCrash_{newPrice.ProductCode}", // <- EKLENEN PARMAK İZİ (Örn: FlashCrash_BTC)
                    ct);
                
                return false; 
            }
        }

        return true; 
    }
}
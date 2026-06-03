using Microsoft.EntityFrameworkCore;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Infrastructure.Persistence;
using Yastık6.Api.Infrastructure.Alerts; 

namespace Yastık6.Api.Infrastructure.BackgroundJobs;

public class HistoryCacheWarmer : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HistoryCacheWarmer> _logger;
    private readonly IAlertService _alertService; 

    public HistoryCacheWarmer(
        IServiceProvider serviceProvider, 
        ILogger<HistoryCacheWarmer> logger,
        IAlertService alertService)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _alertService = alertService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 Hayalet İşçi (Cache Warmer) uyandı ve göreve başladı.");
        
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        var dayTask = RunWarmerLoopAsync(PriceHistoryRange.Day, TimeSpan.FromMinutes(4), stoppingToken);
        var weekTask = RunWarmerLoopAsync(PriceHistoryRange.Week, TimeSpan.FromMinutes(25), stoppingToken);
        var monthTask = RunWarmerLoopAsync(PriceHistoryRange.Month, TimeSpan.FromMinutes(110), stoppingToken);
        var yearTask = RunWarmerLoopAsync(PriceHistoryRange.Year, TimeSpan.FromHours(23), stoppingToken);

        await Task.WhenAll(dayTask, weekTask, monthTask, yearTask);
    }

    private async Task RunWarmerLoopAsync(PriceHistoryRange range, TimeSpan refreshInterval, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var priceService = scope.ServiceProvider.GetRequiredService<IPriceService>();

                var activeSymbols = await dbContext.TrackedSymbols
                    .Select(x => x.Code)
                    .ToListAsync(stoppingToken);

                foreach (var code in activeSymbols)
                {
                    await priceService.GetHistoryAsync(code, range, stoppingToken);
                }
                _logger.LogInformation(
                    "✅ [{Range}] için tüm grafikler vitrine dizildi (Pre-warmed). Sonraki tazeleme: {RefreshIntervalMinutes} dk sonra.", 
                    range, 
                    refreshInterval.TotalMinutes);            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex, 
                    "❌ [{Range}] Hayalet İşçi çalışırken hata oluştu.", 
                    range);                
                // [DÜZELTİLDİ]: Sekretere "{Range}" bazlı özel çelik parmak izi verildi!
                await _alertService.SendAlertAsync(
                    $"🔥 *CACHE WARMER ÇÖKTÜ*\nGrafikler belleğe dizilemedi. Kullanıcı tarafında ({range}) grafiklerinin yüklenmesi ciddi yavaşlayacaktır!\nHata: `{ex.Message}`", 
                    $"CacheWarmer_{range}_{ex.GetType().Name}", // <- EKLENEN PARMAK İZİ (Örn: CacheWarmer_Week_SqlException)
                    stoppingToken);
            }

            await Task.Delay(refreshInterval, stoppingToken);
        }
    }
}
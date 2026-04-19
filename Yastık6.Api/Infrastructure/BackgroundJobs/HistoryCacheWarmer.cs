using Microsoft.EntityFrameworkCore;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Infrastructure.Persistence;

namespace Yastık6.Api.Infrastructure.BackgroundJobs;

// BackgroundService, .NET'in sana verdiği sonsuz döngüde arka planda çalışma yeteneğidir.
public class HistoryCacheWarmer : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HistoryCacheWarmer> _logger;

    // Sadece Fabrikayı (ServiceProvider) içeri alıyoruz. Veritabanını veya Servisi almıyoruz!
    public HistoryCacheWarmer(
        IServiceProvider serviceProvider, 
        ILogger<HistoryCacheWarmer> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 Hayalet İşçi (Cache Warmer) uyandı ve göreve başladı.");
        
        // Uygulama ilk açıldığında DB'nin ve diğer servislerin kendine gelmesi için 10 saniye avans veriyoruz
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        // KURAL 3: Paralel Evrenler. Her bir tarih aralığı kendi döngüsünde bağımsız dönecek.
        // Dikkat et: Yenilenme süreleri, PriceService'deki silinme sürelerinden biraz daha KISA.
        var dayTask = RunWarmerLoopAsync(PriceHistoryRange.Day, TimeSpan.FromMinutes(4), stoppingToken);
        var weekTask = RunWarmerLoopAsync(PriceHistoryRange.Week, TimeSpan.FromMinutes(25), stoppingToken);
        var monthTask = RunWarmerLoopAsync(PriceHistoryRange.Month, TimeSpan.FromMinutes(110), stoppingToken);
        var yearTask = RunWarmerLoopAsync(PriceHistoryRange.Year, TimeSpan.FromHours(23), stoppingToken);

        // Hepsini asenkron olarak bekletiyoruz. Bu satır uygulama kapanana kadar asla aşağıya inmez.
        await Task.WhenAll(dayTask, weekTask, monthTask, yearTask);
    }

    private async Task RunWarmerLoopAsync(PriceHistoryRange range, TimeSpan refreshInterval, CancellationToken stoppingToken)
    {
        // stoppingToken iptal edilmediği (sunucu kapanmadığı) sürece sonsuza kadar dön!
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // KURAL 1: Bellek Sızıntısını (Memory Leak) önlemek için Scope açıyoruz.
                using var scope = _serviceProvider.CreateScope();
                
                // Taptaze, bu döngüye özel servislerimizi çekiyoruz
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var priceService = scope.ServiceProvider.GetRequiredService<IPriceService>();

                // Veritabanındaki aktif olan (takip edilen) varlık kodlarını alıyoruz (Örn: GUMUS_TL, BIST100)
                var activeSymbols = await dbContext.TrackedSymbols
                    .Select(x => x.Code)
                    .ToListAsync(stoppingToken);

                foreach (var code in activeSymbols)
                {
                    // Muazzam Mimari Hamle: DB'ye biz gitmiyoruz. 
                    // Sanki mobil uygulamadan bir kullanıcıymışız gibi PriceService'den istiyoruz.
                    // PriceService, zırhlı koduyla (SemaphoreSlim) bunu karşılayacak, 
                    // kendi kurallarına göre DB'ye gidecek ve Cache'i dolduracak.
                    await priceService.GetHistoryAsync(code, range, stoppingToken);
                }

                _logger.LogInformation($"✅ [{range}] için tüm grafikler vitrine dizildi (Pre-warmed). Sonraki tazeleme: {refreshInterval.TotalMinutes} dk sonra.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ [{range}] Hayalet İşçi çalışırken hata oluştu.");
            }

            // KURAL 2: Erken kalk. Belirlenen süre kadar uyu, sonra döngü başa sarsın ve tekrar tazelemek için çalışsın.
            await Task.Delay(refreshInterval, stoppingToken);
        }
    }
}
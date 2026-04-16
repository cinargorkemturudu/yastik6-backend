using Yastık6.Api.Application.Common.Interfaces;

namespace Yastık6.Api.Infrastructure.BackgroundJobs;

public sealed class DailyRolloverService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DailyRolloverService> _logger;

    public DailyRolloverService(
        IServiceScopeFactory scopeFactory,
        ILogger<DailyRolloverService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DailyRolloverService başlatıldı. Sağlayıcı gün dönümü görevleri ayarlanıyor...");

        // Sistemdeki sağlayıcıların tiplerini geçici bir scope ile keşfediyoruz
        List<Type> providerTypes;
        using (var scope = _scopeFactory.CreateScope())
        {
            var providers = scope.ServiceProvider.GetServices<IPriceProvider>();
            providerTypes = providers.Select(p => p.GetType()).ToList();
        }

        // Sistemdeki her sağlayıcı (Harem, BIST vb.) için ayrı bir asenkron görev başlatır
        var tasks = providerTypes.Select(type => RunProviderCloseTaskAsync(type, stoppingToken));
        
        return Task.WhenAll(tasks);
    }

    private async Task RunProviderCloseTaskAsync(Type providerType, CancellationToken stoppingToken)
    {
        // 1. İlk açılışta veritabanından mevcut kapanışları RAM'e yükle (Warmup)
        using (var warmupScope = _scopeFactory.CreateScope())
        {
            var provider = (IPriceProvider)warmupScope.ServiceProvider.GetServices<IPriceProvider>().First(p => p.GetType() == providerType);
            await LoadClosesForProviderAsync(provider, warmupScope, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                TimeSpan timeUntilClose;

                // 2. Sağlayıcının kapanış saatine ne kadar kaldığını hesapla
                using (var loopScope = _scopeFactory.CreateScope())
                {
                    var provider = (IPriceProvider)loopScope.ServiceProvider.GetServices<IPriceProvider>().First(p => p.GetType() == providerType);
                    
                    var now = DateTime.Now;
                    var closeTimeToday = now.Date.Add(provider.MarketCloseTime);
                    
                    // Eğer bugünün kapanış saati çoktan geçtiyse, yarını hedefle
                    var nextCloseTime = now >= closeTimeToday 
                        ? closeTimeToday.AddDays(1) 
                        : closeTimeToday;

                    timeUntilClose = nextCloseTime - now;

                    _logger.LogInformation(
                        "[{Provider}] Kapanış fiyatları senkronize edildi. Bir sonraki gün dönümü {Delay} saat sonra ({NextClose}).", 
                        provider.ProviderName, 
                        Math.Round(timeUntilClose.TotalHours, 1),
                        nextCloseTime.ToString("HH:mm"));
                }

                // 3. Hedeflenen kapanış saatine kadar uyu
                await Task.Delay(timeUntilClose, stoppingToken);

                // 4. Uyanınca kapanış fiyatlarını veritabanından çekip RAM'e at
                using (var actionScope = _scopeFactory.CreateScope())
                {
                    var provider = (IPriceProvider)actionScope.ServiceProvider.GetServices<IPriceProvider>().First(p => p.GetType() == providerType);
                    await LoadClosesForProviderAsync(provider, actionScope, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gün dönümü servisi çalışırken hata oluştu!");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    private async Task LoadClosesForProviderAsync(IPriceProvider provider, IServiceScope scope, CancellationToken ct)
    {
        var repository = scope.ServiceProvider.GetRequiredService<IPriceHistoryRepository>();
        var store = scope.ServiceProvider.GetRequiredService<IPreviousCloseStore>();
        
        // Sağlayıcının kapanış saati referans alınarak en son geçerli "Cutoff" (Sınır) tarihi belirlenir
        var now = DateTime.Now;
        var closeTimeToday = now.Date.Add(provider.MarketCloseTime);
        var cutoffLocal = now > closeTimeToday ? closeTimeToday : closeTimeToday.AddDays(-1);
        var cutoffUtc = new DateTimeOffset(cutoffLocal, TimeZoneInfo.Local.GetUtcOffset(cutoffLocal));

        // Sadece bu sağlayıcının (Source) verileri çekilir
        var previousCloses = await repository.GetPreviousClosePricesAsync(provider.ProviderName, cutoffUtc, ct);

        // RAM Önbelleği (ConcurrentDictionary) bu yeni verilerle kendini günceller
        store.SetAll(previousCloses);
    }
}
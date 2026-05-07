using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Infrastructure.Alerts; // [YENİ] Watchdog

namespace Yastık6.Api.Infrastructure.BackgroundJobs;

public sealed class DailyRolloverService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DailyRolloverService> _logger;
    private readonly IAlertService _alertService; // [YENİ]

    public DailyRolloverService(
        IServiceScopeFactory scopeFactory,
        ILogger<DailyRolloverService> logger,
        IAlertService alertService)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _alertService = alertService;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DailyRolloverService başlatıldı. Sağlayıcı gün dönümü görevleri ayarlanıyor...");

        List<Type> providerTypes;
        using (var scope = _scopeFactory.CreateScope())
        {
            var providers = scope.ServiceProvider.GetServices<IPriceProvider>();
            providerTypes = providers.Select(p => p.GetType()).ToList();
        }

        var tasks = providerTypes.Select(type => RunProviderCloseTaskAsync(type, stoppingToken));
        
        return Task.WhenAll(tasks);
    }

    private async Task RunProviderCloseTaskAsync(Type providerType, CancellationToken stoppingToken)
    {
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

                using (var loopScope = _scopeFactory.CreateScope())
                {
                    var provider = (IPriceProvider)loopScope.ServiceProvider.GetServices<IPriceProvider>().First(p => p.GetType() == providerType);
                    
                    var now = DateTime.Now;
                    var closeTimeToday = now.Date.Add(provider.MarketCloseTime);
                    
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

                await Task.Delay(timeUntilClose, stoppingToken);

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
                _logger.LogCritical(ex, "Gün dönümü servisi çalışırken hata oluştu!");
                
                // [WATCHDOG] Finansal hesaplamalar tehlikede!
                await _alertService.SendAlertAsync(
                    $"💀 *KRİTİK HATA: Gün Dönümü (Rollover)*\nGece kapanış fiyatları senkronize edilemedi! Yarınki % kâr/zarar oranları hatalı olabilir.\nHata: `{ex.Message}`", 
                    stoppingToken);
                
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    private async Task LoadClosesForProviderAsync(IPriceProvider provider, IServiceScope scope, CancellationToken ct)
    {
        var repository = scope.ServiceProvider.GetRequiredService<IPriceHistoryRepository>();
        var store = scope.ServiceProvider.GetRequiredService<IPreviousCloseStore>();
        
        var now = DateTime.Now;
        var closeTimeToday = now.Date.Add(provider.MarketCloseTime);
        var cutoffLocal = now > closeTimeToday ? closeTimeToday : closeTimeToday.AddDays(-1);
        var cutoffUtc = new DateTimeOffset(cutoffLocal, TimeZoneInfo.Local.GetUtcOffset(cutoffLocal));

        var previousCloses = await repository.GetPreviousClosePricesAsync(provider.ProviderName, cutoffUtc, ct);

        store.SetAll(previousCloses);
    }
}
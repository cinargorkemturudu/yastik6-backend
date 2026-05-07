using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Domain.Entities;
using Yastık6.Api.Infrastructure.Caching;
using Yastık6.Api.Infrastructure.Alerts; // [YENİ] Watchdog için eklendi

namespace Yastık6.Api.Infrastructure.BackgroundJobs;

public sealed class PriceSnapshotRefresher : BackgroundService
{
    private readonly IPriceSnapshotStore _ss;
    private readonly IPreviousCloseStore _previousCloseStore;
    private readonly ILogger<PriceSnapshotRefresher> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAlertService _alertService; // [YENİ] Bekçi Köpeği
    private static readonly object CacheLock = new object();

    public PriceSnapshotRefresher(
        IPriceSnapshotStore ss,
        IPreviousCloseStore previousCloseStore,
        ILogger<PriceSnapshotRefresher> logger,
        IServiceScopeFactory scopeFactory,
        IAlertService alertService) // [YENİ] Bağımlılık eklendi
    {
        _ss = ss;
        _previousCloseStore = previousCloseStore;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _alertService = alertService;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        List<Type> providerTypes;

        using (var scope = _scopeFactory.CreateScope())
        {
            var providers = scope.ServiceProvider.GetServices<IPriceProvider>();
            providerTypes = providers.Select(p => p.GetType()).ToList();
            
            _logger.LogInformation("Price refresh service started. Discovered providers: {Count}", providerTypes.Count);
        }

        var providerTasks = providerTypes.Select(type => RunProviderLoopAsync(type, stoppingToken));

        return Task.WhenAll(providerTasks);
    }

    private async Task RunProviderLoopAsync(Type providerType, CancellationToken stoppingToken)
    {
        bool isFirstRun = true; 

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                
                var provider = scope.ServiceProvider.GetServices<IPriceProvider>()
                                    .FirstOrDefault(p => p.GetType() == providerType);

                if (provider == null)
                {
                    _logger.LogWarning("Sağlayıcı {ProviderType} o an için çözülemedi, bu tur pas geçiliyor.", providerType.Name);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();
                var repository = scope.ServiceProvider.GetRequiredService<IPriceHistoryRepository>();

                if (!isFirstRun && !provider.IsMarketOpen())
                {
                    await Task.Delay(provider.RefreshInterval, stoppingToken);
                    continue;
                }

                var prices = await provider.GetMarketOverviewAsync(stoppingToken);
                
                _logger.LogDebug("[{Provider}] fetched and updated {Count} records.",
                    provider.ProviderName, prices.Count);

                if (prices.Count > 0)
                {
                    var calculatedPrices = new List<LatestPriceResponse>();

                    foreach (var price in prices)
                    {
                        var previousClose = _previousCloseStore.GetPreviousClose(price.ProductCode);
                        decimal changeRate = 0m;

                        if (previousClose.HasValue && previousClose.Value > 0)
                        {
                            changeRate = ((price.Ask - previousClose.Value) / previousClose.Value) * 100m;
                        }

                        calculatedPrices.Add(price with { ChangeRate = Math.Round(changeRate, 2) });
                    }

                    var recordsToInsert = new List<PriceRecord>();

                    lock (CacheLock)
                    {
                        var currentSnapshot = _ss.Current;
                        var currentDict = currentSnapshot?.Items.ToDictionary(p => p.ProductCode)
                                           ?? new Dictionary<string, LatestPriceResponse>();

                        foreach (var newPrice in calculatedPrices)
                        {
                            // --- 1. SIFIR FİYAT KALKANI (ZERO-PRICE SHIELD) ---
                            if (newPrice.Ask <= 0 || newPrice.Bid <= 0)
                            {
                                _logger.LogWarning("[SHIELD] {Code} fiyatı 0 veya negatif geldi. Veri reddedildi! Kaynak: {Source}", newPrice.ProductCode, newPrice.Source);
                                
                                // [WATCHDOG] Arka planda ateşle ve unut (Fire and forget)
                                _ = _alertService.SendAlertAsync($"⚠️ *SIFIR FİYAT REDDEDİLDİ*\nSembol: `{newPrice.ProductCode}`\nKaynak: `{newPrice.Source}`\nFiyat: `{newPrice.Ask}`\n_Sistem eski fiyatı korumaya devam ediyor._", stoppingToken);
                                
                                continue;
                            }

                            bool hasPriceChanged = true;

                            if (currentDict.TryGetValue(newPrice.ProductCode, out var existingPrice))
                            {
                                // --- 2. ANİ SAPMA KALKANI (FLASH CRASH SHIELD) ---
                                if (existingPrice.Ask > 0)
                                {
                                    var instantChange = Math.Abs((newPrice.Ask - existingPrice.Ask) / existingPrice.Ask * 100m);
                                    
                                    if (instantChange > 40m) 
                                    {
                                        _logger.LogWarning("[SHIELD] {Code} için mantıksız fiyat sapması (%{Rate:N2}). Veri reddedildi! Eski: {Old}, Yeni: {New}, Kaynak: {Source}", 
                                            newPrice.ProductCode, instantChange, existingPrice.Ask, newPrice.Ask, newPrice.Source);
                                            
                                        // [WATCHDOG] Arka planda ateşle ve unut (Fire and forget)
                                        _ = _alertService.SendAlertAsync($"🚨 *ANI SAPMA (FLASH CRASH) TESPİT EDİLDİ*\nSembol: `{newPrice.ProductCode}`\nEski: `{existingPrice.Ask}`\nYeni: `{newPrice.Ask}`\nDeğişim: `%{instantChange:N2}`\nKaynak: `{newPrice.Source}`\n\n*Müdahale gerekebilir!*", stoppingToken);
                                        
                                        continue;
                                    }
                                }

                                if (existingPrice.Ask == newPrice.Ask && existingPrice.Bid == newPrice.Bid)
                                {
                                    hasPriceChanged = false; 
                                }
                            }

                            if (hasPriceChanged)
                            {
                                recordsToInsert.Add(new PriceRecord
                                {
                                    Id = Guid.NewGuid(),
                                    ProductCode = newPrice.ProductCode,
                                    Source = newPrice.Source,
                                    AskPrice = newPrice.Ask,
                                    BidPrice = newPrice.Bid,
                                    LastPrice = newPrice.LastPrice,
                                    TimestampUtc = clock.UtcNow
                                });
                            }

                            currentDict[newPrice.ProductCode] = newPrice;
                        }

                        _ss.Set(new LatestPricesResponse(currentDict.Values.ToList()));
                    }

                    if (recordsToInsert.Count > 0)
                    {
                        await repository.AddPriceRecordsAsync(recordsToInsert, stoppingToken);
                    }

                    isFirstRun = false; 
                }

                await Task.Delay(provider.RefreshInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while fetching prices for provider type {ProviderType}!", providerType.Name);
                
                // [WATCHDOG] Tedarikçi tamamen patladığında ana akışı durdurup sana haber verir.
                await _alertService.SendAlertAsync(
                    $"⚠️ *Tedarikçi Çöktü: {providerType.Name}*\n" +
                    $"Zaman: `{DateTime.UtcNow:HH:mm:ss} UTC`\n" +
                    $"Hata: `{ex.Message}`", 
                    stoppingToken);

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
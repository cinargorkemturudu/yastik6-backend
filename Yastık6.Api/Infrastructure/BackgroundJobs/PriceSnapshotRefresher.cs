using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Domain.Entities;
using Yastık6.Api.Infrastructure.Alerts;
using Yastık6.Api.Infrastructure.Caching;

namespace Yastık6.Api.Infrastructure.BackgroundJobs;

/// <summary>
/// Sadece piyasa fiyatlarını periyodik olarak toplamak, iş hattına (Pipeline) paslamak
/// ve doğrulanmış sonuçları RAM Cache ile Veritabanına yazmakla görevli Altyapı Servisi (Trafik Polisi).
/// </summary>
public sealed class PriceSnapshotRefresher : BackgroundService
{
    private readonly IPriceSnapshotStore _snapshotStore;
    private readonly IPriceProcessor _priceProcessor; 
    private readonly ILogger<PriceSnapshotRefresher> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAlertService _alertService;
    
    private static readonly object CacheLock = new object();

    public PriceSnapshotRefresher(
        IPriceSnapshotStore snapshotStore,
        IPriceProcessor priceProcessor,
        ILogger<PriceSnapshotRefresher> logger,
        IServiceScopeFactory scopeFactory,
        IAlertService alertService)
    {
        _snapshotStore = snapshotStore;
        _priceProcessor = priceProcessor;
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
            
            // [KRİTİK DÜZELTME 1]: Yedek oyuncuları (YahooForex ve YahooCrypto) ana sahaya sürmüyoruz.
            // Onlar sadece DI sepetinde (Yedek Kulübesinde) acil durumlar için bekleyecekler.
            providerTypes = providers
                .Select(p => p.GetType())
                .Where(t => t.Name != "YahooForexClient" && t.Name != "YahooCryptoClient")
                .ToList();
            
            _logger.LogInformation("Price refresh service started. Active main providers: {Count}", providerTypes.Count);
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
                    _logger.LogWarning("Provider {ProviderType} could not be resolved. Skipping iteration.", providerType.Name);
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

                var rawPrices = await provider.GetMarketOverviewAsync(stoppingToken);
                
                if (rawPrices.Count > 0)
                {
                    var verifiedPrices = await _priceProcessor.ProcessPricesAsync(rawPrices, provider.ProviderName, stoppingToken);

                    if (verifiedPrices.Count == 0)
                    {
                        await Task.Delay(provider.RefreshInterval, stoppingToken);
                        continue;
                    }

                    var recordsToInsert = new List<PriceRecord>();

                    lock (CacheLock)
                    {
                        var currentSnapshot = _snapshotStore.Current;
                        var currentDict = currentSnapshot?.Items.ToDictionary(p => p.ProductCode)
                                           ?? new Dictionary<string, LatestPriceResponse>();

                        foreach (var newPrice in verifiedPrices)
                        {
                            bool hasPriceChanged = true;

                            if (currentDict.TryGetValue(newPrice.ProductCode, out var existingPrice))
                            {
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

                        _snapshotStore.Set(new LatestPricesResponse(
                            Items: currentDict.Values.ToList(), 
                            ComputedAtUtc: DateTimeOffset.UtcNow
                        ));
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
                _logger.LogError(ex, "A critical error occurred while fetching prices for provider {ProviderType}.", providerType.Name);
                
                // [KRİTİK DÜZELTME 2]: Akıllı Yedekleme Santrali (Switch)
                string? fallbackProviderName = providerType.Name switch
                {
                    "FreeCurrencyClient" => "YahooForexClient",
                    "BinanceClient" => "YahooCryptoClient",
                    _ => null
                };

                if (fallbackProviderName != null)
                {
                    _logger.LogWarning("{Provider} failed! Triggering immediate fallback to {Fallback}...", providerType.Name, fallbackProviderName);
                    
                    await _alertService.SendAlertAsync(
                        $"🔄 *Fallback Activated*\n`{providerType.Name}` crashed. Routing traffic to `{fallbackProviderName}`.\nError: `{ex.Message}`", 
                        $"Fallback_{providerType.Name}_{fallbackProviderName}",
                        stoppingToken);

                    try
                    {
                        // DB'ye yazma ameleliğini yapan kodu aşağıya ortak metoda çıkardık (DRY Prensibi)
                        await ExecuteFallbackAsync(fallbackProviderName, stoppingToken);
                    }
                    catch (Exception fallbackEx)
                    {
                        _logger.LogCritical(fallbackEx, "Fatal: Fallback provider {Fallback} also failed!", fallbackProviderName);
                        await _alertService.SendAlertAsync($"🚨 *DOUBLE CRASH*\nBoth `{providerType.Name}` and fallback `{fallbackProviderName}` failed!", $"DoubleCrash_{providerType.Name}", stoppingToken);
                    }
                }
                else
                {
                    await _alertService.SendAlertAsync(
                        $"⚠️ *Provider Crashed: {providerType.Name}*\nTime: `{DateTime.UtcNow:HH:mm:ss} UTC`\nError: `{ex.Message}`", 
                        $"Snapshot_{providerType.Name}_{ex.GetType().Name}",
                        stoppingToken);
                }

                // [KRİTİK DÜZELTME 3]: DDoS krizini çözen 5 dakikalık karantina cezası!
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    // [YENİ]: Her iki yedek sağlayıcının da kullandığı, kodu tertemiz yapan ortak veritabanı yazıcı metod.
    private async Task ExecuteFallbackAsync(string fallbackProviderName, CancellationToken stoppingToken)
    {
        using var fallbackScope = _scopeFactory.CreateScope();
        
        var fallbackProvider = fallbackScope.ServiceProvider.GetServices<IPriceProvider>()
            .FirstOrDefault(p => p.GetType().Name == fallbackProviderName);

        if (fallbackProvider == null) return;

        var clock = fallbackScope.ServiceProvider.GetRequiredService<ISystemClock>();
        var repository = fallbackScope.ServiceProvider.GetRequiredService<IPriceHistoryRepository>();

        var fallbackRawPrices = await fallbackProvider.GetMarketOverviewAsync(stoppingToken);
        
        if (fallbackRawPrices.Count > 0)
        {
            var verifiedPrices = await _priceProcessor.ProcessPricesAsync(fallbackRawPrices, fallbackProvider.ProviderName, stoppingToken);

            if (verifiedPrices.Count > 0)
            {
                var recordsToInsert = new List<PriceRecord>();

                lock (CacheLock)
                {
                    var currentSnapshot = _snapshotStore.Current;
                    var currentDict = currentSnapshot?.Items.ToDictionary(p => p.ProductCode)
                                       ?? new Dictionary<string, LatestPriceResponse>();

                    foreach (var newPrice in verifiedPrices)
                    {
                        bool hasPriceChanged = true;

                        if (currentDict.TryGetValue(newPrice.ProductCode, out var existingPrice))
                        {
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

                    _snapshotStore.Set(new LatestPricesResponse(
                        Items: currentDict.Values.ToList(), 
                        ComputedAtUtc: DateTimeOffset.UtcNow
                    ));
                }

                if (recordsToInsert.Count > 0)
                {
                    await repository.AddPriceRecordsAsync(recordsToInsert, stoppingToken);
                }
                
                _logger.LogInformation("Fallback to {FallbackProvider} completed successfully and data saved.", fallbackProviderName);
            }
        }
    }
}
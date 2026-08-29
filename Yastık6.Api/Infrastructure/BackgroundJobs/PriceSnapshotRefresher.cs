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
            
            // [KRİTİK DÜZELTME 1]: Yedek oyuncuları ana sahaya sürmüyoruz.
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

                bool isMarketOpen = provider.IsMarketOpen();

                if (!isFirstRun && !isMarketOpen)
                {
                    // [YENİ - HEARTBEAT BUMP]: Piyasa kapalıyken dış API'yi yormuyoruz, 
                    // ancak RAM cache'deki mevcut verilerin süresini (ValidUntilUtc) ileri sararak 
                    // bağlantının canlı olduğunu (nabız attığını) kanıtlıyoruz.
                    lock (CacheLock)
                    {
                        var currentSnapshot = _snapshotStore.Current;
                        if (currentSnapshot != null)
                        {
                            var currentDict = currentSnapshot.Items.ToDictionary(p => p.ProductCode);
                            bool snapshotModified = false;

                            foreach (var item in currentDict.Values.Where(v => v.Source == provider.ProviderName))
                            {
                                currentDict[item.ProductCode] = item with 
                                { 
                                    ValidUntilUtc = DateTimeOffset.UtcNow.Add(provider.RefreshInterval),
                                    IsMarketOpen = false
                                }; 
                                snapshotModified = true;
                            }

                            if (snapshotModified)
                            {
                                _snapshotStore.Set(new LatestPricesResponse(
                                    Items: currentDict.Values.ToList(),
                                    ComputedAtUtc: DateTimeOffset.UtcNow
                                ));
                            }
                        }
                    }

                    _logger.LogDebug("[HEARTBEAT] Bumped validUntilUtc for closed market: {Provider}", provider.ProviderName);
                    await Task.Delay(provider.RefreshInterval, stoppingToken);
                    continue;
                }

                var rawPrices = await provider.GetMarketOverviewAsync(stoppingToken);
                
                if (rawPrices.Count > 0)
                {
                    var verifiedPrices = await _priceProcessor.ProcessPricesAsync(rawPrices, provider.ProviderName, isMarketOpen, stoppingToken);

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

                        TimeSpan currentInterval = provider.RefreshInterval;

                        foreach (var newPrice in verifiedPrices)
                        {
                            var safePrice = newPrice with 
                            {
                                IsMarketOpen = isMarketOpen, 
                                ValidUntilUtc = DateTimeOffset.UtcNow.Add(currentInterval)
                            };

                            bool hasPriceChanged = true;

                            if (currentDict.TryGetValue(safePrice.ProductCode, out var existingPrice))
                            {
                                if (existingPrice.Ask == safePrice.Ask && existingPrice.Bid == safePrice.Bid)
                                {
                                    hasPriceChanged = false; 
                                }
                            }

                            if (hasPriceChanged)
                            {
                                recordsToInsert.Add(new PriceRecord
                                {
                                    Id = Guid.NewGuid(),
                                    ProductCode = safePrice.ProductCode,
                                    Source = safePrice.Source,
                                    AskPrice = safePrice.Ask,
                                    BidPrice = safePrice.Bid,
                                    LastPrice = safePrice.LastPrice,
                                    TimestampUtc = clock.UtcNow
                                });
                            }

                            currentDict[safePrice.ProductCode] = safePrice;
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

                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    private async Task ExecuteFallbackAsync(string fallbackProviderName, CancellationToken stoppingToken)
    {
        using var fallbackScope = _scopeFactory.CreateScope();
        
        var fallbackProvider = fallbackScope.ServiceProvider.GetServices<IPriceProvider>()
            .FirstOrDefault(p => p.GetType().Name == fallbackProviderName);

        if (fallbackProvider == null) return;

        var clock = fallbackScope.ServiceProvider.GetRequiredService<ISystemClock>();
        var repository = fallbackScope.ServiceProvider.GetRequiredService<IPriceHistoryRepository>();

        bool isMarketOpen = fallbackProvider.IsMarketOpen();

        var fallbackRawPrices = await fallbackProvider.GetMarketOverviewAsync(stoppingToken);
        
        if (fallbackRawPrices.Count > 0)
        {
            var verifiedPrices = await _priceProcessor.ProcessPricesAsync(fallbackRawPrices, fallbackProvider.ProviderName, isMarketOpen, stoppingToken);

            if (verifiedPrices.Count > 0)
            {
                var recordsToInsert = new List<PriceRecord>();

                lock (CacheLock)
                {
                    var currentSnapshot = _snapshotStore.Current;
                    var currentDict = currentSnapshot?.Items.ToDictionary(p => p.ProductCode)
                                       ?? new Dictionary<string, LatestPriceResponse>();

                    TimeSpan currentInterval = fallbackProvider.RefreshInterval;

                    foreach (var newPrice in verifiedPrices)
                    {
                        var safePrice = newPrice with
                        {
                            IsMarketOpen = isMarketOpen,
                            ValidUntilUtc = DateTimeOffset.UtcNow.Add(currentInterval)
                        };
                        
                        bool hasPriceChanged = true;

                        if (currentDict.TryGetValue(safePrice.ProductCode, out var existingPrice))
                        {
                            if (existingPrice.Ask == safePrice.Ask && existingPrice.Bid == safePrice.Bid)
                            {
                                hasPriceChanged = false; 
                            }
                        }

                        if (hasPriceChanged)
                        {
                            recordsToInsert.Add(new PriceRecord
                            {
                                Id = Guid.NewGuid(),
                                ProductCode = safePrice.ProductCode,
                                Source = safePrice.Source,
                                AskPrice = safePrice.Ask,
                                BidPrice = safePrice.Bid,
                                LastPrice = safePrice.LastPrice,
                                TimestampUtc = clock.UtcNow
                            });
                        }

                        currentDict[safePrice.ProductCode] = safePrice;
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
using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Domain.Entities;
using Yastık6.Api.Infrastructure.Caching;

namespace Yastık6.Api.Infrastructure.BackgroundJobs;

public sealed class PriceSnapshotRefresher : BackgroundService
{
    private readonly IPriceSnapshotStore _ss;
    private readonly IPreviousCloseStore _previousCloseStore;
    private readonly ILogger<PriceSnapshotRefresher> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private static readonly object CacheLock = new object();

    public PriceSnapshotRefresher(
        IPriceSnapshotStore ss,
        IPreviousCloseStore previousCloseStore,
        ILogger<PriceSnapshotRefresher> logger,
        IServiceScopeFactory scopeFactory)
    {
        _ss = ss;
        _previousCloseStore = previousCloseStore;
        _logger = logger;
        _scopeFactory = scopeFactory;
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
                
                // [DÜZELTİLDİ]: "(IPriceProvider?)" cast'i kaldırıldı. (Type cast is redundant)
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
                
                // [DÜZELTİLDİ]: "prices?.Count ?? 0" yerine direkt "prices.Count" kullanıldı. (Expression is never null)
                _logger.LogDebug("[{Provider}] fetched and updated {Count} records.",
                    provider.ProviderName, prices.Count);

                // [DÜZELTİLDİ]: prices null olamayacağı için direkt Count > 0 kontrolü yapıldı
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
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
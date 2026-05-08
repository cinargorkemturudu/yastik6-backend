using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Domain.Entities;
using Yastık6.Api.Infrastructure.Caching;
using Yastık6.Api.Infrastructure.Alerts;

namespace Yastık6.Api.Infrastructure.BackgroundJobs;

/// <summary>
/// Background service responsible for continuously fetching market prices from various providers,
/// applying safety shields (Zero-Price, Flash Crash), converting foreign assets to local currency (BFF pattern),
/// and maintaining both the in-memory cache and the historical database records.
/// </summary>
public sealed class PriceSnapshotRefresher : BackgroundService
{
    private readonly IPriceSnapshotStore _snapshotStore;
    private readonly IPreviousCloseStore _previousCloseStore;
    private readonly ILogger<PriceSnapshotRefresher> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAlertService _alertService;
    
    // Ensures thread-safe updates to the in-memory snapshot dictionary
    private static readonly object CacheLock = new object();

    public PriceSnapshotRefresher(
        IPriceSnapshotStore snapshotStore,
        IPreviousCloseStore previousCloseStore,
        ILogger<PriceSnapshotRefresher> logger,
        IServiceScopeFactory scopeFactory,
        IAlertService alertService)
    {
        _snapshotStore = snapshotStore;
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

        // Spawn a dedicated background task for each price provider
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

                // Respect market hours after the initial startup run
                if (!isFirstRun && !provider.IsMarketOpen())
                {
                    await Task.Delay(provider.RefreshInterval, stoppingToken);
                    continue;
                }

                // Fetch fresh data from the external source
                var prices = await provider.GetMarketOverviewAsync(stoppingToken);
                
                _logger.LogDebug("[{Provider}] fetched and updated {Count} records.", provider.ProviderName, prices.Count);

                if (prices.Count > 0)
                {
                    // -----------------------------------------------------------------------
                    // BFF ARCHITECTURE: USD TO TRY CONVERSION STRATEGY
                    // Attempt to find the freshest USDTRY rate. Check the current fetched batch first, 
                    // then fallback to the in-memory cache.
                    // -----------------------------------------------------------------------
                    var currentUsdPrice = prices.FirstOrDefault(p => p.ProductCode == "USDTRY=X")?.Ask
                                          ?? _snapshotStore.Current?.Items.FirstOrDefault(p => p.ProductCode == "USDTRY=X")?.Ask 
                                          ?? 0m;

                    var calculatedPrices = new List<LatestPriceResponse>();

                    foreach (var price in prices)
                    {
                        var previousClose = _previousCloseStore.GetPreviousClose(price.ProductCode);
                        decimal changeRate = 0m;

                        // Calculate daily percentage change based on previous close
                        if (previousClose.HasValue && previousClose.Value > 0)
                        {
                            changeRate = ((price.Ask - previousClose.Value) / previousClose.Value) * 100m;
                        }

                        var finalPrice = price with { ChangeRate = Math.Round(changeRate, 2) };

                        // Convert USD-based Crypto assets to TRY for the frontend
                        if (finalPrice.Type == "CRYPTO" && currentUsdPrice > 0)
                        {
                            finalPrice = finalPrice with 
                            {
                                Bid = finalPrice.Bid * currentUsdPrice,
                                Ask = finalPrice.Ask * currentUsdPrice,
                                LastPrice = (finalPrice.LastPrice ?? 0m) * currentUsdPrice,
                                IsConvertedToTry = true
                            };
                        }

                        calculatedPrices.Add(finalPrice);
                    }

                    var recordsToInsert = new List<PriceRecord>();

                    // Synchronize state updates to prevent race conditions across provider threads
                    lock (CacheLock)
                    {
                        var currentSnapshot = _snapshotStore.Current;
                        var currentDict = currentSnapshot?.Items.ToDictionary(p => p.ProductCode)
                                           ?? new Dictionary<string, LatestPriceResponse>();

                        foreach (var newPrice in calculatedPrices)
                        {
                            // --- SHIELD 1: ZERO-PRICE ANOMALY PREVENTION ---
                            if (newPrice.Ask <= 0 || newPrice.Bid <= 0)
                            {
                                _logger.LogWarning("[SHIELD] {Code} returned zero/negative price. Data rejected! Source: {Source}", newPrice.ProductCode, newPrice.Source);
                                
                                _ = _alertService.SendAlertAsync($"⚠️ *ZERO PRICE REJECTED*\nSymbol: `{newPrice.ProductCode}`\nSource: `{newPrice.Source}`\nPrice: `{newPrice.Ask}`\n_System retained the previous valid price._", stoppingToken);
                                
                                continue;
                            }

                            bool hasPriceChanged = true;

                            if (currentDict.TryGetValue(newPrice.ProductCode, out var existingPrice))
                            {
                                // --- SHIELD 2: FLASH CRASH / SPIKE PREVENTION ---
                                if (existingPrice.Ask > 0)
                                {
                                    var instantChange = Math.Abs((newPrice.Ask - existingPrice.Ask) / existingPrice.Ask * 100m);
                                    
                                    // Reject data if price shifts by more than 40% in a single tick
                                    if (instantChange > 40m) 
                                    {
                                        _logger.LogWarning("[SHIELD] Unreasonable deviation detected for {Code} ({Rate:N2}%). Data rejected! Old: {Old}, New: {New}, Source: {Source}", 
                                            newPrice.ProductCode, instantChange, existingPrice.Ask, newPrice.Ask, newPrice.Source);
                                            
                                        _ = _alertService.SendAlertAsync($"🚨 *FLASH CRASH DETECTED*\nSymbol: `{newPrice.ProductCode}`\nOld: `{existingPrice.Ask}`\nNew: `{newPrice.Ask}`\nDeviation: `%{instantChange:N2}`\nSource: `{newPrice.Source}`\n\n*Manual review may be required!*", stoppingToken);
                                        
                                        continue;
                                    }
                                }

                                // Avoid redundant database inserts if the price hasn't moved
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

                            // Update or insert the latest validated price into the dictionary
                            currentDict[newPrice.ProductCode] = newPrice;
                        }

                        // Commit the updated state to the in-memory store with the current UTC timestamp
                        _snapshotStore.Set(new LatestPricesResponse(
                            Items: currentDict.Values.ToList(), 
                            ComputedAtUtc: DateTimeOffset.UtcNow
                        ));
                    }

                    // Bulk insert historical records asynchronously outside the lock
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
                // Graceful shutdown requested
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A critical error occurred while fetching prices for provider {ProviderType}.", providerType.Name);
                
                await _alertService.SendAlertAsync(
                    $"⚠️ *Provider Crashed: {providerType.Name}*\n" +
                    $"Time: `{DateTime.UtcNow:HH:mm:ss} UTC`\n" +
                    $"Error: `{ex.Message}`", 
                    stoppingToken);

                // Backoff before retrying to prevent aggressive loop spinning on failure
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
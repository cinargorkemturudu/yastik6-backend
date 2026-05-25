using Microsoft.EntityFrameworkCore;
using Polly; 
using YahooFinanceApi;
using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Infrastructure.Caching;
using Yastık6.Api.Infrastructure.Persistence;
using Yastık6.Api.Infrastructure.Alerts; // [YENİ] Watchdog

namespace Yastık6.Api.Infrastructure.Providers.YahooFinance;

public abstract class YahooFinanceBase : IPriceProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    protected YahooFinanceBase(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public abstract string ProviderName { get; }
    public abstract TimeSpan RefreshInterval { get; }
    protected abstract string[] TargetSymbolTypes { get; }
    public virtual TimeSpan MarketCloseTime => TimeSpan.FromHours(23.99);

    public virtual bool IsMarketOpen()
    {
        var today = DateTime.UtcNow.DayOfWeek;
        return today != DayOfWeek.Saturday && today != DayOfWeek.Sunday;
    }

    public async Task<List<LatestPriceResponse>> GetMarketOverviewAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var snapshotStore = scope.ServiceProvider.GetRequiredService<IPriceSnapshotStore>();

        var activeSymbols = await dbContext.TrackedSymbols
            .Where(s => s.IsActive && TargetSymbolTypes.Contains(s.SymbolType))
            .ToListAsync(ct);

        if (!activeSymbols.Any()) return new List<LatestPriceResponse>();

        var cachedPrices = snapshotStore.Current?.Items.ToDictionary(i => i.ProductCode) 
            ?? new Dictionary<string, LatestPriceResponse>();

        var symbolsToFetch = new List<string>();
        
        var stalenessThreshold = DateTimeOffset.UtcNow.AddMinutes(-15); 

        foreach (var dbSymbol in activeSymbols)
        {
            if (cachedPrices.TryGetValue(dbSymbol.Code, out var cacheItem))
            {
                if (cacheItem.Source != ProviderName && cacheItem.AsOfUtc > stalenessThreshold)
                {
                    continue; 
                }
            }
            symbolsToFetch.Add(dbSymbol.Code);
        }

        if (!symbolsToFetch.Any()) 
        {
            return new List<LatestPriceResponse>();
        }

        var results = new List<LatestPriceResponse>();
        var chunks = symbolsToFetch.Chunk(50); 
        var random = new Random(); 

        var retryPolicy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(
                retryCount: 2, 
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(2 * attempt),
                onRetry: (exception, timeSpan, retryCount) => 
                {
                    _logger.LogWarning("Yahoo API geçici hata verdi ({Message}). {Delay} saniye sonra tekrar deneniyor (Deneme: {RetryCount}).", exception.Message, timeSpan.TotalSeconds, retryCount);
                });

        foreach (var chunk in chunks)
        {
            try
            {
                await retryPolicy.ExecuteAsync(async () => 
                {
                    var securities = await Yahoo.Symbols(chunk).QueryAsync(ct);
                    foreach (var kvp in securities)
                    {
                        var symbolCode = kvp.Key;
                        var security = kvp.Value;
                        
                        var dbSymbol = activeSymbols.FirstOrDefault(s => string.Equals(s.Code, symbolCode, StringComparison.OrdinalIgnoreCase));
                        
                        if (dbSymbol == null) continue;
                        
                        var mappedItem = YahooFinanceMapper.MapToResponse(
                            symbolCode, 
                            security, 
                            dbSymbol.SymbolType, 
                            ProviderName);

                        if (mappedItem != null)
                        {
                            results.Add(mappedItem);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Yahoo Finance üzerinden {ProviderName} için veri çekilirken hata oluştu.", ProviderName);

                // [WATCHDOG] Shadow Ban veya Limit Aşımı kontrolü
                if (ex.Message.Contains("429") || ex.Message.Contains("Too Many Requests") || ex.Message.Contains("Forbidden"))
                {
                    var alertService = scope.ServiceProvider.GetService<IAlertService>();
                    if (alertService != null)
                    {
                        _ = alertService.SendAlertAsync(
                            $"🚫 *YAHOO SHADOW BAN*\nModül: `{ProviderName}`\nÇok fazla istek atıldı, IP adresi geçici kısıtlandı!", 
                            $"YahooShadowBan_{ProviderName}",
                            ct);
                    }
                }
            }

            var jitterMs = random.Next(0, 300);
            await Task.Delay(500 + jitterMs, ct);
        }

        return results;
    }
}
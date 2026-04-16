using Microsoft.EntityFrameworkCore;
using Polly; // [EKLENDİ]: Polly kütüphanesi
using YahooFinanceApi;
using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Infrastructure.Persistence;

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

    // Properties to be implemented by derived classes
    public abstract string ProviderName { get; }
    public abstract TimeSpan RefreshInterval { get; }
    
    // Defines which symbol types this client is responsible for (e.g., "BIST", "FOREX")
    protected abstract string[] TargetSymbolTypes { get; }

    public virtual TimeSpan MarketCloseTime => TimeSpan.FromHours(23.99);

    public virtual bool IsMarketOpen()
    {
        // Default market open logic: Closed on weekends
        var today = DateTime.UtcNow.DayOfWeek;
        return today != DayOfWeek.Saturday && today != DayOfWeek.Sunday;
    }

    public async Task<List<LatestPriceResponse>> GetMarketOverviewAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Fetch only the symbols that match the target types of the derived class
        var activeSymbols = await dbContext.TrackedSymbols
            .Where(s => s.IsActive && TargetSymbolTypes.Contains(s.SymbolType))
            .ToListAsync(ct);

        if (!activeSymbols.Any()) return new List<LatestPriceResponse>();

        var results = new List<LatestPriceResponse>();
        var symbolCodes = activeSymbols.Select(s => s.Code).ToList();
        
        // Process symbols in chunks of 50 to comply with Yahoo Finance API rate limits
        var chunks = symbolCodes.Chunk(50);
        
        var random = new Random(); 

        var retryPolicy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(
                retryCount: 2, 
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(2 * attempt),
                onRetry: (exception, timeSpan, retryCount, context) => 
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
                        
                        var dbSymbol = activeSymbols.First(s => s.Code == symbolCode);
                        
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
                _logger.LogError(ex, "Error occurred while fetching {ChunkSize} symbols from Yahoo Finance for provider {ProviderName} after all retries.", chunk.Length, ProviderName);
            }

            var jitterMs = random.Next(0, 300);
            await Task.Delay(500 + jitterMs, ct);
        }

        return results;
    }
}
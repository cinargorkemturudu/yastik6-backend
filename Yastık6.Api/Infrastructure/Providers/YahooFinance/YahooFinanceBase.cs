using Microsoft.EntityFrameworkCore;
using YahooFinanceApi;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Infrastructure.Caching;
using Yastık6.Api.Infrastructure.Persistence;
using Yastık6.Api.Infrastructure.Alerts;
using Yastık6.Api.Infrastructure.Providers.Base;
using Yastık6.Api.Application.Common.Interfaces;

namespace Yastık6.Api.Infrastructure.Providers.YahooFinance;

public abstract class YahooFinanceBase : BasePriceClient
{
    private readonly IServiceScopeFactory _scopeFactory;

    // [DÜZELTİLDİ]: ILogger, AlertTracker ve ISystemClock artık DI üzerinden alınıp "base" (Anakart) sınıfına yollanıyor.
    protected YahooFinanceBase(
        IServiceScopeFactory scopeFactory, 
        ILogger<YahooFinanceBase> logger, 
        AlertTracker tracker, 
        ISystemClock clock) 
        : base(logger, tracker, clock)
    {
        _scopeFactory = scopeFactory;
    }

    // Base sınıftaki (abstract/virtual) özellikleri eziyoruz (override)
    public override abstract string ProviderName { get; }
    public override abstract TimeSpan RefreshInterval { get; }
    protected abstract string[] TargetSymbolTypes { get; }
    public override TimeSpan MarketCloseTime => TimeSpan.FromHours(23.99);

    public override bool IsMarketOpen()
    {
        // [DÜZELTİLDİ]: DateTime.UtcNow yerine base sınıftan gelen Clock objesi kullanılıyor
        var today = Clock.UtcNow.DayOfWeek; 
        return today != DayOfWeek.Saturday && today != DayOfWeek.Sunday;
    }

    // [DÜZELTİLDİ]: GetMarketOverviewAsync yerine, Anakartın beklediği "FetchDataAsync" metodunu override ediyoruz
    protected override async Task<List<LatestPriceResponse>> FetchDataAsync(CancellationToken ct = default)
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
        
        // [DÜZELTİLDİ]: DateTimeOffset.UtcNow yerine zaman motorunu kullandık
        var stalenessThreshold = Clock.UtcNow.AddMinutes(-15); 

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

        foreach (var chunk in chunks)
        {
            try
            {
                // [SİLİNDİ]: Polly Retry policy'sini kaldırdık çünkü artık istek zaten Anakarttaki Polly'nin içinden buraya düşüyor.
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
                        
                        // Base sınıfta korumalı (protected) olan Tracker'ı kullanabiliyoruz
                        Tracker.MarkAsResolved($"ZeroPrice_{symbolCode}");
                    }
                }
            }
            catch (Exception ex)
            {
                // [ÖNEMLİ DETAY]: Genel SYS_Yahoo_Api_Down loglarını ve sabıkalarını artık Anakart halledecek.
                // Biz burada sadece Yahoo'ya HAS olan o 429 Shadow Ban durumunu yakalamak için try-catch tuttuk.
                if (ex.Message.Contains("429") || ex.Message.Contains("Too Many Requests") || ex.Message.Contains("Forbidden"))
                {
                    var alertService = scope.ServiceProvider.GetService<IAlertService>();
                    if (alertService != null)
                    {
                        Tracker.ShouldSendAlert("SYS_Yahoo_ShadowBan");
                        
                        _ = alertService.SendAlertAsync(
                            $"🚫 *YAHOO SHADOW BAN*\nModül: `{ProviderName}`\nÇok fazla istek atıldı, IP adresi geçici kısıtlandı!", 
                            $"YahooShadowBan_{ProviderName}",
                            ct);
                    }
                }
                
                // [KRİTİK]: Yakaladığımız hatayı fırlatıyoruz (throw) ki Evrensel Anakart (BasePriceClient)
                // bunu yakalayıp genel Retry mekanizmasını (Polly) tetikleyebilsin ve sistemi güvenle durdursun.
                throw; 
            }

            var jitterMs = random.Next(0, 300);
            await Task.Delay(500 + jitterMs, ct);
        }

        return results;
    }
}
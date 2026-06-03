using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Infrastructure.Alerts;
using Yastık6.Api.Infrastructure.Time;

namespace Yastık6.Api.Infrastructure.Providers.YahooFinance;

public sealed class YahooBistClient : YahooFinanceBase
{
    // [DÜZELTİLDİ]: AlertTracker ve ISystemClock eklendi, base sınıfa iletildi.
    public YahooBistClient(
        IServiceScopeFactory scopeFactory, 
        ILogger<YahooBistClient> logger,
        AlertTracker tracker,
        ISystemClock clock) 
        : base(scopeFactory, logger, tracker, clock)
    {
    }

    public override string ProviderName => "YahooBist";
    
    public override TimeSpan RefreshInterval => TimeSpan.FromMinutes(15);
    
    protected override string[] TargetSymbolTypes => new[] { "BIST" };
    
    public override bool IsMarketOpen()
    {
        // [DÜZELTİLDİ]: DateTime.UtcNow yerine anakarttan gelen Clock kullanılıyor.
        var now = Clock.UtcNow.AddHours(3); // Türkiye Saati
        var isWeekend = now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday;
        
        var isNormalTrading = now.Hour >= 9 && now.Hour < 18;
        
        var isClosingPriceWindow = now.Hour == 18 && now.Minute >= 11 && now.Minute <= 25;

        return !isWeekend && (isNormalTrading || isClosingPriceWindow);
    }
}
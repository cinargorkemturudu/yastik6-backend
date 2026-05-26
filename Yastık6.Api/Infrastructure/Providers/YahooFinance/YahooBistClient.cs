using Yastık6.Api.Application.Common.Interfaces;

namespace Yastık6.Api.Infrastructure.Providers.YahooFinance;

public sealed class YahooBistClient : YahooFinanceBase
{
    public YahooBistClient(IServiceScopeFactory scopeFactory, ILogger<YahooBistClient> logger) 
        : base(scopeFactory, logger)
    {
    }

    public override string ProviderName => "YahooBist";
    
    public override TimeSpan RefreshInterval => TimeSpan.FromMinutes(15);
    
    protected override string[] TargetSymbolTypes => new[] { "BIST" };
    
    public override bool IsMarketOpen()
    {
        var now = DateTime.UtcNow.AddHours(3); // Türkiye Saati
        var isWeekend = now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday;
        
        var isNormalTrading = now.Hour >= 9 && now.Hour < 18;
        
        var isClosingPriceWindow = now.Hour == 18 && now.Minute >= 11 && now.Minute <= 25;

        return !isWeekend && (isNormalTrading || isClosingPriceWindow);
    }
}
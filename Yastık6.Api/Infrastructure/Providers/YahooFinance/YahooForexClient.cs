using Yastık6.Api.Application.Common.Interfaces;

namespace Yastık6.Api.Infrastructure.Providers.YahooFinance;

/// Client responsible for fetching Forex market data from Yahoo Finance.
/// This client manages currency pairs like USD/TRY, EUR/USD, etc.
public sealed class YahooForexClient : YahooFinanceBase
{
    public YahooForexClient(IServiceScopeFactory scopeFactory, ILogger<YahooForexClient> logger) 
        : base(scopeFactory, logger)
    {
    }

    public override string ProviderName => "YahooForex";
    
    // Forex markets are updated frequently during active trading sessions.
    public override TimeSpan RefreshInterval => TimeSpan.FromMinutes(1);
    
    protected override string[] TargetSymbolTypes => new[] { "FOREX" };

    /// Implements specific market hours for Forex.
    /// Global Forex markets close on Friday night and reopen on Sunday night (UTC).
    public override bool IsMarketOpen()
    {
        var now = DateTime.UtcNow;
        var day = now.DayOfWeek;

        // Market is fully closed on Saturdays.
        if (day == DayOfWeek.Saturday) return false;

        // Market remains closed on Sundays until the Sydney session opens (approx. 21:00 UTC).
        if (day == DayOfWeek.Sunday && now.Hour < 21) return false;

        return true;
    }
}
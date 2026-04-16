using Yastık6.Api.Application.Common.Interfaces;

namespace Yastık6.Api.Infrastructure.Providers.YahooFinance;


/// Client responsible for fetching Cryptocurrency market data from Yahoo Finance.
/// Handles high-volatility assets like BTC, ETH, etc.
public sealed class YahooCryptoClient : YahooFinanceBase
{
    public YahooCryptoClient(IServiceScopeFactory scopeFactory, ILogger<YahooCryptoClient> logger) 
        : base(scopeFactory, logger)
    {
    }

    public override string ProviderName => "YahooCrypto";
    
    // Cryptocurrencies trade 24/7 and require frequent updates due to high volatility.
    public override TimeSpan RefreshInterval => TimeSpan.FromMinutes(1);
    
    protected override string[] TargetSymbolTypes => new[] { "CRYPTO" };

    
    /// Cryptocurrencies do not follow traditional market hours and trade 24/7/365.
    public override bool IsMarketOpen() => true;
}
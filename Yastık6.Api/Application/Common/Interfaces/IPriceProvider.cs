using Yastık6.Api.Application.Features.Prices;

namespace Yastık6.Api.Application.Common.Interfaces;

public interface IPriceProvider
{
    string ProviderName { get; }

    bool IsMarketOpen();
    
    TimeSpan RefreshInterval { get; }
    TimeSpan MarketCloseTime { get; }

    Task<List<LatestPriceResponse>> GetMarketOverviewAsync(CancellationToken ct = default);
    
}
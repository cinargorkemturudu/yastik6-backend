using Yastık6.Api.Application.Features.Prices;

namespace Yastık6.Api.Application.Common.Interfaces;

public interface IPriceProcessor
{
    Task<List<LatestPriceResponse>> ProcessPricesAsync(
        List<LatestPriceResponse> rawPrices, 
        string providerName, 
        CancellationToken ct = default);
}
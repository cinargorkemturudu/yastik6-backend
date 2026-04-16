namespace Yastık6.Api.Application.Features.Prices;

public sealed record LatestPricesResponse(
    IReadOnlyList<LatestPriceResponse> Items);
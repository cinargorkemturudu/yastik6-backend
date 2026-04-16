
namespace Yastık6.Api.Application.Features.Prices;

public interface IPriceService
{
    Task<PriceHistoryResponse> GetHistoryAsync(string productCode, PriceHistoryRange range, CancellationToken ct = default);
}
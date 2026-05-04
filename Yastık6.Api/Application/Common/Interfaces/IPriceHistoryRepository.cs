using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Domain.Entities;

namespace Yastık6.Api.Application.Common.Interfaces;

public interface IPriceHistoryRepository
{
    Task AddPriceRecordsAsync(IEnumerable<PriceRecord> records, CancellationToken ct = default);

    Task<List<PricePointDto>> GetBucketedHistoryAsync(string productCode, DateTimeOffset since, TimeSpan bucketSize, CancellationToken ct = default); 
    
    Task<IReadOnlyList<PriceRecord>> GetPricesBetweenAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default);
    
    Task DeletePriceRecordsAsync(IEnumerable<PriceRecord> records, CancellationToken ct = default);
    
    Task<Dictionary<string, decimal>> GetPreviousClosePricesAsync(string source, DateTimeOffset cutoff, CancellationToken ct = default);
    
    Task<PricePointDto?> GetLastPriceBeforeAsync(string productCode, DateTimeOffset targetDate, CancellationToken ct = default);
}


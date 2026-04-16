using System.Collections.Concurrent;
using Yastık6.Api.Application.Common.Interfaces;

namespace Yastık6.Api.Infrastructure.Caching;

public sealed class PreviousCloseStore : IPreviousCloseStore
{
    private readonly ConcurrentDictionary<string, decimal> _closes = new(StringComparer.OrdinalIgnoreCase);

    public decimal? GetPreviousClose(string productCode)
    {
        if (_closes.TryGetValue(productCode, out var price))
        {
            return price;
        }
        
        return null; 
    }

    public void SetAll(Dictionary<string, decimal> previousCloses)
    {
        foreach (var kvp in previousCloses)
        {
            _closes[kvp.Key] = kvp.Value;
        }
    }
}
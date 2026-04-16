using Yastık6.Api.Application.Features.Prices;

namespace Yastık6.Api.Infrastructure.Caching;

public interface IPriceSnapshotStore
{
    LatestPricesResponse? Current { get; }
    void Set(LatestPricesResponse snapshot);
}

public sealed class PriceSnapshotStore : IPriceSnapshotStore
{
    private LatestPricesResponse? _current;
    public LatestPricesResponse? Current => _current;
    public void Set(LatestPricesResponse snapshot) => _current = snapshot;
}
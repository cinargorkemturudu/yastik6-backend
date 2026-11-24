using Xunit;
using FluentAssertions;
using Yastık6.Api.Application.Features.Prices;

namespace Yastık6.Api.Tests.Application.Features;

public class StalePriceEvictorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private static LatestPriceResponse Price(string code, TimeSpan validUntilAgo, bool isMarketOpen = true) => new(
        ProductCode: code,
        Name: code,
        Type: "CRYPTO",
        Bid: 1m,
        Ask: 1m,
        LastPrice: 1m,
        ChangeRate: 0m,
        FormattedBid: "1",
        FormattedAsk: "1",
        AsOfUtc: Now - validUntilAgo - TimeSpan.FromMinutes(1),
        ValidUntilUtc: Now - validUntilAgo,
        Source: "Binance",
        IsMarketOpen: isMarketOpen);

    private static Dictionary<string, LatestPriceResponse> Snapshot(params LatestPriceResponse[] prices)
        => prices.ToDictionary(p => p.ProductCode);

    [Fact]
    public void RemovesAssetsWhoseValidityExpiredLongAgo_AndReturnsThem()
    {
        var items = Snapshot(Price("DEAD-USD", TimeSpan.FromDays(27)), Price("BTC-USD", TimeSpan.FromSeconds(-30)));

        var evicted = StalePriceEvictor.Evict(items, Now, MaxAge);

        evicted.Should().ContainSingle().Which.ProductCode.Should().Be("DEAD-USD");
        items.Should().ContainKey("BTC-USD").And.NotContainKey("DEAD-USD");
    }

    [Fact]
    public void KeepsAssetsThatExpiredRecently_SoAShortProviderOutageDoesNotBlankTheApp()
    {
        var items = Snapshot(Price("GOLD", TimeSpan.FromHours(23)), Price("USDTRY=X", TimeSpan.FromMinutes(10)));

        var evicted = StalePriceEvictor.Evict(items, Now, MaxAge);

        evicted.Should().BeEmpty();
        items.Should().HaveCount(2);
    }

    [Fact]
    public void KeepsAssetsRecordedWhileTheirMarketWasClosed()
    {
        // Piyasa kapalıyken kaydedilen fiyatın geçerlilik süresi zaten uzun hafta sonu boşluğunu kapsar; ölü varlık değildir.
        var items = Snapshot(Price("THYAO.IS", TimeSpan.FromDays(3), isMarketOpen: false));

        var evicted = StalePriceEvictor.Evict(items, Now, MaxAge);

        evicted.Should().BeEmpty();
        items.Should().ContainKey("THYAO.IS");
    }

    [Fact]
    public void BoundaryIsExclusive_ExactlyMaxAgeIsKept()
    {
        var items = Snapshot(Price("EDGE-USD", MaxAge));

        StalePriceEvictor.Evict(items, Now, MaxAge).Should().BeEmpty();
    }

    [Fact]
    public void EmptySnapshot_IsNoOp()
    {
        StalePriceEvictor.Evict(new Dictionary<string, LatestPriceResponse>(), Now, MaxAge).Should().BeEmpty();
    }
}

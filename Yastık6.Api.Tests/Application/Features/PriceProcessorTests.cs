using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using FluentAssertions;
using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Infrastructure.Alerts;
using Yastık6.Api.Infrastructure.Caching;

namespace Yastık6.Api.Tests.Application.Features;

public class PriceProcessorTests
{
    private static LatestPriceResponse MakePrice(
        string productCode,
        decimal bid,
        decimal ask,
        string type = "METAL",
        DateTimeOffset? validUntilUtc = null,
        decimal? lastPrice = null)
        => new(
            ProductCode: productCode,
            Name: productCode,
            Type: type,
            Bid: bid,
            Ask: ask,
            LastPrice: lastPrice,
            ChangeRate: 0m,
            FormattedBid: bid.ToString(),
            FormattedAsk: ask.ToString(),
            AsOfUtc: DateTimeOffset.UtcNow,
            ValidUntilUtc: validUntilUtc ?? DateTimeOffset.UtcNow.AddMinutes(5),
            Source: "Test"
        );

    private static PriceProcessor CreateProcessor(
        IPriceSnapshotStore snapshotStore,
        out Mock<IPreviousCloseStore> previousCloseStoreMock)
    {
        previousCloseStoreMock = new Mock<IPreviousCloseStore>();
        previousCloseStoreMock.Setup(s => s.GetPreviousClose(It.IsAny<string>())).Returns((decimal?)null);

        var shieldValidator = new PriceShieldValidator(
            NullLogger<PriceShieldValidator>.Instance,
            Mock.Of<IAlertService>(),
            new AlertTracker());

        return new PriceProcessor(
            snapshotStore,
            previousCloseStoreMock.Object,
            shieldValidator,
            NullLogger<PriceProcessor>.Instance);
    }

    [Fact]
    public async Task ProcessPricesAsync_FiltersOutZeroOrNegativePrices()
    {
        var store = new PriceSnapshotStore();
        var processor = CreateProcessor(store, out _);
        var rawPrices = new List<LatestPriceResponse> { MakePrice("GRAM_ALTIN", bid: 100m, ask: 0m) };

        var result = await processor.ProcessPricesAsync(rawPrices, "Harem", isMarketOpen: true);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessPricesAsync_FiltersOutExpiredAssets_WhenMarketIsOpen()
    {
        var store = new PriceSnapshotStore();
        var processor = CreateProcessor(store, out _);
        var expiredPrice = MakePrice("BTC-USD", bid: 100m, ask: 101m, type: "CRYPTO",
            validUntilUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        var result = await processor.ProcessPricesAsync(new List<LatestPriceResponse> { expiredPrice }, "Binance", isMarketOpen: true);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessPricesAsync_ConvertsCryptoToTry_UsingValidatedUsdRate()
    {
        var store = new PriceSnapshotStore();
        var processor = CreateProcessor(store, out _);
        var rawPrices = new List<LatestPriceResponse>
        {
            MakePrice("USDTRY=X", bid: 40m, ask: 40m, type: "CURRENCY"),
            MakePrice("BTC-USD", bid: 70000m, ask: 70010m, type: "CRYPTO", lastPrice: 70005m)
        };

        var result = await processor.ProcessPricesAsync(rawPrices, "Binance", isMarketOpen: true);

        var btc = result.Should().ContainSingle(p => p.ProductCode == "BTC-USD").Subject;
        btc.IsConvertedToTry.Should().BeTrue();
        btc.Ask.Should().Be(70010m * 40m);
        btc.Bid.Should().Be(70000m * 40m);
    }

    [Fact]
    public async Task ProcessPricesAsync_SkipsCrypto_WhenNoUsdRateIsAvailableYet()
    {
        var store = new PriceSnapshotStore(); // empty snapshot: no cached USDTRY=X either
        var processor = CreateProcessor(store, out _);
        var rawPrices = new List<LatestPriceResponse>
        {
            MakePrice("BTC-USD", bid: 70000m, ask: 70010m, type: "CRYPTO")
        };

        var result = await processor.ProcessPricesAsync(rawPrices, "Binance", isMarketOpen: true);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessPricesAsync_FallsBackToCachedUsdRate_WhenFreshRateFailsFlashCrashShield()
    {
        // Cached snapshot already has a stable USDTRY=X rate from a previous cycle
        var cachedUsd = MakePrice("USDTRY=X", bid: 40m, ask: 40m, type: "CURRENCY");
        var store = new PriceSnapshotStore();
        store.Set(new LatestPricesResponse(Items: new List<LatestPriceResponse> { cachedUsd }, ComputedAtUtc: DateTimeOffset.UtcNow));
        var processor = CreateProcessor(store, out _);

        // Incoming USDTRY=X is a >40% flash-crash relative to the cached rate — the shield rejects it
        var badUsd = MakePrice("USDTRY=X", bid: 10m, ask: 10m, type: "CURRENCY");
        var btc = MakePrice("BTC-USD", bid: 1000m, ask: 1000m, type: "CRYPTO");

        var result = await processor.ProcessPricesAsync(new List<LatestPriceResponse> { badUsd, btc }, "Binance", isMarketOpen: true);

        // BTC should still convert using the last known-good cached USD rate (40), not the rejected one (10)
        var convertedBtc = result.Should().ContainSingle(p => p.ProductCode == "BTC-USD").Subject;
        convertedBtc.Ask.Should().Be(1000m * 40m);
    }

    [Fact]
    public async Task ProcessPricesAsync_RejectsFlashCrash_AgainstExistingSnapshot()
    {
        var existing = MakePrice("GRAM_ALTIN", bid: 7000m, ask: 7000m);
        var store = new PriceSnapshotStore();
        store.Set(new LatestPricesResponse(Items: new List<LatestPriceResponse> { existing }, ComputedAtUtc: DateTimeOffset.UtcNow));
        var processor = CreateProcessor(store, out _);

        // 50% below the existing cached price
        var crashed = MakePrice("GRAM_ALTIN", bid: 3500m, ask: 3500m);

        var result = await processor.ProcessPricesAsync(new List<LatestPriceResponse> { crashed }, "Harem", isMarketOpen: true);

        result.Should().BeEmpty();
    }
}

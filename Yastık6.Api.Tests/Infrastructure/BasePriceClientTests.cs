using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FluentAssertions;
using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Infrastructure.Alerts;
using Yastık6.Api.Infrastructure.Providers.Base;

namespace Yastık6.Api.Tests.Infrastructure.Providers;

public class BasePriceClientTests
{
    private sealed class FakeSystemClock : ISystemClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    }

    // Test double: RefreshInterval is deliberately tiny (15s) so it's obviously different
    // from the mapper's own multi-minute ValidUntilUtc window below — if BasePriceClient
    // ever goes back to overwriting ValidUntilUtc with now+RefreshInterval, this test catches it.
    private sealed class TestPriceClient : BasePriceClient
    {
        private readonly List<LatestPriceResponse> _results;

        public TestPriceClient(List<LatestPriceResponse> results, ISystemClock clock)
            : base(NullLogger.Instance, new AlertTracker(), clock)
        {
            _results = results;
        }

        public override string ProviderName => "TestProvider";
        public override TimeSpan RefreshInterval => TimeSpan.FromSeconds(15);

        protected override Task<List<LatestPriceResponse>> FetchDataAsync(CancellationToken ct)
            => Task.FromResult(_results);
    }

    [Fact]
    public async Task GetMarketOverviewAsync_DoesNotOverwrite_MapperComputedValidUntilUtc()
    {
        // Arrange: mapper decided this asset (e.g. Harem outside trading hours) stays valid
        // for 35 minutes — far outside the provider's own 15s RefreshInterval.
        var mapperComputedValidUntil = new DateTimeOffset(2026, 1, 1, 12, 35, 0, TimeSpan.Zero);
        var mapperPrice = new LatestPriceResponse(
            ProductCode: "GRAM_ALTIN",
            Name: "Gram Altın",
            Type: "METAL",
            Bid: 100m,
            Ask: 101m,
            LastPrice: null,
            ChangeRate: 0m,
            FormattedBid: "100,00",
            FormattedAsk: "101,00",
            AsOfUtc: DateTimeOffset.UtcNow,
            ValidUntilUtc: mapperComputedValidUntil,
            Source: "Harem"
        );

        var client = new TestPriceClient(new List<LatestPriceResponse> { mapperPrice }, new FakeSystemClock());

        // Act
        var results = await client.GetMarketOverviewAsync();

        // Assert
        results.Should().ContainSingle();
        results[0].ValidUntilUtc.Should().Be(mapperComputedValidUntil);
    }
}

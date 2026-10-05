using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using FluentAssertions;
using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Infrastructure.Alerts;
using Yastık6.Api.Infrastructure.BackgroundJobs;
using Yastık6.Api.Infrastructure.Caching;
using Yastık6.Api.Infrastructure.Providers.Base;

namespace Yastık6.Api.Tests.Infrastructure.BackgroundJobs;

public class PriceSnapshotRefresherTests
{
    private static LatestPriceResponse Price(string code, string source) => new(
        ProductCode: code,
        Name: code,
        Type: "FOREX",
        Bid: 40m,
        Ask: 41m,
        LastPrice: null,
        ChangeRate: 0m,
        FormattedBid: "40,00",
        FormattedAsk: "41,00",
        AsOfUtc: DateTimeOffset.UtcNow,
        ValidUntilUtc: DateTimeOffset.UtcNow.AddMinutes(5),
        Source: source
    );

    // PriceSnapshotRefresher fallback'i tip ADIYLA eşliyor ("FreeCurrencyClient" -> "YahooForexClient"),
    // bu yüzden test double'lar gerçek client'larla aynı isimleri taşıyor.
    private sealed class FreeCurrencyClient : IPriceProvider
    {
        public string ProviderName => "FreeCurrencyAPI";
        public bool IsMarketOpen() => true;
        public TimeSpan RefreshInterval => TimeSpan.FromMinutes(10);
        public TimeSpan MarketCloseTime => TimeSpan.Zero;

        public Task<List<LatestPriceResponse>> GetMarketOverviewAsync(CancellationToken ct = default)
            => throw new ProviderFetchException(ProviderName, new HttpRequestException("Connection refused"));
    }

    private sealed class YahooForexClient : IPriceProvider
    {
        public string ProviderName => "YahooForex";
        public bool IsMarketOpen() => true;
        public TimeSpan RefreshInterval => TimeSpan.FromMinutes(10);
        public TimeSpan MarketCloseTime => TimeSpan.Zero;

        public Task<List<LatestPriceResponse>> GetMarketOverviewAsync(CancellationToken ct = default)
            => Task.FromResult(new List<LatestPriceResponse> { Price("USDTRY", "YahooForex") });
    }

    private sealed class FakeSystemClock : ISystemClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }

    [Fact]
    public async Task ProviderFailure_TriggersFallback_AndSendsTelegramAlert()
    {
        // Arrange
        var snapshotStore = new PriceSnapshotStore();

        var processor = new Mock<IPriceProcessor>();
        processor
            .Setup(p => p.ProcessPricesAsync(It.IsAny<List<LatestPriceResponse>>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<LatestPriceResponse> raw, string _, bool _, CancellationToken _) => raw);

        var alertSent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var alerts = new Mock<IAlertService>();
        alerts
            .Setup(a => a.SendAlertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string message, string _, CancellationToken _) => alertSent.TrySetResult(message))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddTransient<IPriceProvider, FreeCurrencyClient>();
        services.AddTransient<IPriceProvider, YahooForexClient>();
        services.AddSingleton<ISystemClock, FakeSystemClock>();
        services.AddSingleton(Mock.Of<IPriceHistoryRepository>());
        using var provider = services.BuildServiceProvider();

        var refresher = new PriceSnapshotRefresher(
            snapshotStore,
            processor.Object,
            NullLogger<PriceSnapshotRefresher>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            alerts.Object);

        // Act
        await refresher.StartAsync(CancellationToken.None);
        var alertMessage = await alertSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Fallback, alarmdan hemen sonra snapshot'ı yazıyor; kısa bir süre bekle.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (snapshotStore.Current == null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await refresher.StopAsync(CancellationToken.None);

        // Assert
        alertMessage.Should().Contain("Fallback Activated").And.Contain("YahooForexClient");
        snapshotStore.Current.Should().NotBeNull();
        snapshotStore.Current!.Items.Should().ContainSingle(p => p.ProductCode == "USDTRY" && p.Source == "YahooForex");
    }
}

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

    // Testin primary provider'ı ne zaman düşürüp ne zaman geri getireceğini kontrol ettiği anahtar.
    private sealed class OutageSwitch
    {
        public volatile bool IsDown = true;
    }

    // PriceSnapshotRefresher fallback'i tip ADIYLA eşliyor ("FreeCurrencyClient" -> "YahooForexClient"),
    // bu yüzden test double'lar gerçek client'larla aynı isimleri taşıyor.
    private sealed class FreeCurrencyClient(OutageSwitch outage) : IPriceProvider
    {
        public string ProviderName => "FreeCurrencyAPI";
        public bool IsMarketOpen() => true;
        public TimeSpan RefreshInterval => TimeSpan.FromMinutes(10);
        public TimeSpan MarketCloseTime => TimeSpan.Zero;

        public Task<List<LatestPriceResponse>> GetMarketOverviewAsync(CancellationToken ct = default)
            => outage.IsDown
                ? throw new ProviderFetchException(ProviderName, new HttpRequestException("Connection refused"))
                : Task.FromResult(new List<LatestPriceResponse> { Price("USDTRY", "FreeCurrencyAPI") });
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

    private sealed record Harness(PriceSnapshotRefresher Refresher, PriceSnapshotStore Store, OutageSwitch Outage, AlertTracker Tracker, Func<int, Task<IReadOnlyList<string>>> WaitForAlerts, ServiceProvider Services);

    private static Harness CreateHarness()
    {
        var snapshotStore = new PriceSnapshotStore();
        var outage = new OutageSwitch();
        var tracker = new AlertTracker();

        var processor = new Mock<IPriceProcessor>();
        processor
            .Setup(p => p.ProcessPricesAsync(It.IsAny<List<LatestPriceResponse>>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<LatestPriceResponse> raw, string _, bool _, CancellationToken _) => raw);

        var sentAlerts = new List<string>();
        var alerts = new Mock<IAlertService>();
        alerts
            .Setup(a => a.SendAlertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string message, string cacheKey, CancellationToken _) =>
            {
                // Gerçek TelegramAlertService gibi: anahtarlı alarmlar tracker'a "aktif" olarak düşer.
                if (!string.IsNullOrEmpty(cacheKey)) tracker.ShouldSendAlert(cacheKey);
                lock (sentAlerts) sentAlerts.Add(message);
            })
            .Returns(Task.CompletedTask);

        async Task<IReadOnlyList<string>> WaitForAlerts(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (sentAlerts) if (sentAlerts.Count >= count) return sentAlerts.ToList();
                await Task.Delay(20);
            }
            lock (sentAlerts) return sentAlerts.ToList();
        }

        var services = new ServiceCollection();
        services.AddSingleton(outage);
        services.AddTransient<IPriceProvider, FreeCurrencyClient>();
        services.AddTransient<IPriceProvider, YahooForexClient>();
        services.AddSingleton<ISystemClock, FakeSystemClock>();
        services.AddSingleton(Mock.Of<IPriceHistoryRepository>());
        var provider = services.BuildServiceProvider();

        var refresher = new PriceSnapshotRefresher(
            snapshotStore,
            processor.Object,
            NullLogger<PriceSnapshotRefresher>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            alerts.Object,
            tracker)
        {
            CrashBackoff = TimeSpan.FromMilliseconds(200)
        };

        return new Harness(refresher, snapshotStore, outage, tracker, WaitForAlerts, provider);
    }

    [Fact]
    public async Task ProviderFailure_TriggersFallback_AndSendsTelegramAlert()
    {
        var h = CreateHarness();

        await h.Refresher.StartAsync(CancellationToken.None);
        var alerts = await h.WaitForAlerts(1);
        await h.Refresher.StopAsync(CancellationToken.None);
        h.Services.Dispose();

        alerts.Should().NotBeEmpty();
        alerts[0].Should().Contain("Fallback Activated").And.Contain("YahooForexClient");
        h.Store.Current.Should().NotBeNull();
        h.Store.Current!.Items.Should().Contain(p => p.ProductCode == "USDTRY" && p.Source == "YahooForex");
    }

    [Fact]
    public async Task PrimaryComingBack_SendsRecoveredAlert_Once_AndClearsOutageAlert()
    {
        var h = CreateHarness();

        await h.Refresher.StartAsync(CancellationToken.None);
        (await h.WaitForAlerts(1)).Should().ContainSingle(a => a.Contains("Fallback Activated"));

        // Primary geri geliyor; bir sonraki denemede "Recovered" bildirimi gitmeli.
        h.Outage.IsDown = false;
        var alerts = await h.WaitForAlerts(2);

        // Recovery yalnızca bir kez bildirilmeli (sonraki başarılı fetch'ler sessiz).
        await Task.Delay(500);
        alerts = await h.WaitForAlerts(2);
        await h.Refresher.StopAsync(CancellationToken.None);
        h.Services.Dispose();

        alerts.Should().HaveCount(2);
        alerts[1].Should().Contain("Provider Recovered").And.Contain("FreeCurrencyClient").And.Contain("back on the primary provider");
        h.Tracker.GetCurrentSnapshot().Active.Should().NotContainKey("Fallback_FreeCurrencyClient_YahooForexClient");
        h.Store.Current!.Items.Should().Contain(p => p.ProductCode == "USDTRY" && p.Source == "FreeCurrencyAPI");
    }

    [Fact]
    public async Task StaleAssetInSnapshot_IsEvicted_AndTelegramAlertListsIt()
    {
        var h = CreateHarness();
        h.Outage.IsDown = false;

        var now = DateTimeOffset.UtcNow;
        h.Store.Set(new LatestPricesResponse(
            Items: new List<LatestPriceResponse>
            {
                // 27 gündür güncellenmeyen, delist olmuş coin
                Price("QUICK-USD", "Binance") with { Type = "CRYPTO", AsOfUtc = now.AddDays(-27), ValidUntilUtc = now.AddDays(-27).AddMinutes(1) },
                // Kısa süre önce dolmuş ama hâlâ canlı sayılması gereken kayıt
                Price("EURTRY", "FreeCurrencyAPI") with { AsOfUtc = now.AddHours(-2), ValidUntilUtc = now.AddHours(-1) },
            },
            ComputedAtUtc: now));

        await h.Refresher.StartAsync(CancellationToken.None);
        var alerts = await h.WaitForAlerts(1);
        await h.Refresher.StopAsync(CancellationToken.None);
        h.Services.Dispose();

        alerts.Should().ContainSingle();
        alerts[0].Should().Contain("Stale Prices Removed").And.Contain("`QUICK-USD`").And.Contain("27d ago").And.NotContain("EURTRY");
        h.Store.Current!.Items.Select(p => p.ProductCode).Should().BeEquivalentTo("EURTRY", "USDTRY");
    }

    [Fact]
    public async Task NothingStale_SendsNoEvictionAlert()
    {
        var h = CreateHarness();
        h.Outage.IsDown = false;

        await h.Refresher.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await h.Refresher.StopAsync(CancellationToken.None);
        h.Services.Dispose();

        (await h.WaitForAlerts(1)).Should().BeEmpty();
        h.Store.Current!.Items.Should().ContainSingle(p => p.ProductCode == "USDTRY");
    }
}

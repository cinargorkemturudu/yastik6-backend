using System.Reflection;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Domain.Entities;
using Yastık6.Api.Infrastructure.Alerts;
using Yastık6.Api.Infrastructure.BackgroundJobs;
using Yastık6.Api.Infrastructure.Persistence;

namespace Yastık6.Api.Tests.Infrastructure.BackgroundJobs;

// Deploy sırasında (docker compose down) stoppingToken iptal edilir; bu bir hata değildir
// ve Watchdog'a "ÇÖKTÜ" alarmı göndermemelidir.
public class ShutdownCancellationTests
{
    [Fact]
    public async Task HistoryCacheWarmer_DoesNotAlert_WhenCancelledByShutdown()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        using (var seedContext = new ApplicationDbContext(options))
        {
            seedContext.Database.EnsureCreated();
            seedContext.TrackedSymbols.Add(new TrackedSymbol { Id = Guid.NewGuid(), Code = "USDTRY=X", SymbolType = "FOREX" });
            seedContext.SaveChanges();
        }

        using var cts = new CancellationTokenSource();
        var priceService = new Mock<IPriceService>();
        priceService
            .Setup(x => x.GetHistoryAsync(It.IsAny<string>(), It.IsAny<PriceHistoryRange>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cts.Cancel(); // Uygulama tam bu sırada kapanıyor.
                throw new TaskCanceledException("A task was canceled.");
            });

        var services = new ServiceCollection();
        services.AddScoped(_ => new ApplicationDbContext(options));
        services.AddSingleton(priceService.Object);
        using var provider = services.BuildServiceProvider();

        var alertService = new Mock<IAlertService>();
        var warmer = new HistoryCacheWarmer(provider, NullLogger<HistoryCacheWarmer>.Instance, alertService.Object);

        var loop = typeof(HistoryCacheWarmer).GetMethod("RunWarmerLoopAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)loop.Invoke(warmer, [PriceHistoryRange.Month, TimeSpan.FromMinutes(110), cts.Token])!;

        await task.WaitAsync(TimeSpan.FromSeconds(10));

        alertService.Verify(
            x => x.SendAlertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DataRetentionWorker_DoesNotAlert_WhenCancelledByShutdown()
    {
        using var cts = new CancellationTokenSource();

        var repository = new Mock<IPriceHistoryRepository>();
        repository
            .Setup(x => x.GetPricesBetweenAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cts.Cancel();
                throw new TaskCanceledException("A task was canceled.");
            });

        var clock = new Mock<ISystemClock>();
        clock.Setup(x => x.UtcNow).Returns(DateTime.UtcNow);

        var services = new ServiceCollection();
        services.AddSingleton(repository.Object);
        services.AddSingleton(clock.Object);
        using var provider = services.BuildServiceProvider();

        var alertService = new Mock<IAlertService>();
        var worker = new DataRetentionWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DataRetentionWorker>.Instance,
            alertService.Object);

        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        alertService.Verify(
            x => x.SendAlertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HistoryCacheWarmer_StillAlerts_OnRealFailure()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        using (var seedContext = new ApplicationDbContext(options))
        {
            seedContext.Database.EnsureCreated();
            seedContext.TrackedSymbols.Add(new TrackedSymbol { Id = Guid.NewGuid(), Code = "USDTRY=X", SymbolType = "FOREX" });
            seedContext.SaveChanges();
        }

        using var cts = new CancellationTokenSource();
        var priceService = new Mock<IPriceService>();
        priceService
            .Setup(x => x.GetHistoryAsync(It.IsAny<string>(), It.IsAny<PriceHistoryRange>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var services = new ServiceCollection();
        services.AddScoped(_ => new ApplicationDbContext(options));
        services.AddSingleton(priceService.Object);
        using var provider = services.BuildServiceProvider();

        var alertService = new Mock<IAlertService>();
        alertService
            .Setup(x => x.SendAlertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel()) // Alarm geldikten sonra döngüyü bitir.
            .Returns(Task.CompletedTask);

        var warmer = new HistoryCacheWarmer(provider, NullLogger<HistoryCacheWarmer>.Instance, alertService.Object);

        var loop = typeof(HistoryCacheWarmer).GetMethod("RunWarmerLoopAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)loop.Invoke(warmer, [PriceHistoryRange.Month, TimeSpan.FromMinutes(110), cts.Token])!;

        await task.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { }); // Task.Delay iptalde fırlatabilir.

        alertService.Verify(
            x => x.SendAlertAsync(It.Is<string>(m => m.Contains("CACHE WARMER")), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}

using FluentAssertions;
using Moq;
using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Domain.Entities;
using Yastık6.Api.Infrastructure.Caching;

namespace Yastık6.Api.Tests.Application.Features;

public class PriceServiceTests
{
    private readonly Mock<IPriceHistoryRepository> _mockRepo;
    private readonly Mock<ISystemClock> _mockClock;
    private readonly Mock<IPriceSnapshotStore> _mockSnapshotStore; // [EKLENDİ]
    
    private readonly DateTime _fixedTimeUtc = new(2026, 3, 13, 12, 0, 0, DateTimeKind.Utc);
    private readonly DateTimeOffset _fixedTimeOffset = new(2026, 3, 13, 12, 0, 0, TimeSpan.Zero);

    public PriceServiceTests()
    {
        _mockRepo = new Mock<IPriceHistoryRepository>();
        _mockClock = new Mock<ISystemClock>();
        _mockSnapshotStore = new Mock<IPriceSnapshotStore>(); // [EKLENDİ]

        _mockClock.Setup(c => c.UtcNow).Returns(_fixedTimeUtc);
    }

    [Fact]
    public async Task GetMarketOverviewAsync_ShouldReturnDataFromSnapshotStore()
    {
        // Arrange
        var mockSnapshot = new LatestPricesResponse(new List<LatestPriceResponse> 
        { 
            new("XAU", "Altın", "METAL", 100m, 105m, 0, "100", "105", _fixedTimeOffset, "Harem") 
        });

        // Store'dan veri istendiğinde bizim sahte nesneyi dön
        _mockSnapshotStore.Setup(s => s.Current).Returns(mockSnapshot);

        var service = new PriceService(_mockSnapshotStore.Object, _mockRepo.Object, _mockClock.Object);

        // Act
        var result = await service.GetMarketOverviewAsync();

        // Assert
        result.Should().NotBeNull();
        result.Items.Should().HaveCount(1); 
        result.Items[0].ProductCode.Should().Be("XAU");
        
        // RAM'deki Store'un çağrıldığını (API'ye gidilmediğini) kanıtlıyoruz
        _mockSnapshotStore.Verify(s => s.Current, Times.Once);
    }

    [Fact]
    public async Task GetHistoryAsync_WithDayRange_ShouldGroupInto15MinuteBucketsAndAveragePrices()
    {
        // Arrange
        var productCode = "USD_TRY";
        var expectedSince = new DateTimeOffset(_fixedTimeUtc).AddDays(-1); 
        
        var mockRecords = new List<PriceRecord>
        {
            new() { AskPrice = 30.00m, TimestampUtc = _fixedTimeOffset.AddMinutes(2) },
            new() { AskPrice = 32.00m, TimestampUtc = _fixedTimeOffset.AddMinutes(10) }, 
            new() { AskPrice = 40.00m, TimestampUtc = _fixedTimeOffset.AddMinutes(20) } 
        };

        _mockRepo.Setup(r => r.GetHistoricalPricesAsync(productCode, expectedSince, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockRecords);

        // [GÜNCELLENDİ] Provider yerine SnapshotStore verildi
        var service = new PriceService(_mockSnapshotStore.Object, _mockRepo.Object, _mockClock.Object);

        // Act
        var result = await service.GetHistoryAsync(productCode, PriceHistoryRange.Day);

        // Assert
        result.Points.Should().HaveCount(2);
        result.Points[0].Price.Should().Be(31.00m); 
        result.Points[1].Price.Should().Be(40.00m);
    }

    [Fact]
    public async Task GetHistoryAsync_WithInvalidRange_ShouldThrowArgumentOutOfRangeException()
    {
        // Arrange
        var invalidRange = (PriceHistoryRange)999;
        var service = new PriceService(_mockSnapshotStore.Object, _mockRepo.Object, _mockClock.Object);

        // Act
        Func<Task> act = async () => await service.GetHistoryAsync("XAU", invalidRange);

        // Assert
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
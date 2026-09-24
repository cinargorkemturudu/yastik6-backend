using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using FluentAssertions;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Infrastructure.Alerts;

namespace Yastık6.Api.Tests.Application.Features;

public class PriceShieldValidatorTests
{
    private static LatestPriceResponse MakePrice(string productCode, decimal bid, decimal ask, string source = "Harem")
        => new(
            ProductCode: productCode,
            Name: productCode,
            Type: "METAL",
            Bid: bid,
            Ask: ask,
            LastPrice: null,
            ChangeRate: 0m,
            FormattedBid: bid.ToString(),
            FormattedAsk: ask.ToString(),
            AsOfUtc: DateTimeOffset.UtcNow,
            ValidUntilUtc: DateTimeOffset.UtcNow.AddMinutes(5),
            Source: source
        );

    private static PriceShieldValidator CreateValidator(out Mock<IAlertService> alertServiceMock)
    {
        alertServiceMock = new Mock<IAlertService>();
        return new PriceShieldValidator(
            NullLogger<PriceShieldValidator>.Instance,
            alertServiceMock.Object,
            new AlertTracker());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task ValidatePriceAsync_RejectsZeroOrNegativeAsk(decimal badAsk)
    {
        var validator = CreateValidator(out var alertServiceMock);
        var price = MakePrice("GRAM_ALTIN", bid: 100m, ask: badAsk);

        var result = await validator.ValidatePriceAsync(price, existingPrice: null, ct: default);

        result.Should().BeFalse();
        alertServiceMock.Verify(a => a.SendAlertAsync(
            It.IsAny<string>(), "ZeroPrice_GRAM_ALTIN", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ValidatePriceAsync_AcceptsValidPrice_WithNoExistingPrice()
    {
        var validator = CreateValidator(out var alertServiceMock);
        var price = MakePrice("GRAM_ALTIN", bid: 100m, ask: 101m);

        var result = await validator.ValidatePriceAsync(price, existingPrice: null, ct: default);

        result.Should().BeTrue();
        alertServiceMock.Verify(a => a.SendAlertAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ValidatePriceAsync_RejectsFlashCrash_WhenDeviationExceeds40Percent()
    {
        var validator = CreateValidator(out var alertServiceMock);
        var existingPrice = MakePrice("GRAM_ALTIN", bid: 100m, ask: 100m);
        // 41% below the existing Ask
        var crashedPrice = MakePrice("GRAM_ALTIN", bid: 59m, ask: 59m);

        var result = await validator.ValidatePriceAsync(crashedPrice, existingPrice, ct: default);

        result.Should().BeFalse();
        alertServiceMock.Verify(a => a.SendAlertAsync(
            It.IsAny<string>(), "FlashCrash_GRAM_ALTIN", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ValidatePriceAsync_AcceptsPrice_AtExactly40PercentDeviation()
    {
        var validator = CreateValidator(out var alertServiceMock);
        var existingPrice = MakePrice("GRAM_ALTIN", bid: 100m, ask: 100m);
        // Exactly 40% below — boundary is "> 40", so this must still pass
        var borderlinePrice = MakePrice("GRAM_ALTIN", bid: 60m, ask: 60m);

        var result = await validator.ValidatePriceAsync(borderlinePrice, existingPrice, ct: default);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ValidatePriceAsync_DoesNotSpamAlerts_ForRepeatedZeroPriceWithinSilenceWindow()
    {
        var validator = CreateValidator(out var alertServiceMock);
        var price = MakePrice("GRAM_ALTIN", bid: 100m, ask: 0m);

        await validator.ValidatePriceAsync(price, existingPrice: null, ct: default);
        await validator.ValidatePriceAsync(price, existingPrice: null, ct: default);

        // AlertTracker silences repeat alerts for the same cacheKey within its 1-hour window
        alertServiceMock.Verify(a => a.SendAlertAsync(
            It.IsAny<string>(), "ZeroPrice_GRAM_ALTIN", It.IsAny<CancellationToken>()), Times.Once);
    }
}

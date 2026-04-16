using Xunit;
using Yastık6.Api.Infrastructure.Providers.Harem;
using FluentAssertions;

namespace Yastık6.Api.Tests.Infrastructure.Mappers;

public class HaremDataMapperTests
{
    [Theory]
    [InlineData("GRAM ALTIN", "Gram Altın", "METAL")]
    [InlineData("YENİ ÇEYREK", "Çeyrek Altın (Yeni)", "METAL")]
    [InlineData("ONS", "Ons Altın ($)", "INDICATOR")]
    [InlineData("22 AYAR", "22 Ayar Altın (Gram)", "METAL")]
    public void MapToResponse_ValidAssets_ShouldMapCorrectively(string key, string expectedName, string expectedType)
    {
        // Arrange
        var rawItem = new HaremClient.HaremItemRaw 
        { 
            Key = key, Buy = "7.175,30", Sell = "7.285,76", LastUpdate = "14.03.2026 20:32:59" 
        };

        // Act
        var result = HaremDataMapper.MapToResponse(rawItem);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be(expectedName);
        result.Type.Should().Be(expectedType);
        result.ProductCode.Should().Be(key.Replace(" ", "_"));
    }

    [Theory]
    [InlineData("EUR/KG")]
    [InlineData("ALTIN GÜMÜŞ")]
    [InlineData("PLATİN USD")]
    [InlineData("ÇÖP VERİ")]
    public void MapToResponse_UnwantedAssets_ShouldReturnNull(string key)
    {
        // Arrange
        var rawItem = new HaremClient.HaremItemRaw { Key = key, Buy = "100", Sell = "110" };

        // Act
        var result = HaremDataMapper.MapToResponse(rawItem);

        // Assert
        result.Should().BeNull();
    }
}
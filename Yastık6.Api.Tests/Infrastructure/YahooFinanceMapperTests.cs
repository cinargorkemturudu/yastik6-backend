using Xunit;
using Yastık6.Api.Infrastructure.Providers.YahooFinance;
using YahooFinanceApi;
using FluentAssertions;
using System.Reflection;

namespace Yastık6.Api.Tests.Infrastructure.Mappers;

public class YahooFinanceMapperTests
{
    // Kütüphanedeki internal constructor'ı aşmak için yardımcı metod
    private Security CreateSecurity(string shortName, decimal price)
    {
        var fields = new Dictionary<string, object>
        {
            { "ShortName", shortName },
            // DİKKAT: Yahoo kütüphanesi içerde 'double' beklediği için cast ediyoruz
            { "RegularMarketPrice", (double)price } 
        };

        // Reflection kullanarak internal constructor'ı çağırıyoruz
        var constructor = typeof(Security).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).First();
        return (Security)constructor.Invoke(new object[] { fields });
    }

    [Fact]
    public void MapToResponse_Currency_ShouldMapToCleanNameAndType()
    {
        // Arrange
        var symbol = "USDTRY=X";
        var security = CreateSecurity("USD/TRY", 44.02m);

        // Act
        var result = YahooFinanceMapper.MapToResponse(symbol, security, "FOREX", "YahooGlobal");

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Amerikan Doları");
        result.Type.Should().Be("CURRENCY");
    }

    [Fact]
    public void MapToResponse_Stock_ShouldBeInTitleCase()
    {
        // Arrange
        var symbol = "THYAO.IS";
        var security = CreateSecurity("TURK HAVA YOLLARI", 292.25m);

        // Act
        var result = YahooFinanceMapper.MapToResponse(symbol, security, "BIST", "YahooBist");

        // Assert
        result.Should().NotBeNull();
        // TitleCase check: TURK HAVA YOLLARI -> Turk Hava Yollari
        result!.Name.Should().Be("Turk Hava Yollari"); 
        result.Type.Should().Be("STOCK");
    }

    [Fact]
    public void MapToResponse_Crypto_ShouldMapCorrectType()
    {
        // Arrange
        var symbol = "BTC-USD";
        var security = CreateSecurity("Bitcoin USD", 70000m);

        // Act
        var result = YahooFinanceMapper.MapToResponse(symbol, security, "CRYPTO", "YahooGlobal");

        // Assert
        result.Should().NotBeNull();
        result!.Type.Should().Be("CRYPTO");
        result.Name.Should().Be("Bitcoin");
    }

    [Fact]
    public void MapToResponse_InvalidYahooData_ShouldReturnNull()
    {
        // Arrange
        var symbol = "XAU=F";
        var security = CreateSecurity("Invalid", 0m);

        // Act
        var result = YahooFinanceMapper.MapToResponse(symbol, security, "FOREX", "Yahoo");

        // Assert
        result.Should().BeNull();
    }
}
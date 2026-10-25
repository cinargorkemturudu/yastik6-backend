using FluentValidation.TestHelper;
using Yastık6.Api.Application.Features.Prices;

namespace Yastık6.Api.Tests.Application.Features;

public class GetHistoryValidatorTests
{
    private readonly GetHistoryValidator _validator;

    public GetHistoryValidatorTests()
    {
        _validator = new GetHistoryValidator();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    // [GÜNCELLENDİ]: string yerine string? kullanarak null gelebileceğini belirttik
    public void Should_HaveError_When_ProductCode_IsEmpty(string? invalidCode)
    {
        // Act - invalidCode! kullanarak derleyicinin "bu null olabilir" uyarısını eziyoruz 
        // (Zaten Validator'ın bu null durumu yakalamasını test ediyoruz)
        var result = _validator.TestValidate((productCode: invalidCode!, range: PriceHistoryRange.Day));

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.productCode)
            .WithErrorMessage("Product code is required.");
    }

    [Fact]
    public void Should_HaveError_When_ProductCode_ExceedsMaxLength()
    {
        // Arrange
        var longCode = new string('A', 21);

        // Act - [GÜNCELLENDİ]: Tuple isimleri eklendi.
        var result = _validator.TestValidate((productCode: longCode, range: PriceHistoryRange.Day));

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.productCode)
              .WithErrorMessage("Product code must be 20 characters or fewer.");
    }

    [Theory]
    [InlineData("usd-try")] 
    [InlineData("USD TRY")] 
    [InlineData("USD/TRY")] 
    [InlineData("USDTRY!")] 
    public void Should_HaveError_When_ProductCode_HasInvalidFormat(string invalidCode)
    {
        // Act - [GÜNCELLENDİ]: Tuple isimleri eklendi.
        var result = _validator.TestValidate((productCode: invalidCode, range: PriceHistoryRange.Day));

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.productCode)
              .WithErrorMessage("Product code may only contain A-Z, 0-9 and _ - . =");
    }

    [Fact]
    public void Should_HaveError_When_Range_IsInvalid()
    {
        // Arrange 
        var invalidRange = (PriceHistoryRange)999;

        // Act - [GÜNCELLENDİ]: Tuple isimleri eklendi.
        var result = _validator.TestValidate((productCode: "USD_TRY", range: invalidRange));

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.range)
              .WithErrorMessage("Invalid range.");
    }

    [Theory]
    [InlineData("USD_TRY", PriceHistoryRange.Day)]
    [InlineData("XAU", PriceHistoryRange.Year)]
    [InlineData("BIST100", PriceHistoryRange.Month)]
    [InlineData("GRAM_ALTIN_1", PriceHistoryRange.FiveYears)] 
    public void Should_NotHaveError_When_Inputs_AreValid(string validCode, PriceHistoryRange validRange)
    {
        // Act - [GÜNCELLENDİ]: Tuple isimleri eklendi.
        var result = _validator.TestValidate((productCode: validCode, range: validRange));

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }
}
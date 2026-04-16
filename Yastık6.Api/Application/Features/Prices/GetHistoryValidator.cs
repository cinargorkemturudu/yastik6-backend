using FluentValidation;

namespace Yastık6.Api.Application.Features.Prices;

public sealed class GetHistoryValidator : AbstractValidator<(string productCode, PriceHistoryRange range)>
{
    public GetHistoryValidator()
    {
        RuleFor(x => x.productCode)
            .NotEmpty().WithMessage("Ürün kodu boş olamaz.")
            .MaximumLength(20).WithMessage("Ürün kodu 20 karakterden uzun olamaz.")
            // [DÜZELTİLDİ]: Yahoo Finance sembollerindeki . - ve = işaretlerine izin verildi
            .Matches(@"^[A-Za-z0-9_\-\.\=]+$").WithMessage("Ürün kodu geçersiz formatta.");

        RuleFor(x => x.range)
            .IsInEnum().WithMessage("Geçersiz zaman aralığı.");
    }
}
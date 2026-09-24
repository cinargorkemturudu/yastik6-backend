using FluentValidation;

namespace Yastık6.Api.Application.Features.Prices;

public sealed class GetHistoryValidator : AbstractValidator<(string productCode, PriceHistoryRange range)>
{
    public GetHistoryValidator()
    {
        RuleFor(x => x.productCode)
            // CascadeMode.Stop: Eğer NotEmpty'de patlarsa, boşuna Regex veya Length kontrolü yapıp CPU harcamaz.
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Ürün kodu boş olamaz.")
            .MaximumLength(20).WithMessage("Ürün kodu 20 karakterden uzun olamaz.")
            // Sadece büyük harf, rakam ve finansal semboller (. - = _) — ürün kodları her zaman büyük harf
            .Matches(@"^[A-Z0-9_\-\.\=]+$").WithMessage("Ürün kodu geçersiz formatta.");

        RuleFor(x => x.range)
            .IsInEnum().WithMessage("Geçersiz zaman aralığı.");
    }
}

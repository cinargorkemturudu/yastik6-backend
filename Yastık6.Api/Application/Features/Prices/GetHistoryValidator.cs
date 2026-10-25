using FluentValidation;

namespace Yastık6.Api.Application.Features.Prices;

public sealed class GetHistoryValidator : AbstractValidator<(string productCode, PriceHistoryRange range)>
{
    public GetHistoryValidator()
    {
        RuleFor(x => x.productCode)
            // CascadeMode.Stop: Eğer NotEmpty'de patlarsa, boşuna Regex veya Length kontrolü yapıp CPU harcamaz.
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Product code is required.")
            .MaximumLength(20).WithMessage("Product code must be 20 characters or fewer.")
            // Sadece büyük harf, rakam ve finansal semboller (. - = _) — ürün kodları her zaman büyük harf
            .Matches(@"^[A-Z0-9_\-\.\=]+$").WithMessage("Product code may only contain A-Z, 0-9 and _ - . =");

        RuleFor(x => x.range)
            .IsInEnum().WithMessage("Invalid range.");
    }
}

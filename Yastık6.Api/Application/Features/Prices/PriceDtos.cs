namespace Yastık6.Api.Application.Features.Prices;

public enum PriceHistoryRange
{
    Day,
    Week,
    Month,
    ThreeMonths,
    SixMonths,
    Year,
    FiveYears,
    All
}

public sealed record PricePointDto(
    DateTimeOffset TimestampUtc,
    decimal Price
);

public sealed record LatestPriceResponse(
    string ProductCode,
    string Name, 
    string Type, 
    decimal Bid,
    decimal Ask,
    decimal ChangeRate,
    string FormattedBid, 
    string FormattedAsk, 
    DateTimeOffset AsOfUtc,
    DateTimeOffset ValidUntilUtc, // <--- İŞTE YENİ EKLENEN SİHİRLİ ALAN BURASI
    string Source
);

public sealed record PriceHistoryResponse(
    string ProductCode,
    PriceHistoryRange Range,
    IReadOnlyList<PricePointDto> Points
);
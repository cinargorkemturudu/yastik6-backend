namespace Yastık6.Api.Infrastructure.Providers.Harem;

public sealed record HaremLatestRaw
{
    public string Code { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public DateTime Timestamp { get; init; }
}

public sealed record HaremHistoryPointRaw
{
    public DateTime Timestamp { get; init; }
    public decimal Price { get; init; }
}

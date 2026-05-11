using System.Text.Json.Serialization;

namespace Yastık6.Api.Infrastructure.Providers.Binance;

// Binance'in /api/v3/ticker/bookTicker endpoint'inden dönecek raw veriyi temsil eder
public sealed record BinanceBookTickerRaw
{
    [JsonPropertyName("symbol")] 
    public string Symbol { get; init; } = string.Empty;

    [JsonPropertyName("bidPrice")] 
    public string BidPrice { get; init; } = "0";

    [JsonPropertyName("askPrice")] 
    public string AskPrice { get; init; } = "0";
}

public sealed record BinanceTicker24HrRaw
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    [JsonPropertyName("lastPrice")]
    public string LastPrice { get; init; } = "0";

    [JsonPropertyName("priceChangePercent")]
    public string PriceChangePercent { get; init; } = "0";
}
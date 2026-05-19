using System.Globalization;
using Yastık6.Api.Application.Features.Prices;

namespace Yastık6.Api.Infrastructure.Providers.Binance;

public static class BinanceMapper
{
    // Dictionary mapping Binance symbols to our internal product codes and display names
    private static readonly Dictionary<string, (string TargetCode, string PrettyName)> AllowedAssets = new()
    {
        { "BTCUSDT", ("BTC-USD", "Bitcoin") },
        { "ETHUSDT", ("ETH-USD", "Ethereum") },
        { "SOLUSDT", ("SOL-USD", "Solana") },
        { "BNBUSDT", ("BNB-USD", "Binance Coin") },
        { "XRPUSDT", ("XRP-USD", "Ripple") },
        { "ADAUSDT", ("ADA-USD", "Cardano") },
        { "AVAXUSDT", ("AVAX-USD", "Avalanche") },
        { "DOGEUSDT", ("DOGE-USD", "Dogecoin") },
        { "DOTUSDT", ("DOT-USD", "Polkadot") },
        { "LINKUSDT", ("LINK-USD", "Chainlink") },
        { "MATICUSDT", ("MATIC-USD", "Polygon") },
        { "SHIBUSDT", ("SHIB-USD", "Shiba Inu") },
        { "TRXUSDT", ("TRX-USD", "Tron") },
        { "LTCUSDT", ("LTC-USD", "Litecoin") },
        { "UNIUSDT", ("UNI-USD", "Uniswap") },
        { "ATOMUSDT", ("ATOM-USD", "Cosmos") },
        { "NEARUSDT", ("NEAR-USD", "NEAR Protocol") },
        { "APTUSDT", ("APT-USD", "Aptos") },
        { "INJUSDT", ("INJ-USD", "Injective") },
        { "OPUSDT", ("OP-USD", "Optimism") },
        { "ARBUSDT", ("ARB-USD", "Arbitrum") }
    };

    /// <summary>
    /// Maps the raw Binance book ticker and 24-hour statistics to the standard LatestPriceResponse DTO.
    /// </summary>
    public static LatestPriceResponse? MapToResponse(BinanceBookTickerRaw item, BinanceTicker24HrRaw? stat24h)
    {
        // 1. Symbol Filtering: Check against the predefined VIP list first
        if (!AllowedAssets.TryGetValue(item.Symbol, out var assetInfo))
        {
            // Dynamic fallback: If the symbol ends with "USDT" and is longer than 4 characters
            if (item.Symbol.EndsWith("USDT") && item.Symbol.Length > 4)
            {
                var baseCoin = item.Symbol.Substring(0, item.Symbol.Length - 4); 
                assetInfo = ($"{baseCoin}-USD", baseCoin); 
            }
            else
            {
                return null; // Ignore non-USDT pairs or invalid symbols
            }
        }

        // 2. Price Parsing: Convert string prices to decimal
        if (!TryParseCryptoPrice(item.BidPrice, out var bid) || 
            !TryParseCryptoPrice(item.AskPrice, out var ask))
        {
            return null;
        }

        if (bid <= 0 || ask <= 0) return null;

        var asOfUtc = DateTimeOffset.UtcNow;
        // Crypto markets are 24/7; set a short validity period (1 minute) for high volatility
        var validUntilUtc = asOfUtc.AddMinutes(1);

        // 3. Last Price Calculation: Use the mid-price of the bid-ask spread as the last price
        // This is standard practice for centralized exchanges with very tight spreads
        var lastPrice = (bid + ask) / 2.0m;

        // 4. Change Rate: Use the 24-hour API fallback rate if available, otherwise default to 0
        // This value acts as a fallback for the PriceProcessor if the database has no prior closing record
        decimal changeRate = 0m;
        if (stat24h != null && decimal.TryParse(stat24h.PriceChangePercent, NumberStyles.Any, CultureInfo.InvariantCulture, out var apiRate))
        {
            changeRate = apiRate;
        }

        return new LatestPriceResponse(
            ProductCode: assetInfo.TargetCode,
            Name: assetInfo.PrettyName,
            Type: "CRYPTO",
            Bid: bid,
            Ask: ask,
            LastPrice: lastPrice, 
            ChangeRate: changeRate, 
            FormattedBid: bid.ToString("N2", CultureInfo.InvariantCulture),
            FormattedAsk: ask.ToString("N2", CultureInfo.InvariantCulture),
            AsOfUtc: asOfUtc,
            ValidUntilUtc: validUntilUtc,
            Source: "Binance"
        );
    }

    private static bool TryParseCryptoPrice(string? raw, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}
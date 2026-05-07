using System.Globalization;
using Yastık6.Api.Application.Features.Prices;

namespace Yastık6.Api.Infrastructure.Providers.Binance;

public static class BinanceMapper
{
    // Binance sembollerini veritabanı sembollerine ve arayüzde görünecek isimlere eşleyen sözlük
    private static readonly Dictionary<string, (string TargetCode, string PrettyName)> AllowedAssets = new()
    {
        { "BTCUSDT", ("BTC-USD", "Bitcoin") },
        { "ETHUSDT", ("ETH-USD", "Ethereum") },
        { "SOLUSDT", ("SOL-USD", "Solana") },
        { "BNBUSDT", ("BNB-USD", "Binance Coin") },
        { "XRPUSDT", ("XRP-USD", "Ripple") },
        { "ADAUSDT", ("ADA-USD", "Cardano") },
        { "AVAXUSDT", ("AVAX-USD", "Avalanche") }
    };

    public static LatestPriceResponse? MapToResponse(BinanceBookTickerRaw item)
    {
        // 1. Sembol Filtresi: Listemizde olmayan bir coin geldiyse nazikçe atla
        if (!AllowedAssets.TryGetValue(item.Symbol, out var assetInfo))
        {
            return null;
        }

        // 2. Fiyat Parse: String fiyatları decimal'e çevir
        if (!TryParseCryptoPrice(item.BidPrice, out var bid) || 
            !TryParseCryptoPrice(item.AskPrice, out var ask))
        {
            return null;
        }

        if (bid <= 0 || ask <= 0) return null;

        var asOfUtc = DateTimeOffset.UtcNow;
        // Kripto pazarı 7/24 açık olduğu için fiyat geçerlilik süresini 1 dakika veriyoruz
        var validUntilUtc = asOfUtc.AddMinutes(1);

        // 3. Last Price: Sektör standardı gereği Binance gibi merkezi borsalarda 
        // son işlem fiyatı yerine makas çok dar olduğu için (Bid+Ask)/2 orta fiyatını LastPrice olarak atıyoruz.
        // Bu değer DTO'dan PriceHistoryRepository'ye geçecek.
        var lastPrice = (bid + ask) / 2.0m;

        return new LatestPriceResponse(
            ProductCode: assetInfo.TargetCode,
            Name: assetInfo.PrettyName,
            Type: "CRYPTO",
            Bid: bid,
            Ask: ask,
            LastPrice: lastPrice, // Yeni eklenen alan!
            ChangeRate: 0m,       // ChangeRate, PriceSnapshotRefresher tarafından ezilip hesaplanacak
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
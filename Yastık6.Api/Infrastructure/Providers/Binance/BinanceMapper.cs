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

    public static LatestPriceResponse? MapToResponse(BinanceBookTickerRaw item)
    {
        // 1. Sembol Filtresi: Önce VIP listesine bak (Kurumsal isimler için)
        if (!AllowedAssets.TryGetValue(item.Symbol, out var assetInfo))
        {
            // [ÖĞRETİCİ NOT]: TryGetValue false dönerse coin VIP listede yoktur.
            // Eskiden burada direkt "return null;" vardı. Şimdi dinamik kalkanı devreye sokuyoruz.
            
            // Eğer sembol "USDT" ile bitiyorsa (ve sadece "USDT" değilse, yani boyu 4'ten büyükse)
            if (item.Symbol.EndsWith("USDT") && item.Symbol.Length > 4)
            {
                // Substring(0, Uzunluk - 4): "PEPEUSDT" stringinin baştan başla, son 4 harfini (USDT) kes at. Geriye "PEPE" kalır.
                var baseCoin = item.Symbol.Substring(0, item.Symbol.Length - 4); 
                
                // assetInfo değişkenini dinamik olarak dolduruyoruz. (Code: PEPE-USD, Name: PEPE)
                assetInfo = ($"{baseCoin}-USD", baseCoin); 
            }
            else
            {
                return null; 
            }
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
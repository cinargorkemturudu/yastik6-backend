using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Yastık6.Api.Domain.Entities;

namespace Yastık6.Api.Infrastructure.Persistence;

public static class DbSeeder
{
    public static async Task SeedSymbolsAsync(ApplicationDbContext context, ILogger logger)
    {
        // Önce veritabanındaki mevcut sembolleri alıyoruz ki aynılarını tekrar eklemeyelim
        var existingCodes = await context.TrackedSymbols.Select(x => x.Code).ToListAsync();
        var newSymbols = new List<TrackedSymbol>();

        // 1. FOREX VE KRİPTO PARİTELERİ (Sabit ve Majör Liste)
        var forexAndCrypto = new[]
        {
            "USDTRY=X", "EURTRY=X", "GBPTRY=X", "CHFTRY=X", "JPYTRY=X",
            "AUDTRY=X", "CADTRY=X", "CNYTRY=X", "RUBTRY=X", "XAU=F",
            "XAG=F", "BTC-USD", "ETH-USD"
        };

        foreach (var code in forexAndCrypto)
        {
            if (!existingCodes.Contains(code))
            {
                newSymbols.Add(new TrackedSymbol
                {
                    Id = Guid.NewGuid(),
                    Code = code,
                    SymbolType = code.Contains('=') || code.Contains("=F") ? "FOREX" : "CRYPTO",
                    IsActive = true
                });
            }
        }

        // 2. BİST HİSSELERİNİ GITHUB'DAN OTOMATİK ÇEK
        // [YENİ] Thundering Herd Fix: Eğer veritabanında halihazırda BİST hisseleri varsa, 
        // GitHub'a gereksiz HTTP isteği atmaktan kaçınıyoruz. Bu, uygulamanın anında açılmasını sağlar.
        bool hasBistStocks = existingCodes.Any(c => c.EndsWith(".IS"));

        if (!hasBistStocks)
        {
            try
            {
                using var httpClient = new HttpClient();
                var response = await httpClient.GetStringAsync("https://raw.githubusercontent.com/ahmeterenodaci/Istanbul-Stock-Exchange--BIST--including-symbols-and-logos/main/without_logo.json");

                var bistStocks = JsonSerializer.Deserialize<List<BistRepoItem>>(response);

                if (bistStocks != null)
                {
                    foreach (var stock in bistStocks)
                    {
                        var yahooCode = $"{stock.Symbol}.IS";

                        if (!existingCodes.Contains(yahooCode) && !newSymbols.Any(x => x.Code == yahooCode))
                        {
                            newSymbols.Add(new TrackedSymbol
                            {
                                Id = Guid.NewGuid(),
                                Code = yahooCode,
                                SymbolType = "BIST",
                                IsActive = true
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch BIST stocks from GitHub. Only Forex pairs will be added.");
            }
        }

        // 3. EKSİKLERİ VERİTABANINA BAS
        if (newSymbols.Any())
        {
            await context.TrackedSymbols.AddRangeAsync(newSymbols);
            await context.SaveChangesAsync();
            logger.LogInformation("Success: {Count} new BIST and Forex symbols added to the database.", newSymbols.Count);
        }
    }

    private class BistRepoItem
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = "";
    }
}

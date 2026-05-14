using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Domain.Entities;
using Yastık6.Api.Infrastructure.Caching;
using Yastık6.Api.Infrastructure.Alerts;
using Yastık6.Api.Infrastructure.Providers.Binance;

namespace Yastık6.Api.Infrastructure.BackgroundJobs;

/// <summary>
/// Background service responsible for continuously fetching market prices from various providers,
/// applying safety shields (Zero-Price, Flash Crash), converting foreign assets to local currency (BFF pattern),
/// and maintaining both the in-memory cache and the historical database records.
/// </summary>
public sealed class PriceSnapshotRefresher : BackgroundService
{
    private readonly IPriceSnapshotStore _snapshotStore;
    private readonly IPreviousCloseStore _previousCloseStore;
    private readonly ILogger<PriceSnapshotRefresher> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAlertService _alertService;
    
    // Anlık fiyat güncellemelerinin çakışmasını önleyen kilit
    private static readonly object CacheLock = new object();
    
    // Sistemi ilk açılışta veritabanına yazdığımız coinleri hatırlaması için kilit (Toplu Can Suyu için)
    private static readonly HashSet<string> _seededSymbols = new();

    public PriceSnapshotRefresher(
        IPriceSnapshotStore snapshotStore,
        IPreviousCloseStore previousCloseStore,
        ILogger<PriceSnapshotRefresher> logger,
        IServiceScopeFactory scopeFactory,
        IAlertService alertService)
    {
        _snapshotStore = snapshotStore;
        _previousCloseStore = previousCloseStore;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _alertService = alertService;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        List<Type> providerTypes;

        using (var scope = _scopeFactory.CreateScope())
        {
            var providers = scope.ServiceProvider.GetServices<IPriceProvider>();
            providerTypes = providers.Select(p => p.GetType()).ToList();
            
            _logger.LogInformation("Price refresh service started. Discovered providers: {Count}", providerTypes.Count);
        }

        // Her fiyat sağlayıcısı (Binance, Yahoo, Harem) için ayrı bir asenkron görev başlat
        var providerTasks = providerTypes.Select(type => RunProviderLoopAsync(type, stoppingToken));

        return Task.WhenAll(providerTasks);
    }

    private async Task RunProviderLoopAsync(Type providerType, CancellationToken stoppingToken)
    {
        bool isFirstRun = true; 

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                
                var provider = scope.ServiceProvider.GetServices<IPriceProvider>()
                                    .FirstOrDefault(p => p.GetType() == providerType);

                if (provider == null)
                {
                    _logger.LogWarning("Provider {ProviderType} could not be resolved. Skipping iteration.", providerType.Name);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();
                var repository = scope.ServiceProvider.GetRequiredService<IPriceHistoryRepository>();

                // İlk başlangıçtan sonra borsa saatlerine (Hafta sonu vs.) saygı göster
                if (!isFirstRun && !provider.IsMarketOpen())
                {
                    await Task.Delay(provider.RefreshInterval, stoppingToken);
                    continue;
                }

                // API'den taze veriyi çek
                var prices = await provider.GetMarketOverviewAsync(stoppingToken);
                
                _logger.LogDebug("[{Provider}] fetched and updated {Count} records.", provider.ProviderName, prices.Count);

                if (prices.Count > 0)
                {
                    // -----------------------------------------------------------------------
                    // BFF ARCHITECTURE: USD TO TRY CONVERSION STRATEGY
                    // -----------------------------------------------------------------------
                    var currentUsdPrice = prices.FirstOrDefault(p => p.ProductCode == "USDTRY=X")?.Ask
                                          ?? _snapshotStore.Current?.Items.FirstOrDefault(p => p.ProductCode == "USDTRY=X")?.Ask 
                                          ?? 0m;

                    // [DÜZELTME]: Veritabanına yazılacak kayıt listesi döngüden ÖNCE oluşturuldu.
                    // [DÜZELTME]: Veritabanına yazılacak kayıt listesi döngüden ÖNCE oluşturuldu.
                    var recordsToInsert = new List<PriceRecord>();
                    var calculatedPrices = new List<LatestPriceResponse>();

                    foreach (var price in prices)
                    {
                        var previousClose = _previousCloseStore.GetPreviousClose(price.ProductCode);
                        decimal changeRate = 0m;

                        if (previousClose.HasValue && previousClose.Value > 0)
                        {
                            // 1. ANA SİSTEM (NORMAL AKIŞ): DB'mizde veri var, hızlıca hesapla.
                            changeRate = ((price.Ask - previousClose.Value) / previousClose.Value) * 100m;
                        }
                        else 
                        {
                            // 2. YEDEK PLAN (FALLBACK & İLK AÇILIŞ): DB'miz boşsa veya veri patladıysa
                            // [MİMARİ DÜZELTME]: Eski sistemdeki "hesaplanmış sahte dünkü fiyatı veritabanına kaydetme (Seed)" işlemi TAMAMEN SİLİNDİ.
// Artık sadece arayüzde % değişimi doğru göstermek için matematiksel bir hesaplama yapıyoruz, ancak bunu KALICI TARİHE (DB'ye) YAZMIYORUZ.

                            if (provider is IBinanceClient binanceClient)
                            {
                                // Tüm piyasayı tek seferde (RAM Cache'li) getir
                                var bulkData = await binanceClient.GetBulk24HrTickersAsync(stoppingToken);
    
                                var coinData = bulkData.FirstOrDefault(x => x.Symbol == price.ProductCode.Replace("-USD", "USDT"));
    
                                if (coinData != null && 
                                    decimal.TryParse(coinData.PriceChangePercent, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var fallbackRate))
                                {
                                    changeRate = fallbackRate; 
        
                                    // DİKKAT: Eski kodda burada hesaplanan dünkü kapanış `recordsToInsert.Add(...)` ile DB'ye yazılıyordu.
                                    // O blok tamamen silindi. Veritabanına sadece o anki GERÇEK fiyat yazılacak.
                                }
                            }
                            else
                            {
                                // Yahoo gibi hazır ChangeRate yollayan sistemler için
                                changeRate = price.ChangeRate;
                            }
                        }

                        var finalPrice = price with { ChangeRate = Math.Round(changeRate, 2) };

                        // Kripto paraları TL'ye çevirme (BFF)
                        if (finalPrice.Type == "CRYPTO")
                        {
                            // [DÜZELTME]: Race Condition Kalkanı
                            // USD fiyatı henüz hazır değilse, veriyi sisteme sokma, bir sonraki döngüyü bekle.
                            if (currentUsdPrice <= 0)
                            {
                                _logger.LogWarning("[BFF Shield] USD/TRY oranı henüz hazır değil. {Code} çevirisi bir sonraki döngüye ertelendi.", finalPrice.ProductCode);
                                continue; 
                            }

                            finalPrice = finalPrice with 
                            {
                                Bid = finalPrice.Bid * currentUsdPrice,
                                Ask = finalPrice.Ask * currentUsdPrice,
                                LastPrice = (finalPrice.LastPrice ?? 0m) * currentUsdPrice,
                                IsConvertedToTry = true
                            };
                        }

                        calculatedPrices.Add(finalPrice);
                    }

                    // İşlemleri State (RAM Cache) ve Kalkan kilitlerine alıyoruz
                    lock (CacheLock)
                    {
                        var currentSnapshot = _snapshotStore.Current;
                        var currentDict = currentSnapshot?.Items.ToDictionary(p => p.ProductCode)
                                           ?? new Dictionary<string, LatestPriceResponse>();

                        foreach (var newPrice in calculatedPrices)
                        {
                            // --- SHIELD 1: SIFIR FİYAT KALKANI ---
                            if (newPrice.Ask <= 0 || newPrice.Bid <= 0)
                            {
                                _logger.LogWarning("[SHIELD] {Code} returned zero/negative price. Source: {Source}", newPrice.ProductCode, newPrice.Source);
                                _ = _alertService.SendAlertAsync($"⚠️ *ZERO PRICE REJECTED*\nSymbol: `{newPrice.ProductCode}`\nSource: `{newPrice.Source}`\nPrice: `{newPrice.Ask}`\n_System retained the previous valid price._", stoppingToken);
                                continue;
                            }

                            bool hasPriceChanged = true;

                            if (currentDict.TryGetValue(newPrice.ProductCode, out var existingPrice))
                            {
                                // --- SHIELD 2: FLASH CRASH (ANİ ÇÖKÜŞ) KALKANI ---
                                if (existingPrice.Ask > 0)
                                {
                                    var instantChange = Math.Abs((newPrice.Ask - existingPrice.Ask) / existingPrice.Ask * 100m);
                                    
                                    // Eğer saniyeler içinde fiyat %40 oynarsa veriyi reddet
                                    if (instantChange > 40m) 
                                    {
                                        _logger.LogWarning("[SHIELD] Unreasonable deviation detected for {Code} ({Rate:N2}%). Data rejected!", newPrice.ProductCode, instantChange);
                                        _ = _alertService.SendAlertAsync($"🚨 *FLASH CRASH DETECTED*\nSymbol: `{newPrice.ProductCode}`\nOld: `{existingPrice.Ask}`\nNew: `{newPrice.Ask}`\nDeviation: `%{instantChange:N2}`\nSource: `{newPrice.Source}`", stoppingToken);
                                        continue;
                                    }
                                }

                                // Fiyat değişmediyse veritabanı log şişmesini engelle
                                if (existingPrice.Ask == newPrice.Ask && existingPrice.Bid == newPrice.Bid)
                                {
                                    hasPriceChanged = false; 
                                }
                            }

                            if (hasPriceChanged)
                            {
                                recordsToInsert.Add(new PriceRecord
                                {
                                    Id = Guid.NewGuid(),
                                    ProductCode = newPrice.ProductCode,
                                    Source = newPrice.Source,
                                    AskPrice = newPrice.Ask,
                                    BidPrice = newPrice.Bid,
                                    LastPrice = newPrice.LastPrice,
                                    TimestampUtc = clock.UtcNow
                                });
                            }

                            // Doğrulanan yeni veriyi hafızaya at
                            currentDict[newPrice.ProductCode] = newPrice;
                        }

                        // Ön belleği güncelle
                        _snapshotStore.Set(new LatestPricesResponse(
                            Items: currentDict.Values.ToList(), 
                            ComputedAtUtc: DateTimeOffset.UtcNow
                        ));
                    }

                    // Yeni oluşan geçerli fiyatları (ve ilk gün can sularını) DB'ye toplu kaydet
                    if (recordsToInsert.Count > 0)
                    {
                        await repository.AddPriceRecordsAsync(recordsToInsert, stoppingToken);
                    }

                    isFirstRun = false; 
                }

                await Task.Delay(provider.RefreshInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break; // Kapanış talebi
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A critical error occurred while fetching prices for provider {ProviderType}.", providerType.Name);
                
                await _alertService.SendAlertAsync(
                    $"⚠️ *Provider Crashed: {providerType.Name}*\nTime: `{DateTime.UtcNow:HH:mm:ss} UTC`\nError: `{ex.Message}`", 
                    stoppingToken);

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
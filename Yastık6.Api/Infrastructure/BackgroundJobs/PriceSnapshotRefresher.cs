using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Domain.Entities;
using Yastık6.Api.Infrastructure.Alerts;
using Yastık6.Api.Infrastructure.Caching;

namespace Yastık6.Api.Infrastructure.BackgroundJobs;

/// <summary>
/// Sadece piyasa fiyatlarını periyodik olarak toplamak, iş hattına (Pipeline) paslamak
/// ve doğrulanmış sonuçları RAM Cache ile Veritabanına yazmakla görevli Altyapı Servisi (Trafik Polisi).
/// </summary>
public sealed class PriceSnapshotRefresher : BackgroundService
{
    private readonly IPriceSnapshotStore _snapshotStore;
    private readonly IPriceProcessor _priceProcessor; // [YENİ]: Tüm ağır işi devralan motorumuz
    private readonly ILogger<PriceSnapshotRefresher> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAlertService _alertService;
    
    // RAM Cache güncellemelerinin yarış durumuna (Race Condition) girmesini önleyen kilit
    private static readonly object CacheLock = new object();

    public PriceSnapshotRefresher(
        IPriceSnapshotStore snapshotStore,
        IPriceProcessor priceProcessor,
        ILogger<PriceSnapshotRefresher> logger,
        IServiceScopeFactory scopeFactory,
        IAlertService alertService)
    {
        _snapshotStore = snapshotStore;
        _priceProcessor = priceProcessor;
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

        // Her fiyat sağlayıcısı (Binance, Yahoo, Harem) için bağımsız birer döngü (Task) başlat
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

                // Piyasa saatleri kontrolü (Hafta sonu BIST/Forex kapalıysa boşuna API'yi yorma)
                if (!isFirstRun && !provider.IsMarketOpen())
                {
                    await Task.Delay(provider.RefreshInterval, stoppingToken);
                    continue;
                }

                // 1. ADIM: Sağlayıcıdan (Borsa/API) ham fiyat verilerini çek
                var rawPrices = await provider.GetMarketOverviewAsync(stoppingToken);
                
                if (rawPrices.Count > 0)
                {
                    // 2. ADIM: Ham verileri işlenmesi, TL'ye çevrilmesi ve kalkanlardan geçmesi için 
                    // yeni soyutladığımız mimari motora (Application katmanına) gönderiyoruz.
                    var verifiedPrices = await _priceProcessor.ProcessPricesAsync(rawPrices, provider.ProviderName, stoppingToken);

                    if (verifiedPrices.Count == 0)
                    {
                        // Eğer tüm fiyatlar kalkanlara takılıp elendiyse döngüye devam et
                        await Task.Delay(provider.RefreshInterval, stoppingToken);
                        continue;
                    }

                    var recordsToInsert = new List<PriceRecord>();

                    // 3. ADIM: İş hattından tertemiz çıkan doğrulanmış fiyatları RAM önbelleğine (State) yazıyoruz
                    lock (CacheLock)
                    {
                        var currentSnapshot = _snapshotStore.Current;
                        var currentDict = currentSnapshot?.Items.ToDictionary(p => p.ProductCode)
                                           ?? new Dictionary<string, LatestPriceResponse>();

                        foreach (var newPrice in verifiedPrices)
                        {
                            bool hasPriceChanged = true;

                            if (currentDict.TryGetValue(newPrice.ProductCode, out var existingPrice))
                            {
                                // Fiyat milimetrik olarak bile değişmediyse DB loglarının şişmesini engelle
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

                            // Hafızayı en güncel doğrulanmış fiyatla tazele
                            currentDict[newPrice.ProductCode] = newPrice;
                        }

                        // Global snapshot'ı güncelle (Arayüz burayı okuyor)
                        _snapshotStore.Set(new LatestPricesResponse(
                            Items: currentDict.Values.ToList(), 
                            ComputedAtUtc: DateTimeOffset.UtcNow
                        ));
                    }

                    // 4. ADIM: Sadece fiyatı değişen yeni geçerli kayıtları toplu olarak DB'ye logla
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
                break; // Uygulama kapanıyor
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
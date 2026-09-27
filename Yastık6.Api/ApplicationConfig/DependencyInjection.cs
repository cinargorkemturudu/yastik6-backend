using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.Extensions.Http;
using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Infrastructure.BackgroundJobs;
using Yastık6.Api.Infrastructure.Caching;
using Yastık6.Api.Infrastructure.Persistence;
using Yastık6.Api.Infrastructure.Persistence.Repositories;
using Yastık6.Api.Infrastructure.Providers.Harem;
using Yastık6.Api.Infrastructure.Providers.YahooFinance;
using Yastık6.Api.Infrastructure.Providers.Binance;
using Yastık6.Api.Infrastructure.Providers.FreeCurrency;
using Yastık6.Api.Infrastructure.Alerts;
using Yastık6.Api.Infrastructure.Time;

namespace Yastık6.Api.ApplicationConfig;

public static class DependencyInjection
{
    public static IServiceCollection AddYastık6Application(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddMemoryCache();

        services.AddSingleton<ISystemClock, SystemClock>();
        services.AddSingleton<AlertTracker>();

        var dbPath = configuration["Database:ConnectionString"] ?? "Data Source=yastik.db";
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(dbPath));

        services.AddScoped<IPriceHistoryRepository, PriceHistoryRepository>();
        services.AddScoped<IPriceService, PriceService>();
        services.AddScoped<IValidator<(string productCode, PriceHistoryRange range)>, GetHistoryValidator>();

        services.AddSingleton<IPriceSnapshotStore, PriceSnapshotStore>();

        services.AddSingleton<PriceShieldValidator>();
        services.AddSingleton<IPriceProcessor, PriceProcessor>();

        services.AddHostedService<PriceSnapshotRefresher>();

        // --- WATCHDOG (TELEGRAM ALARM) CONFIGURATION ---
        services.AddHttpClient<IAlertService, TelegramAlertService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        // --- HAREM CLIENT CONFIGURATION ---
        var haremBaseUrlRaw = configuration["Harem:BaseUrl"] ?? throw new InvalidOperationException("Harem:BaseUrl is missing.");
        var hostRaw = configuration["Harem:Host"] ?? throw new InvalidOperationException("Harem:Host missing.");
        var apiKeyRaw = configuration["Harem:ApiKey"] ?? throw new InvalidOperationException("Harem:ApiKey missing.");

        var haremBaseUrl = new string(haremBaseUrlRaw.Where(c => c >= 32 && c <= 126).ToArray());
        var host = new string(hostRaw.Where(c => c >= 32 && c <= 126).ToArray());
        var apiKey = new string(apiKeyRaw.Where(c => c >= 32 && c <= 126).ToArray());

        services.AddHttpClient<HaremClient>(client =>
            {
                client.BaseAddress = new Uri(haremBaseUrl);
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-RapidAPI-Host", host);
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-RapidAPI-Key", apiKey);
                client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            // [KRİTİK DÜZELTME]: AddPolicyHandler kullanılarak ILogger içeri sızdırıldı ve log şablonu düzeltildi.
            .AddPolicyHandler((sp, request) =>
            {
                var logger = sp.GetRequiredService<ILogger<HaremClient>>();
                return HttpPolicyExtensions
                    .HandleTransientHttpError()
                    .WaitAndRetryAsync(
                        retryCount: 3,
                        sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000)),
                        onRetry: (outcome, timespan, retryAttempt, context) =>
                        {
                            logger.LogWarning("Harem API returned a transient error ({ErrorMessage}). Retrying in {DelaySeconds:F1}s (attempt {RetryCount}).",
                                outcome.Exception?.Message ?? $"HTTP {(int?)outcome.Result?.StatusCode}",
                                timespan.TotalSeconds,
                                retryAttempt);
                        });
            });

        services.AddTransient<IPriceProvider>(sp => sp.GetRequiredService<HaremClient>());

        // --- BINANCE CLIENT CONFIGURATION ---
        services.AddHttpClient<BinanceClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.binance.com");
            client.Timeout = TimeSpan.FromSeconds(10);
        })
        .AddPolicyHandler((sp, request) =>
        {
            var logger = sp.GetRequiredService<ILogger<BinanceClient>>();
            return HttpPolicyExtensions
                .HandleTransientHttpError()
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000)),
                    onRetry: (outcome, timespan, retryAttempt, context) =>
                    {
                        logger.LogWarning("Binance API returned a transient error ({ErrorMessage}). Retrying in {DelaySeconds:F1}s (attempt {RetryCount}).",
                            outcome.Exception?.Message ?? $"HTTP {(int?)outcome.Result?.StatusCode}",
                            timespan.TotalSeconds,
                            retryAttempt);
                    });
        });

        services.AddTransient<IPriceProvider>(sp => sp.GetRequiredService<BinanceClient>());

        // --- FREE CURRENCY CLIENT CONFIGURATION ---
        var fcBaseUrlRaw = configuration["FreeCurrency:BaseUrl"] ?? "https://api.freecurrencyapi.com";
        var fcBaseUrl = new string(fcBaseUrlRaw.Where(c => c >= 32 && c <= 126).ToArray());
        var fcApiKeyRaw = configuration["FreeCurrency:ApiKey"] ?? throw new InvalidOperationException("FreeCurrency:ApiKey is missing.");
        var fcApiKey = new string(fcApiKeyRaw.Where(c => c >= 32 && c <= 126).ToArray());

        services.AddHttpClient<FreeCurrencyClient>(client =>
        {
            client.BaseAddress = new Uri(fcBaseUrl);
            client.DefaultRequestHeaders.TryAddWithoutValidation("apikey", fcApiKey);
            client.Timeout = TimeSpan.FromSeconds(15);
        })
        .AddPolicyHandler((sp, request) =>
        {
            var logger = sp.GetRequiredService<ILogger<FreeCurrencyClient>>();
            return HttpPolicyExtensions
                .HandleTransientHttpError()
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000)),
                    onRetry: (outcome, timespan, retryAttempt, context) =>
                    {
                        // Seq'te gördüğün o 'Deneme: []' hatasını düzelten kusursuz eşleşme:
                        logger.LogWarning("FreeCurrency API returned a transient error ({ErrorMessage}). Retrying in {DelaySeconds:F1}s (attempt {RetryCount}).",
                            outcome.Exception?.Message ?? $"HTTP {(int?)outcome.Result?.StatusCode}",
                            timespan.TotalSeconds,
                            retryAttempt);
                    });
        });

        services.AddTransient<IPriceProvider>(sp => sp.GetRequiredService<FreeCurrencyClient>());

        // --- YAHOO FINANCE CLIENT CONFIGURATION ---
        services.AddTransient<IPriceProvider, YahooBistClient>();
        services.AddTransient<IPriceProvider, YahooForexClient>();
        services.AddTransient<IPriceProvider, YahooCryptoClient>();

        services.AddHostedService<DailyRolloverService>();
        services.AddHostedService<DataRetentionWorker>();
        services.AddHostedService<HistoryCacheWarmer>();
        services.AddSingleton<IPreviousCloseStore, PreviousCloseStore>();

        return services;
    }
}

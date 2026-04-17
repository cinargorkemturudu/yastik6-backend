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

        var dbPath = configuration["Database:ConnectionString"] ?? "Data Source=yastik.db";
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(dbPath));

        services.AddScoped<IPriceHistoryRepository, PriceHistoryRepository>();
        services.AddScoped<IPriceService, PriceService>();
        services.AddScoped<IValidator<(string productCode, PriceHistoryRange range)>, GetHistoryValidator>();

        services.AddSingleton<IPriceSnapshotStore, PriceSnapshotStore>();
        services.AddHostedService<PriceSnapshotRefresher>();
        
        // --- HAREM CLIENT CONFIGURATION ---
        var haremBaseUrl = configuration["Harem:BaseUrl"] ?? throw new InvalidOperationException("Harem:BaseUrl is missing.");
        var host = configuration["Harem:Host"] ?? throw new InvalidOperationException("Harem:Host missing.");
        var apiKey = configuration["Harem:ApiKey"] ?? throw new InvalidOperationException("Harem:ApiKey missing.");

        services.AddHttpClient<IHaremClient, HaremClient>(client =>
        {
            client.BaseAddress = new Uri(haremBaseUrl);
            client.DefaultRequestHeaders.TryAddWithoutValidation("x-rapidapi-host", host);
            client.DefaultRequestHeaders.TryAddWithoutValidation("x-rapidapi-key", apiKey);
            
            // [KRİTİK DÜZELTME]: Harem'in yavaş cevaplarını tolere etmek için 10 saniyeden 30 saniyeye çıkarıldı!
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddTransientHttpErrorPolicy(policyBuilder => 
            policyBuilder.WaitAndRetryAsync(
                retryCount: 3, 
                sleepDurationProvider: attempt => 
                {
                    var baseDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); 
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000)); 
                    return baseDelay + jitter; 
                }
            ));

        services.AddTransient<IPriceProvider>(sp => sp.GetRequiredService<IHaremClient>());
        
        // --- YAHOO FINANCE CLIENT CONFIGURATION ---
        services.AddTransient<IPriceProvider, YahooBistClient>();
        services.AddTransient<IPriceProvider, YahooForexClient>();
        services.AddTransient<IPriceProvider, YahooCryptoClient>();
        
        // NOT: Eğer ileride İndikatörler (^GSPC vs) için bir YahooIndicesClient yazarsan, onu da buraya eklemelisin:
        // services.AddTransient<IPriceProvider, YahooIndicesClient>();
        
        services.AddHostedService<DailyRolloverService>();
        services.AddHostedService<DataRetentionWorker>();
        services.AddSingleton<IPreviousCloseStore, PreviousCloseStore>();

        return services;
    }
}
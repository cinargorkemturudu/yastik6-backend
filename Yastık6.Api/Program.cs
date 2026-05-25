using System.IO.Compression;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using Yastık6.Api.ApplicationConfig;
using Yastık6.Api.Infrastructure.Alerts;
using Yastık6.Api.Infrastructure.BackgroundJobs;
using Yastık6.Api.Infrastructure.Persistence;

// Bootstrap Serilog at the earliest possible stage to ensure robust error logging
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        "logs/yastik-api-.txt", 
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 10 * 1024 * 1024, // Dosya boyutu 10MB'ı aşarsa yenisine geçer
        rollOnFileSizeLimit: true,            // Boyut limitini aktif eder
        retainedFileCountLimit: 3,            // Sunucuda sadece son 3 günün log dosyasını tutar (Kara Kutu)
        shared: true)
    .CreateLogger();

try
{
    Log.Information("Starting Yastık6 API...");

    var builder = WebApplication.CreateBuilder(args);

    // Reverse Proxy (Nginx, Docker vb.) arkasında gerçek kullanıcı IP'sini alma ayarı
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });

    // Replace the default .NET logger with Serilog
    builder.Host.UseSerilog((context, loggerConfiguration) =>
    {
        // IP adresini hardcoded yapmamak için konfigürasyondan güvenli bir şekilde okuyoruz
        var seqUrl = context.Configuration["Serilog:SeqServerUrl"] ?? "http://localhost:5341";

        loggerConfiguration
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.Seq(seqUrl) // Çalışma zamanındaki tüm logları Seq paneline akıtır
            .WriteTo.File(
                "logs/yastik-api-.txt", 
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,            
                retainedFileCountLimit: 3, // Canlı ortam çalışma zamanında da 3 gün kuralı geçerli
                shared: true);
    });

    // Configure Controllers with CamelCase naming policy
    builder.Services.AddControllers()
        .AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            o.JsonSerializerOptions.DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            o.JsonSerializerOptions.Converters.Add(new Yastık6.Api.Infrastructure.Time.UtcDateTimeConverter());
        });

    builder.Services.AddEndpointsApiExplorer();
    
    builder.Services.AddSwaggerGen(c =>
    {
        c.OperationFilter<Yastık6.Api.Infrastructure.Swagger.ApiKeyHeaderFilter>();
    });
    
    // Herkese açık CORS ("AllowAll") yerine katı CORS (StrictCors) politikası
    builder.Services.AddCors(options =>
    {
        var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
        options.AddPolicy("StrictCors", policy =>
        {
            if (allowedOrigins.Any())
            {
                policy.WithOrigins(allowedOrigins)
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            }
        });
    });

    // Rate Limiter "SlidingWindow" ayarları
    builder.Services.AddRateLimiter(options =>
    {
        options.AddPolicy("PublicApi", httpContext =>
            RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: partition => new SlidingWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromMinutes(1),
                    PermitLimit = 60,
                    SegmentsPerWindow = 3,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 2
                }));
        
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests; 
    });

    // Register Health Checks for system monitoring
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<ApplicationDbContext>("SQLite_Database_Check");

    // Register Global Exception Handler
    builder.Services.AddExceptionHandler<Yastık6.Api.Infrastructure.ExceptionHandling.GlobalExceptionHandler>();
    builder.Services.AddProblemDetails();

    // Configure Response Compression (Brotli & Gzip)
    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
    });

    builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
    {
        options.Level = CompressionLevel.Fastest;
    });

    // Register Application Services
    builder.Services.AddYastık6Application(builder.Configuration);
    
    builder.Services.AddSingleton<AlertTracker>();
    builder.Services.AddHostedService<DailyReportWorker>();
    builder.Services.AddHostedService<TelegramCommandListener>();

    var app = builder.Build();

    var expectedApiKey = app.Configuration["ApiSettings:MobileClientKey"] 
                         ?? throw new InvalidOperationException("Critical Security Error: MobileClientKey is not configured.");

    // --- MIDDLEWARE PIPELINE (ORDER IS CRITICAL) ---

    app.UseForwardedHeaders();
    app.UseCors("StrictCors");
    app.UseExceptionHandler();

    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
    });

    app.UseResponseCompression();
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseRateLimiter();

    // Mobile Client Verification & Security Middleware
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/swagger"))
        {
            if (!app.Environment.IsDevelopment())
            {
                var swaggerKey = app.Configuration["ApiSettings:SwaggerKey"];
                
                if (string.IsNullOrEmpty(swaggerKey) || context.Request.Query["key"] != swaggerKey)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
            }
            await next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/api/health"))
        {
            await next(context);
            return;
        }
        
        if (!context.Request.Headers.TryGetValue("X-Mobile-Client-Key", out var extractedApiKey) || 
            extractedApiKey != expectedApiKey)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized Client.");
            return;
        }

        await next(context);
    });

    app.MapHealthChecks("/api/health");
    app.MapControllers().RequireRateLimiting("PublicApi");

    // --- DATABASE SEEDING PROCESS ---
    using (var scope = app.Services.CreateScope())
    {
        var services = scope.ServiceProvider;
        try
        {
            var context = services.GetRequiredService<ApplicationDbContext>();
            var logger = services.GetRequiredService<ILogger<Program>>();
            
            var dbConnection = context.Database.GetDbConnection();
            if (dbConnection is Microsoft.Data.Sqlite.SqliteConnection sqliteConnection)
            {
                var dataSource = sqliteConnection.DataSource;
                var directoryPath = Path.GetDirectoryName(dataSource);

                if (!string.IsNullOrWhiteSpace(directoryPath) && !Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                    logger.LogInformation("Created missing database directory at: {DirectoryPath}", directoryPath);
                }
            }
            
            context.Database.Migrate(); 
            context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            
            await DbSeeder.SeedSymbolsAsync(context, logger);
            
            Log.Information("Database migration, WAL mode activation, and seeding completed successfully.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "An error occurred during the database seeding process.");
        }
    }

    app.Run();
}
catch (HostAbortedException) 
{
    throw;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Yastık6 API terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush(); // Log havuzundaki verilerin diske ve Seq'e tamamen yazıldığından emin olup kapatır
}
using System.IO.Compression;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.HttpOverrides; // [EKLENDİ - ÇÖZÜM 1]: Proxy başlıkları için gerekli kütüphane
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using Yastık6.Api.ApplicationConfig;
using Yastık6.Api.Infrastructure.Persistence;

// Bootstrap Serilog at the earliest possible stage to ensure robust error logging
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/yastik-api-.txt", rollingInterval: RollingInterval.Day, shared: true)
    .CreateLogger();

try
{
    Log.Information("Starting Yastık6 API...");

    var builder = WebApplication.CreateBuilder(args);

    // [EKLENDİ - ÇÖZÜM 1]: Reverse Proxy (Nginx, Docker vb.) arkasında gerçek kullanıcı IP'sini alma ayarı
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });

    // Replace the default .NET logger with Serilog
    builder.Host.UseSerilog((context, loggerConfiguration) =>
    {
        loggerConfiguration
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File("logs/yastik-api-.txt", rollingInterval: RollingInterval.Day, shared: true);
    });

    // Configure Controllers with CamelCase naming policy
    builder.Services.AddControllers()
        .AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            o.JsonSerializerOptions.DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });

    builder.Services.AddEndpointsApiExplorer();
    
    builder.Services.AddSwaggerGen(c =>
    {
        c.OperationFilter<Yastık6.Api.Infrastructure.Swagger.ApiKeyHeaderFilter>();
    });
    
    // [DEĞİŞTİRİLDİ - ÇÖZÜM 5]: Herkese açık CORS ("AllowAll") yerine katı CORS (StrictCors) politikası eklendi.
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
            // Eğer liste boşsa, dışarıdan hiçbir tarayıcı erişimine izin vermez (Flutter mobil uygulaması CORS kurallarına takılmaz, çalışmaya devam eder).
        });
    });

    // [DEĞİŞTİRİLDİ - ÇÖZÜM 3]: Rate Limiter "SlidingWindow" olarak güncellendi.
    builder.Services.AddRateLimiter(options =>
    {
        options.AddPolicy("PublicApi", httpContext =>
            RateLimitPartition.GetSlidingWindowLimiter( // FixedWindow yerine SlidingWindow kullanıyoruz
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: partition => new SlidingWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromMinutes(1),
                    PermitLimit = 60,
                    SegmentsPerWindow = 3, // 1 dakikayı 3'e böler (20 saniyelik dilimler), yığılmaları (bursting) önler
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

    var app = builder.Build();

    var expectedApiKey = app.Configuration["ApiSettings:MobileClientKey"] 
                         ?? throw new InvalidOperationException("Critical Security Error: MobileClientKey is not configured.");

    // --- MIDDLEWARE PIPELINE (ORDER IS CRITICAL) ---

    // [EKLENDİ - ÇÖZÜM 1]: Gerçek IP adreslerini çözümleyen middleware en başa yerleştirildi.
    app.UseForwardedHeaders();

    // [DEĞİŞTİRİLDİ - ÇÖZÜM 5]: StrictCors politikasını devreye alıyoruz.
    app.UseCors("StrictCors");

    // 2. Global error handling captures exceptions from all subsequent middlewares
    app.UseExceptionHandler();

    // 3. Serilog is now placed BEFORE Response Compression.
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
    });

    // 4. Apply Response Compression
    app.UseResponseCompression();

    app.UseSwagger();
    app.UseSwaggerUI();

    // 5. Apply Rate Limiting
    app.UseRateLimiter();

    // 6. Mobile Client Verification & Security Middleware
    app.Use(async (context, next) =>
    {
        // [DEĞİŞTİRİLDİ - ÇÖZÜM 4]: Swagger arayüzü koruması
        if (context.Request.Path.StartsWithSegments("/swagger"))
        {
            if (!app.Environment.IsDevelopment())
            {
                var swaggerKey = app.Configuration["ApiSettings:SwaggerKey"];
                
                // Production ortamında URL sonuna ?key=şifre eklenmemişse Swagger arayüzünü saklar
                if (string.IsNullOrEmpty(swaggerKey) || context.Request.Query["key"] != swaggerKey)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound; // 401 yerine bilerek 404 dönüyoruz ki tarayıcılar orada bir şey olduğunu anlamasın
                    return;
                }
            }
            await next(context);
            return;
        }

        // Explicitly exempt infrastructure endpoints from the API Key requirement
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

    // Map Health Checks completely outside of the "PublicApi" rate limit policy
    app.MapHealthChecks("/api/health");
    
    // Map standard controllers and explicitly attach the rate limit policy to them
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
            
            // Enable WAL mode to prevent database locking issues
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
    Log.CloseAndFlush();
}
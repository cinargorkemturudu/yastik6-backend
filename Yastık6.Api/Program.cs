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
using Yastık6.Api.Infrastructure.RateLimiting;
using Yastık6.Api.Controllers;

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
            // [YENİ 1]: Her loga eşsiz bir İstek Kimliği (Trace ID) basar. Hata takibini kusursuzlaştırır.
            .Enrich.WithCorrelationIdHeader("X-Correlation-Id")

            // [YENİ 2]: Siber bot taramalarını (Zombies) Seq'e ulaşmadan havada yok eden Gümrük Memuru!
            .Filter.ByExcluding(logEvent =>
            {
                if (logEvent.Properties.TryGetValue("RequestPath", out var pathValue))
                {
                    var path = pathValue.ToString().ToLowerInvariant();
                    return path.Contains(".env") || path.Contains("cgi-bin") || path.Contains("wp-admin") || path.Contains("stok");
                }
                return false;
            })
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
        c.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo { Title = "Yastık6 API", Version = "v1" });
        c.SwaggerDoc(PublicApiInfo.DocumentName, new Microsoft.OpenApi.OpenApiInfo
        {
            Title = "Yastık6 Public API",
            Version = "v1",
            Description = "Live market data behind the Yastık6 app: Turkish gold, currencies, BIST stocks and crypto. " +
                          $"No key needed. {PublicApiInfo.PermitPerIpPerMinute(builder.Configuration)} requests per minute per IP. " +
                          PublicApiInfo.Disclaimer
        });

        // The full doc lists everything; the public doc lists only the keyless controller.
        c.DocInclusionPredicate((docName, api) =>
            docName != PublicApiInfo.DocumentName || api.GroupName == PublicApiInfo.DocumentName);

        c.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml"));
        c.OperationFilter<Yastık6.Api.Infrastructure.Swagger.ApiKeyHeaderFilter>();
        c.ParameterFilter<Yastık6.Api.Infrastructure.Swagger.PublicHistoryRangeFilter>();
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

    var publicDemoLimiter = new PublicDemoLimiter(
        PublicApiInfo.PermitPerIpPerMinute(builder.Configuration),
        PublicApiInfo.GlobalPermitPerMinute(builder.Configuration));

    // Rate Limiter "SlidingWindow" ayarları
    builder.Services.AddRateLimiter(options =>
    {
        options.AddPolicy(RateLimitPolicies.MobileApp, httpContext =>
            RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: partition => new SlidingWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromMinutes(1),
                    // The app polls every 10s (6 req/min), and Turkish mobile carriers put many users
                    // behind one CGNAT IP, so 60/min would start returning 429s at ~10 users per IP.
                    // 600/min leaves room for ~100; responses come from RAM, so this is cheap to serve.
                    PermitLimit = 600,
                    SegmentsPerWindow = 3,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 2
                }));

        // Keyless /api/public routes and their Swagger page: a small budget per visitor plus a shared
        // ceiling. At ~380 KB per uncompressed market response, 120/min caps a sustained flood at
        // roughly 2 TB/month of egress, inside the server's free allowance.
        options.GlobalLimiter = publicDemoLimiter.Limiter;

        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, ct) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            }

            await context.HttpContext.Response.WriteAsJsonAsync(
                new { error = "Too many requests. Please slow down and retry shortly." }, ct);

            if (PublicDemoLimiter.IsPublic(context.HttpContext) &&
                publicDemoLimiter.IsSharedCeilingExhausted(context.HttpContext) &&
                publicDemoLimiter.TryClaimAlert(DateTimeOffset.UtcNow))
            {
                var alertService = context.HttpContext.RequestServices.GetRequiredService<IAlertService>();
                await alertService.SendAlertAsync(
                    $"⚠️ Public demo API hit its shared limit ({PublicApiInfo.GlobalPermitPerMinute(builder.Configuration)}/min). " +
                    "Visitors are getting 429s; the mobile app is unaffected. Muted for 1 hour. " +
                    "To switch the public tier off: PublicApi__Enabled=false in .env, then restart.",
                    null, ct);
            }
        };
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

    // Required for Enrich.WithCorrelationIdHeader to be able to read the current request's headers
    builder.Services.AddHttpContextAccessor();

    builder.Services.AddSingleton<AlertTracker>();
    builder.Services.AddHostedService<DailyReportWorker>();
    builder.Services.AddHostedService<TelegramCommandListener>();

    var app = builder.Build();

    var expectedApiKey = app.Configuration["ApiSettings:MobileClientKey"]
                         ?? throw new InvalidOperationException("Critical Security Error: MobileClientKey is not configured.");

    // --- MIDDLEWARE PIPELINE (ORDER IS CRITICAL) ---

    app.UseForwardedHeaders();

    // Ensures every request carries an X-Correlation-Id, and pushes it onto Serilog's
    // ambient LogContext for the whole request so every log line written anywhere
    // during it - no matter how deep the call stack - carries CorrelationId via the
    // Enrich.FromLogContext() enricher above. (The separate Enrich.WithCorrelationIdHeader
    // enricher was not reliably propagating to nested service calls, so this replaces
    // it as the actual mechanism rather than relying on that package's internals.)
    app.Use(async (context, next) =>
    {
        var correlationId = context.Request.Headers.TryGetValue("X-Correlation-Id", out var existing) && !string.IsNullOrEmpty(existing)
            ? existing.ToString()
            : Guid.NewGuid().ToString();

        context.Request.Headers["X-Correlation-Id"] = correlationId;
        context.Response.Headers["X-Correlation-Id"] = correlationId;

        using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    });

    app.UseCors("StrictCors");
    app.UseExceptionHandler();

    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";

        // [YENİ 3]: Canlı (Prod) ortamda başarılı istekleri (HTTP 200) yutar, diski korur. 
        // Sadece HTTP 400 ve üstü hatalar Seq'e Warning olarak düşer.
        options.GetLevel = (httpContext, elapsed, ex) =>
        {
            // A flood against the public demo would otherwise write one Warning per rejected request
            // into Seq and the log files. The shared-limit Telegram alert reports it instead.
            if (ex == null && httpContext.Response.StatusCode == StatusCodes.Status429TooManyRequests &&
                PublicDemoLimiter.IsPublic(httpContext))
            {
                return Serilog.Events.LogEventLevel.Debug;
            }

            return ex != null || httpContext.Response.StatusCode >= 400
                ? Serilog.Events.LogEventLevel.Warning
                : Serilog.Events.LogEventLevel.Debug;
        };
    });

    app.UseResponseCompression();

    // Kill switch for the keyless tier: PublicApi__Enabled=false in .env, then restart the container.
    var publicApiEnabled = PublicApiInfo.IsEnabled(app.Configuration);
    app.Use(async (context, next) =>
    {
        if (!publicApiEnabled && PublicDemoLimiter.IsPublic(context))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await next(context);
    });

    // Before Swagger, so the public docs' files count against the public limits too.
    app.UseRateLimiter();

    // The private docs need ?key= outside Development. This has to run before UseSwagger,
    // which answers /swagger/* itself and never calls the middleware after it.
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/swagger") && !app.Environment.IsDevelopment())
        {
            var swaggerKey = app.Configuration["ApiSettings:SwaggerKey"];

            if (string.IsNullOrEmpty(swaggerKey) || context.Request.Query["key"] != swaggerKey)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }
        await next(context);
    });

    app.UseSwagger();
    app.UseSwaggerUI();

    // Public docs live under /api/public because nginx only forwards /api/* to this app.
    // The path check guarantees only the public document can be served from here.
    app.UseWhen(
        context => context.Request.Path.Equals($"{RateLimitPolicies.PublicPathPrefix}/openapi.json"),
        branch => branch.UseSwagger(o => o.RouteTemplate = "api/{documentName}/openapi.json"));
    app.UseSwaggerUI(c =>
    {
        c.RoutePrefix = "api/public/docs";
        c.SwaggerEndpoint($"{RateLimitPolicies.PublicPathPrefix}/openapi.json", "Yastık6 Public API");
        c.DocumentTitle = "Yastık6 Public API";
    });

    // --- İHANET ALARMI (SECURITY SHIELD) MIDDLEWARE ---
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

        // İsteği normal akışa (bir sonraki katmana) bırakıyoruz
        await next(context);

        var statusCode = context.Response.StatusCode;

        // Senin sisteminde yasal olarak var olan ve beklediğin rotalar
        bool isExpectedRoute = path.StartsWith("/api/") ||
                               path.StartsWith("/swagger") ||
                               path.StartsWith("/health") ||
                               path == "/";

        // Beklenmeyen rotadan BAŞARILI yanıt döndüyse çanları çal!
        if (!isExpectedRoute && statusCode >= 200 && statusCode < 300)
        {
            var alertService = context.RequestServices.GetRequiredService<IAlertService>();
            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

            logger.LogCritical("SECURITY BREACH! An unrecognized route was accessed successfully: {Path}", path);

            var alertMessage = $"""
                               🚨 *KRİTİK GÜVENLİK İHLALİ (Sızıntı Şüphesi)* 🚨
                               Tanımlanmayan bir adrese yapılan istek başarıyla sonuçlandı!
                               
                               *Hedef:* `{path}`
                               *IP Adresi:* `{context.Connection.RemoteIpAddress}`
                               *Dönen Kod:* `{statusCode}`
                               
                               _Sistemden yanlışlıkla veri veya dosya sızdırılmış olabilir! Acil müdahale gerekiyor._
                               """;

            await alertService.SendAlertAsync(alertMessage, null, context.RequestAborted);
        }
    });

    // Mobile Client Verification & Security Middleware
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api/health") ||
            context.Request.Path.StartsWithSegments(RateLimitPolicies.PublicPathPrefix))
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
    // Each controller names its own rate-limit policy ([EnableRateLimiting]), so the mobile and
    // public budgets can't override each other.
    app.MapControllers();

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

// Lets the integration tests boot the real pipeline with WebApplicationFactory<Program>.
public partial class Program;

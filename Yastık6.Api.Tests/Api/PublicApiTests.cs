using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Yastık6.Api.Tests.Api;

// Boots the real middleware pipeline (key check, rate limiter, Swagger) in memory.
// Background workers are removed so no external price provider is called.
public sealed class PublicApiFactory : WebApplicationFactory<Program>
{
    private readonly int _permitPerIp;
    private readonly int _globalPermit;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"yastik6-test-{Guid.NewGuid():N}.db");

    public PublicApiFactory(int permitPerIp = 3, int globalPermit = 100)
    {
        _permitPerIp = permitPerIp;
        _globalPermit = globalPermit;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("ApiSettings:MobileClientKey", PublicApiTests.MobileKey);
        builder.UseSetting("Harem:ApiKey", "test");
        builder.UseSetting("FreeCurrency:ApiKey", "test");
        builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath}");
        builder.UseSetting("Serilog:SeqServerUrl", "http://localhost:1");
        builder.UseSetting("PublicApi:PermitPerIpPerMinute", _permitPerIp.ToString());
        builder.UseSetting("PublicApi:GlobalPermitPerMinute", _globalPermit.ToString());

        builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbPath, $"{_dbPath}-wal", $"{_dbPath}-shm" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}

public class PublicApiTests
{
    public const string MobileKey = "test-mobile-key";

    private static HttpRequestMessage Get(string url, string ip, string? mobileKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("CF-Connecting-IP", ip);
        if (mobileKey != null) request.Headers.Add("X-Mobile-Client-Key", mobileKey);
        return request;
    }

    [Fact]
    public async Task PublicMarket_WithoutKey_Returns200()
    {
        using var factory = new PublicApiFactory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(Get("/api/public/market", "203.0.113.1"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MobileRoute_WithoutKey_StillReturns401()
    {
        using var factory = new PublicApiFactory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(Get("/api/prices/market", "203.0.113.2"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("Year")]
    [InlineData("All")]
    public async Task PublicHistory_RangeLongerThanAMonth_Returns400(string range)
    {
        using var factory = new PublicApiFactory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(Get($"/api/public/history/GRAM_ALTIN?range={range}", "203.0.113.3"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PublicHistory_DayRange_Returns200()
    {
        using var factory = new PublicApiFactory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(Get("/api/public/history/GRAM_ALTIN?range=Day", "203.0.113.4"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PublicRoutes_OverPerIpLimit_Return429WithRetryAfter_AndOtherIpsAreUnaffected()
    {
        using var factory = new PublicApiFactory(permitPerIp: 3);
        var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            (await client.SendAsync(Get("/api/public", "203.0.113.5"))).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var rejected = await client.SendAsync(Get("/api/public", "203.0.113.5"));
        var otherVisitor = await client.SendAsync(Get("/api/public", "198.51.100.5"));

        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.Should().NotBeNull();
        otherVisitor.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PublicRoutes_OverGlobalLimit_Return429_EvenFromFreshIps()
    {
        using var factory = new PublicApiFactory(permitPerIp: 10, globalPermit: 4);
        var client = factory.CreateClient();

        for (var i = 0; i < 4; i++)
        {
            (await client.SendAsync(Get("/api/public", $"203.0.113.{10 + i}"))).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var response = await client.SendAsync(Get("/api/public", "203.0.113.99"));

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task MobileRoutes_AreNotCountedAgainstThePublicLimits()
    {
        using var factory = new PublicApiFactory(permitPerIp: 1, globalPermit: 1);
        var client = factory.CreateClient();

        (await client.SendAsync(Get("/api/public", "203.0.113.20"))).StatusCode.Should().Be(HttpStatusCode.OK);

        for (var i = 0; i < 3; i++)
        {
            var response = await client.SendAsync(Get("/api/prices/market", "203.0.113.20", MobileKey));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task PublicOpenApiDocument_ListsOnlyPublicRoutes_WithoutTheKeyHeader()
    {
        using var factory = new PublicApiFactory();
        var client = factory.CreateClient();

        var json = await client.GetStringAsync("/api/public/openapi.json");
        using var doc = JsonDocument.Parse(json);

        var paths = doc.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        paths.Should().NotBeEmpty().And.OnlyContain(p => p.StartsWith("/api/public"));
        json.Should().NotContain("X-Mobile-Client-Key");
    }

    [Fact]
    public async Task PublicDocsPage_IsServedWithoutKey()
    {
        using var factory = new PublicApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/public/docs/index.html");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PrivateOpenApiDocument_IsNotReachableThroughThePublicPath()
    {
        using var factory = new PublicApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/openapi.json");

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }
}

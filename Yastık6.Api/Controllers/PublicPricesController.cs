using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Infrastructure.Caching;
using Yastık6.Api.Infrastructure.RateLimiting;

namespace Yastık6.Api.Controllers;

/// <summary>
/// Keyless, read-only view of the live price API for people evaluating the project.
/// Serves the same in-memory snapshot as the mobile app. Rate limits live in PublicDemoLimiter
/// (the global limiter), so they also cover the Swagger page's files.
/// </summary>
[ApiController]
[Route("api/public")]
[ApiExplorerSettings(GroupName = PublicApiInfo.DocumentName)]
[Produces("application/json")]
public sealed class PublicPricesController : ControllerBase
{
    // Longer ranges are the expensive SQLite queries, so the open tier stops at a month.
    internal static readonly PriceHistoryRange[] AllowedHistoryRanges =
        [PriceHistoryRange.Day, PriceHistoryRange.Week, PriceHistoryRange.Month];

    private readonly IPriceService _ps;
    private readonly IPriceSnapshotStore _store;

    public PublicPricesController(IPriceService ps, IPriceSnapshotStore store)
    {
        _ps = ps;
        _store = store;
    }

    /// <summary>Lists the public endpoints, valid product codes and the rate limit.</summary>
    [HttpGet]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType<PublicApiIndex>(StatusCodes.Status200OK)]
    public ActionResult<PublicApiIndex> GetIndex([FromServices] IConfiguration configuration)
    {
        var productCodes = _store.Current?.Items.Select(i => i.ProductCode).ToArray() ?? [];

        return Ok(new PublicApiIndex(
            Name: "Yastık6 Public API",
            Docs: $"{RateLimitPolicies.PublicPathPrefix}/docs",
            RateLimit: $"{PublicApiInfo.PermitPerIpPerMinute(configuration)} requests per minute per IP",
            Disclaimer: PublicApiInfo.Disclaimer,
            Endpoints:
            [
                $"GET {RateLimitPolicies.PublicPathPrefix}/market",
                $"GET {RateLimitPolicies.PublicPathPrefix}/history/{{productCode}}?range=Day|Week|Month"
            ],
            ProductCodes: productCodes));
    }

    /// <summary>Latest bid/ask for every tracked asset (gold, currencies, BIST stocks, crypto).</summary>
    /// <remarks>Served from RAM; a background worker refreshes it every few seconds from Harem, Binance, Yahoo and FreeCurrency.</remarks>
    [HttpGet("market")]
    [ResponseCache(Duration = 10, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType<LatestPricesResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public ActionResult<LatestPricesResponse> GetMarket()
    {
        var cachedData = _store.Current ?? new LatestPricesResponse(
            Items: Array.Empty<LatestPriceResponse>(),
            ComputedAtUtc: DateTimeOffset.UtcNow
        );
        return Ok(cachedData);
    }

    /// <summary>Price history for one asset, downsampled for charting.</summary>
    /// <param name="productCode">For example GRAM_ALTIN, USDTRY=X, BTC-USD or THYAO.IS. See GET /api/public for the full list.</param>
    /// <param name="range">Day, Week or Month.</param>
    /// <param name="validator">Shared validator used by the mobile app's history endpoint.</param>
    /// <param name="ct">Request cancellation.</param>
    [HttpGet("history/{productCode}")]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType<PriceHistoryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PriceHistoryResponse>> GetHistory(
        string productCode,
        [FromServices] IValidator<(string, PriceHistoryRange)> validator,
        CancellationToken ct,
        [FromQuery] PriceHistoryRange range = PriceHistoryRange.Day)
    {
        var validationResult = await validator.ValidateAsync((productCode, range), ct);

        if (!validationResult.IsValid)
        {
            return BadRequest(validationResult.Errors.Select(e => e.ErrorMessage));
        }

        if (!AllowedHistoryRanges.Contains(range))
        {
            return BadRequest(new[] { "The public API serves Day, Week and Month ranges only." });
        }

        // Unknown codes never reach the database. A cache miss in PriceService takes a lock shared
        // with the mobile app's history requests, so random codes must not be able to queue there.
        if (_store.Current?.Items.Any(i => i.ProductCode == productCode) != true)
        {
            return NotFound(new[] { $"Unknown product code '{productCode}'. GET {RateLimitPolicies.PublicPathPrefix} lists the valid codes." });
        }

        var result = await _ps.GetHistoryAsync(productCode, range, ct);
        return Ok(result);
    }
}

public sealed record PublicApiIndex(
    string Name,
    string Docs,
    string RateLimit,
    string Disclaimer,
    IReadOnlyList<string> Endpoints,
    IReadOnlyList<string> ProductCodes
);

public static class PublicApiInfo
{
    public const string DocumentName = "public";

    public const string Disclaimer =
        "Demo access for evaluating the project. Prices come from third-party providers and may be delayed; not for trading.";

    public static int PermitPerIpPerMinute(IConfiguration configuration) =>
        configuration.GetValue("PublicApi:PermitPerIpPerMinute", 30);

    public static int GlobalPermitPerMinute(IConfiguration configuration) =>
        configuration.GetValue("PublicApi:GlobalPermitPerMinute", 120);

    public static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue("PublicApi:Enabled", true);
}

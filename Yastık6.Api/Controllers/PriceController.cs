using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Hosting;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Domain.Entities;
using Yastık6.Api.Infrastructure.Caching;
using Yastık6.Api.Infrastructure.Persistence;
using Yastık6.Api.Infrastructure.RateLimiting;

namespace Yastık6.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting(RateLimitPolicies.MobileApp)]
public sealed class PricesController : ControllerBase
{
    private readonly IPriceService _ps;
    private readonly IPriceSnapshotStore _store;
    private readonly IHostEnvironment _env;

    public PricesController(IPriceService ps, IPriceSnapshotStore store, IHostEnvironment env)
    {
        _ps = ps;
        _store = store;
        _env = env;
    }

    [HttpGet("market")]
    public IActionResult GetMarketOverview()
    {
        // The background worker constantly updates _store.Current.
        // If the application just started and the cache is temporarily empty, return an empty array instantly.
        var cachedData = _store.Current ?? new LatestPricesResponse(
            Items: Array.Empty<LatestPriceResponse>(),
            ComputedAtUtc: DateTimeOffset.UtcNow
        );
        return Ok(cachedData);
    }

    [HttpGet("history/{productCode}")]
    public async Task<ActionResult<PriceHistoryResponse>> GetHistory(
        string productCode,
        [FromQuery] PriceHistoryRange range,
        [FromServices] IValidator<(string, PriceHistoryRange)> validator,
        CancellationToken ct)
    {
        var validationResult = await validator.ValidateAsync((productCode, range), ct);

        if (!validationResult.IsValid)
        {
            return BadRequest(validationResult.Errors);
        }

        var result = await _ps.GetHistoryAsync(productCode, range, ct);
        return Ok(result);
    }

    [HttpPost("seed-test-data")]
    public async Task<IActionResult> SeedTestData([FromServices] ApplicationDbContext dbContext)
    {
        if (!_env.IsDevelopment())
        {
            return NotFound();
        }

        var records = new List<PriceRecord>();
        var random = new Random();
        var basePrice = 7400m; // Fake initial price for Gram Gold
        var now = DateTimeOffset.UtcNow;

        // Create 1 data point per day for the last 365 days
        for (int i = 0; i <= 365; i++)
        {
            var fakeDate = now.AddDays(-i);

            // Add a random volatility fluctuation between -1% and +1% for each day
            var fluctuation = (decimal)(random.NextDouble() * 0.02 - 0.01);
            basePrice = basePrice + (basePrice * fluctuation);


            records.Add(new PriceRecord
            {
                Id = Guid.NewGuid(),
                ProductCode = "GRAM_ALTIN",
                Source = "Harem",
                AskPrice = basePrice * 1.01m, // Ask price is always slightly higher than Bid
                BidPrice = basePrice,
                TimestampUtc = fakeDate
            });
        }

        await dbContext.Prices.AddRangeAsync(records);
        await dbContext.SaveChangesAsync();

        return Ok($"Successfully seeded 365 days of fake 'GRAM_ALTIN' data. First Date: {now.AddDays(-365):dd/MM/yyyy}");
    }
}

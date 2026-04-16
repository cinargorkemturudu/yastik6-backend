using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Domain.Entities;

namespace Yastık6.Api.Infrastructure.BackgroundJobs;

public sealed class DataRetentionWorker(
    IServiceScopeFactory scopeFactory, 
    ILogger<DataRetentionWorker> logger) : BackgroundService
{
    // The worker wakes up once a day to perform database cleanup
    private readonly TimeSpan _runInterval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Data Retention and Compression worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CompressOldDataAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred during the data compression cycle!");
            }

            await Task.Delay(_runInterval, stoppingToken);
        }
    }

    private async Task CompressOldDataAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPriceHistoryRepository>();
        var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();

        var now = clock.UtcNow;

        // RULE 1: Data between 7 and 30 days old -> Compress into 2-HOUR buckets
        var weekAgo = now.AddDays(-7);
        var monthAgo = now.AddDays(-30);
        
        // [CHUNKED] Process the 30-day window in 1-DAY chunks to prevent RAM spikes (OOM)
        var currentChunkStart = monthAgo;
        while (currentChunkStart < weekAgo)
        {
            var currentChunkEnd = currentChunkStart.AddDays(1);
            if (currentChunkEnd > weekAgo) currentChunkEnd = weekAgo;

            await ProcessTimeRangeAsync(repository, currentChunkStart, currentChunkEnd, TimeSpan.FromHours(2), ct);
            currentChunkStart = currentChunkEnd;
        }

        // RULE 2: Data older than 30 days (up to 5 years) -> Compress into DAILY buckets
        var fiveYearsAgo = now.AddYears(-5); 
        currentChunkStart = fiveYearsAgo;
        
        // [CHUNKED] Process the 5-year window in 1-MONTH chunks to prevent RAM spikes (OOM)
        while (currentChunkStart < monthAgo)
        {
            var currentChunkEnd = currentChunkStart.AddMonths(1);
            if (currentChunkEnd > monthAgo) currentChunkEnd = monthAgo;

            await ProcessTimeRangeAsync(repository, currentChunkStart, currentChunkEnd, TimeSpan.FromDays(1), ct);
            currentChunkStart = currentChunkEnd;
        }
        
        logger.LogInformation("Daily database cleanup and compression cycle completed successfully. Memory limits preserved.");
    }

    private async Task ProcessTimeRangeAsync(
        IPriceHistoryRepository repository, 
        DateTimeOffset start, 
        DateTimeOffset end, 
        TimeSpan bucketSize, 
        CancellationToken ct)
    {
        // Because of the chunking loop above, this will only ever load a safe amount of records (e.g., 1 month max)
        var oldRecords = await repository.GetPricesBetweenAsync(start, end, ct);

        if (oldRecords.Count == 0) return;

        var compressedRecords = oldRecords
            .GroupBy(r => new 
            { 
                r.ProductCode, 
                BucketKey = r.TimestampUtc.Ticks / bucketSize.Ticks 
            })
            .Select(g => new PriceRecord
            {
                Id = Guid.NewGuid(),
                ProductCode = g.Key.ProductCode,
                Source = g.First().Source, 
                AskPrice = g.Average(r => r.AskPrice),
                BidPrice = g.Average(r => r.BidPrice),
                TimestampUtc = new DateTimeOffset(g.Key.BucketKey * bucketSize.Ticks, TimeSpan.Zero)
            })
            .ToList();

        // Safety check: Don't write anything if the compression didn't actually reduce the row count
        if (compressedRecords.Count >= oldRecords.Count) return;

        // EF Core transactions ensure atomicity: It either deletes and inserts together, or fails safely
        await repository.AddPriceRecordsAsync(compressedRecords, ct);
        await repository.DeletePriceRecordsAsync(oldRecords, ct);
        
        logger.LogInformation(
            "[{Start} - {End}]: {OldCount} raw rows deleted, {NewCount} compressed buckets inserted.", 
            start.ToString("dd/MM/yyyy"), end.ToString("dd/MM/yyyy"), oldRecords.Count, compressedRecords.Count);
    }
}
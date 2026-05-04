using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yastık6.Api.Application.Common.Interfaces;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Domain.Entities;

namespace Yastık6.Api.Infrastructure.Persistence.Repositories;

public sealed class PriceHistoryRepository : IPriceHistoryRepository
{
    private readonly ApplicationDbContext _context;

    public PriceHistoryRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    // A private helper class to map the raw SQL response
    private class BucketResult
    {
        // 'init' tells the IDE that EF Core will set these during creation, and then lock them
        public long UnixSeconds { get; init; }
        public decimal Price { get; init; }
    }

    public async Task<List<PricePointDto>> GetBucketedHistoryAsync(
        string productCode, 
        DateTimeOffset since, 
        TimeSpan bucketSize, 
        CancellationToken ct = default)
    {
        var bucketSeconds = (long)bucketSize.TotalSeconds;
        
        // SQLite stores DateTimes as text strings behind the scenes.
        // We format the C# DateTime into a string SQLite can natively understand.
        var sinceFormatted = since.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");

        // [FIXED] Used substr() to strip the timezone offset from the DateTimeOffset string. 
        // Without this, SQLite's strftime('%s') cannot parse the string and silently returns NULL.
        var sql = @"
            SELECT 
                (CAST(strftime('%s', substr(TimestampUtc, 1, 19)) AS INTEGER) / @bucket) * @bucket AS UnixSeconds,
                AVG(AskPrice) AS Price
            FROM Prices
            WHERE ProductCode = @code AND TimestampUtc >= @since
            GROUP BY UnixSeconds
            ORDER BY UnixSeconds ASC";

        // Explicitly declared as object[] to prevent co-variant array conversion warnings
        var parameters = new object[]
        {
            new SqliteParameter("@bucket", bucketSeconds),
            new SqliteParameter("@code", productCode),
            new SqliteParameter("@since", sinceFormatted)
        };

        // Database.SqlQueryRaw maps the SQL result directly to our BucketResult class
        var results = await _context.Database
            .SqlQueryRaw<BucketResult>(sql, parameters)
            .ToListAsync(ct);

        // Convert the SQLite Unix timestamps back to DateTimeOffset for the frontend and round the price
        return results.Select(r => new PricePointDto(
            DateTimeOffset.FromUnixTimeSeconds(r.UnixSeconds),
            Math.Round(r.Price, 2)
        )).ToList();
    }

    public async Task AddPriceRecordsAsync(IEnumerable<PriceRecord> records, CancellationToken ct = default)
    {
        await _context.Prices.AddRangeAsync(records, ct);
        await _context.SaveChangesAsync(ct);
    }
    
    public async Task<IReadOnlyList<PriceRecord>> GetPricesBetweenAsync(
        DateTimeOffset start, 
        DateTimeOffset end, 
        CancellationToken ct = default)
    {
        return await _context.Prices
            .AsNoTracking()
            .Where(p => p.TimestampUtc >= start && p.TimestampUtc < end)
            .OrderBy(p => p.TimestampUtc)
            .ToListAsync(ct);
    }

    public async Task DeletePriceRecordsAsync(IEnumerable<PriceRecord> records, CancellationToken ct = default)
    {
        // [FIXED] Bypassed EF Core tracking entirely. Calling RemoveRange on AsNoTracking records 
        // forces the CPU to individually attach thousands of records, causing a massive spike.
        // Instead, we extract the IDs and execute a bulk delete directly on the database.
        var ids = records.Select(r => r.Id).ToList();
        
        if (ids.Count == 0) return;

        // Chunking into groups of 500 to stay well under SQLite's expression tree limit for IN (...) clauses
        foreach (var chunk in ids.Chunk(500))
        {
            await _context.Prices
                .Where(p => chunk.Contains(p.Id))
                .ExecuteDeleteAsync(ct);
        }
    }
    
    public async Task<Dictionary<string, decimal>> GetPreviousClosePricesAsync(string source, DateTimeOffset cutoff, CancellationToken ct = default)
    {
        // Finds records before the cutoff time, groups them by asset code, 
        // and selects the most recent record (highest timestamp) for each group.
        var previousCloses = await _context.Prices
            .Where(p => p.Source == source && p.TimestampUtc < cutoff)
            .GroupBy(p => p.ProductCode)
            .Select(g => g.OrderByDescending(p => p.TimestampUtc).First())
            .ToDictionaryAsync(p => p.ProductCode, p => p.AskPrice, ct);

        return previousCloses;
    }
    
    public async Task<PricePointDto?> GetLastPriceBeforeAsync(string productCode, DateTimeOffset targetDate, CancellationToken ct = default)
    {
        // Fetches the single most recent record for the specific asset before the target date
        var record = await _context.Prices
            .AsNoTracking()
            .Where(p => p.ProductCode == productCode && p.TimestampUtc < targetDate)
            .OrderByDescending(p => p.TimestampUtc)
            .FirstOrDefaultAsync(ct);

        if (record == null)
            return null;

        // Map the database entity to the DTO expected by the service
        return new PricePointDto(record.TimestampUtc, record.AskPrice);
    }
}
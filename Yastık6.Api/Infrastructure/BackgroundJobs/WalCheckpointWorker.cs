using Yastık6.Api.Infrastructure.Persistence;

namespace Yastık6.Api.Infrastructure.BackgroundJobs;

// Keeps the SQLite WAL small. A multi-GB WAL makes every restart slow, and a start that gets killed
// while it is still running Migrate() leaves the migration lock behind.
public sealed class WalCheckpointWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<WalCheckpointWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup (migration, seeding) finish before the first checkpoint.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var (busy, walFrames, checkpointedFrames) = SqliteMaintenance.CheckpointWal(context);
                logger.LogInformation(
                    "WAL checkpoint finished. Busy={Busy} WalFrames={WalFrames} CheckpointedFrames={CheckpointedFrames}",
                    busy, walFrames, checkpointedFrames);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "WAL checkpoint failed; will retry next cycle.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}

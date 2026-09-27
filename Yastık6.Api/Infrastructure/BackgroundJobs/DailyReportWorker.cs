using System.Text;
using Yastık6.Api.Infrastructure.Alerts;

namespace Yastık6.Api.Infrastructure.BackgroundJobs;

/// <summary>
/// Her gece 23:55'te uyanıp AlertTracker (Akıllı Defter) üzerindeki günlük 
/// hata vukuatlarını toplayan ve Telegram'a asayiş raporu olarak atan Gece Bekçisi.
/// </summary>
public sealed class DailyReportWorker : BackgroundService
{
    private readonly ILogger<DailyReportWorker> _logger;
    private readonly IAlertService _alertService;
    private readonly AlertTracker _tracker;

    public DailyReportWorker(
        ILogger<DailyReportWorker> logger,
        IAlertService alertService,
        AlertTracker tracker)
    {
        _logger = logger;
        _alertService = alertService;
        _tracker = tracker;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Daily Report Worker started. Nightly reports scheduled.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                var nextRun = now.Date.AddDays(1).AddMinutes(-5);

                // [BUG FIX]: Eşitlik durumu (>=) eklendi
                if (now >= nextRun)
                {
                    nextRun = nextRun.AddDays(1);
                }

                var delay = nextRun - now;

                // [BUG FIX]: Kuantum döngüsünü (milisaniye spamını) önlemek için güvenlik sigortası
                if (delay.TotalSeconds < 1)
                {
                    delay = TimeSpan.FromSeconds(1);
                }

                _logger.LogInformation("Night Watcher going to sleep. Next wake-up time: {NextRun}", nextRun.ToString("HH:mm"));

                await Task.Delay(delay, stoppingToken);

                var (active, resolved) = _tracker.GetAndClearDailySummary();

                var reportBuilder = new StringBuilder();
                reportBuilder.AppendLine("📊 *Yastık6 Gün Sonu Asayiş Raporu*\n");

                if (active.Count > 0 || resolved.Count > 0)
                {
                    if (active.Count > 0)
                    {
                        reportBuilder.AppendLine("🔥 *Gün Sonu Devam Eden Krizler:*");
                        foreach (var item in active.OrderByDescending(x => x.Value).Take(40))
                        {
                            reportBuilder.AppendLine($"- `{item.Key}` : {item.Value} kez patladı.");
                        }
                        if (active.Count > 40) reportBuilder.AppendLine($"_...ve {active.Count - 40} asset daha._");
                        reportBuilder.AppendLine();
                    }

                    if (resolved.Count > 0)
                    {
                        reportBuilder.AppendLine("✅ *Gün İçinde Kendi Kendine Düzelenler:*");
                        foreach (var item in resolved.OrderByDescending(x => x.Value).Take(40))
                        {
                            reportBuilder.AppendLine($"- `{item.Key}` : {item.Value} hatadan sonra düzeldi.");
                        }
                        if (resolved.Count > 40) reportBuilder.AppendLine($"_...ve {resolved.Count - 40} asset daha._");
                        reportBuilder.AppendLine();
                    }
                }
                else
                {
                    // [YENİ]: Artık defter boş olsa bile sessiz kalmayacak, Telegram'a rapor geçecek.
                    reportBuilder.AppendLine("✅ *Bugün raporlanacak hiçbir vukuat olmadı.*\n");
                }

                reportBuilder.AppendLine("_Sistem stabil. Kayıt defteri yarına hazır. İyi geceler patron._");

                await _alertService.SendAlertAsync(reportBuilder.ToString(), null, stoppingToken);
                _logger.LogInformation("End-of-day incident report successfully sent to Telegram.");
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error occurred while preparing the end-of-day report.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }
}

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
        _logger.LogInformation("🌙 Daily Report Worker (Gece Bekçisi) göreve başladı. Gece raporları ayarlandı.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                var nextRun = now.Date.AddDays(1).AddMinutes(-5); 
                
                if (now > nextRun)
                {
                    nextRun = nextRun.AddDays(1); 
                }

                var delay = nextRun - now;
                _logger.LogInformation("Gece Bekçisi uykuya dalıyor. Uyanış zamanı: {NextRun}", nextRun.ToString("HH:mm"));
                
                await Task.Delay(delay, stoppingToken);

                // [DÜZELTİLDİ]: Artık defter bize iki ayrı liste dönüyor (Tuple)
                var (active, resolved) = _tracker.GetAndClearDailySummary();

                if (active.Count > 0 || resolved.Count > 0)
                {
                    var reportBuilder = new StringBuilder();
                    reportBuilder.AppendLine("📊 *Yastık6 Gün Sonu Asayiş Raporu*\n");

                    if (active.Count > 0)
                    {
                        reportBuilder.AppendLine("🔥 *Gün Sonu Devam Eden Krizler:*");
                        // Limit 40 olarak eklendi
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

                    reportBuilder.AppendLine("_Sistem stabil. Kayıt defteri yarına hazır. İyi geceler._");

                    await _alertService.SendAlertAsync(reportBuilder.ToString(), null, stoppingToken);
                    _logger.LogInformation("Gün sonu vukuat raporu Telegram'a başarıyla iletildi.");
                }
                else
                {
                    _logger.LogInformation("Bugün raporlanacak hiçbir vukuat olmadı. Sistem kusursuz çalıştı.");
                }
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gün sonu raporu hazırlanırken beklenmeyen bir hata oluştu.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }
}
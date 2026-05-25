using System.Text;
using Yastık6.Api.Infrastructure.Alerts;

namespace Yastık6.Api.Infrastructure.BackgroundJobs;

/// <summary>
/// Her gece 23:55'te uyanıp AlertTracker (Akıllı Defter) üzerindeki günlük susturulan
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
        _tracker = tracker; // Bekçiye Akıllı Defterin yerini gösteriyoruz
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🌙 Daily Report Worker (Gece Bekçisi) göreve başladı. Gece raporları ayarlandı.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. ADIM: SAAT KURMA
                // Şu anki zamana bakıp, gece 23:55'e tam olarak ne kadar saniye/dakika kaldığını hesaplarız.
                var now = DateTime.Now;
                var nextRun = now.Date.AddDays(1).AddMinutes(-5); // Bugünün gece yarısı (00:00) eksi 5 dakika = 23:55
                
                // Eğer uygulama saat 23:56'da başlatılmışsa, bekçi "Aaa saat geçmiş" demesin, saati ertesi gün 23:55'e kursun.
                if (now > nextRun)
                {
                    nextRun = nextRun.AddDays(1); 
                }

                var delay = nextRun - now;
                _logger.LogInformation("Gece Bekçisi uykuya dalıyor. Uyanış zamanı: {NextRun}", nextRun.ToString("HH:mm"));
                
                // Bekçiyi o saate kadar derin uykuya yatırıyoruz (RAM'i ve CPU'yu hiç yormaz)
                await Task.Delay(delay, stoppingToken);

                // 2. ADIM: UYANIŞ VE RAPORLAMA (Saat 23:55 oldu)
                // Bekçi uyanır uyanmaz sekreterin masasındaki defteri alır, kopyasını çıkarır ve defterin sayfalarını yırtar (sıfırlar).
                var summary = _tracker.GetAndClearDailySummary();

                // Eğer defterde kayıtlı vukuat varsa (yani gün içinde hatalar susturulmuşsa)
                if (summary.Count > 0)
                {
                    var reportBuilder = new StringBuilder();
                    reportBuilder.AppendLine("📊 *Yastık6 Gün Sonu Asayiş Raporu*");
                    reportBuilder.AppendLine("Bugün yaşanan ve otomatik susturulan anormallikler:\n");

                    // Hataları en çok sayıdan en aza doğru sıralayıp listeye ekler
                    foreach (var item in summary.OrderByDescending(x => x.Value))
                    {
                        reportBuilder.AppendLine($"- `{item.Key}` : {item.Value} kez engellendi.");
                    }

                    reportBuilder.AppendLine("\n_Sistem stabil. Kayıt defteri yarına hazır. İyi geceler._");

                    // Raporu sekretere verip Telegram'a postalatıyoruz (Parmak izi olarak null geçiyoruz ki bu mesajı susturmasın)
                    await _alertService.SendAlertAsync(reportBuilder.ToString(), null, stoppingToken);
                    _logger.LogInformation("Gün sonu vukuat raporu Telegram'a başarıyla iletildi.");
                }
                else
                {
                    // Defter boşsa Telegram'ı yormaya gerek yok, sadece arka planda (Seq'e) her şeyin yolunda olduğunu yazıyoruz.
                    _logger.LogInformation("Bugün raporlanacak susturulmuş bir hata olmadı. Sistem kusursuz çalıştı.");
                }
            }
            catch (TaskCanceledException)
            {
                // Uygulama kapanırken bekçi uykudan uyanırsa sessizce işi bırakır.
                break;
            }
            catch (Exception ex)
            {
                // Bekçi kendi işini yaparken takılıp düşerse 5 dakika bekle ve tekrar dene.
                _logger.LogError(ex, "Gün sonu raporu hazırlanırken beklenmeyen bir hata oluştu.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }
}
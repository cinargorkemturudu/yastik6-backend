using System.Text;
using System.Text.Json;
using Yastık6.Api.Infrastructure.Alerts;

namespace Yastık6.Api.Infrastructure.BackgroundJobs;

/// <summary>
/// Telegram'dan gelen komutları Long Polling mantığıyla dinleyen,
/// güvenlik onayı yapan ve patrona anlık rapor dönen Telsiz Operatörü.
/// </summary>
public sealed class TelegramCommandListener : BackgroundService
{
    private readonly HttpClient _httpClient;
    private readonly string _botToken;
    private readonly string _expectedChatId;
    private readonly ILogger<TelegramCommandListener> _logger;
    private readonly AlertTracker _tracker;
    private readonly IAlertService _alertService;

    public TelegramCommandListener(
        HttpClient httpClient,
        IConfiguration config,
        ILogger<TelegramCommandListener> logger,
        AlertTracker tracker,
        IAlertService alertService)
    {
        _httpClient = httpClient;
        _botToken = config["Telegram:BotToken"] ?? string.Empty;
        _expectedChatId = config["Telegram:ChatId"] ?? string.Empty;
        _logger = logger;
        _tracker = tracker;
        _alertService = alertService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_botToken) || string.IsNullOrWhiteSpace(_expectedChatId))
        {
            _logger.LogWarning("Telegram Listener başlatılamadı: Token veya ChatId eksik.");
            return;
        }

        _logger.LogInformation("📻 Telsiz Operatörü (Command Listener) frekansı dinlemeye başladı.");
        
        long offset = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var url = $"https://api.telegram.org/bot{_botToken}/getUpdates?offset={offset}&timeout=30";
                
                var response = await _httpClient.GetAsync(url, stoppingToken);
                if (!response.IsSuccessStatusCode)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                var jsonString = await response.Content.ReadAsStringAsync(stoppingToken);
                using var doc = JsonDocument.Parse(jsonString);
                var root = doc.RootElement;

                if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean() && root.TryGetProperty("result", out var results))
                {
                    foreach (var update in results.EnumerateArray())
                    {
                        if (update.TryGetProperty("update_id", out var updateIdElement))
                        {
                            offset = updateIdElement.GetInt64() + 1;
                        }

                        if (update.TryGetProperty("message", out var message) &&
                            message.TryGetProperty("chat", out var chat) &&
                            chat.TryGetProperty("id", out var chatIdElement) &&
                            message.TryGetProperty("text", out var textElement))
                        {
                            var chatId = chatIdElement.GetInt64().ToString();
                            var text = textElement.GetString();

                            if (chatId == _expectedChatId && text?.Trim().ToLower() == "/rapor")
                            {
                                _logger.LogInformation("Patrondan /rapor komutu alındı. Anlık durum iletiliyor...");
                                await SendSnapshotReportAsync(stoppingToken);
                            }
                            else if (chatId != _expectedChatId)
                            {
                                _logger.LogWarning("🚨 Tanımlanmayan bir kaynaktan bota mesaj geldi! ChatId: {ChatId}, Metin: {Text}", chatId, text);
                            }
                        }
                    }
                }
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telsiz Operatörü Telegram'ı dinlerken takıldı.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task SendSnapshotReportAsync(CancellationToken ct)
    {
        // [DÜZELTİLDİ]: Tuple parçalaması
        var (active, resolved) = _tracker.GetCurrentSnapshot();

        var reportBuilder = new StringBuilder();
        reportBuilder.AppendLine("📡 *Yastık6 Telsiz - Kapsamlı Durum Raporu*\n");

        // [VIP LOCASI]: Altyapı Hataları (SYS_ veya CRITICAL_ ile başlayanlar)
        var sysErrors = active.Where(x => x.Key.StartsWith("SYS_") || x.Key.StartsWith("CRITICAL_")).ToList();
        var dataErrors = active.Where(x => !x.Key.StartsWith("SYS_") && !x.Key.StartsWith("CRITICAL_")).ToList();

        if (sysErrors.Any())
        {
            reportBuilder.AppendLine("🚨 *ALTYAPI VE SAĞLIK DURUMU (KRİTİK):*");
            foreach (var item in sysErrors.OrderByDescending(x => x.Value))
            {
                reportBuilder.AppendLine($"- `{item.Key}` : {item.Value} kez uyarı verdi!");
            }
            reportBuilder.AppendLine();
        }
        else
        {
            reportBuilder.AppendLine("✅ *Altyapı:* Tüm motorlar %100 sağlıklı çalışıyor.\n");
        }

        // AKTİF VERİ KRİZLERİ
        if (dataErrors.Any())
        {
            reportBuilder.AppendLine($"🔥 *Devam Eden Veri Krizleri:* Toplam {dataErrors.Count} asset hata veriyor.");
            foreach (var item in dataErrors.OrderByDescending(x => x.Value).Take(40)) // Take(40) Sınırı
            {
                reportBuilder.AppendLine($"- `{item.Key}` : {item.Value} kez patladı.");
            }
            
            if (dataErrors.Count > 40) reportBuilder.AppendLine($"_...ve listelenmeyen {dataErrors.Count - 40} asset daha._");
            reportBuilder.AppendLine();
        }

        // KENDİ KENDİNE DÜZELENLER
        if (resolved.Count > 0)
        {
            reportBuilder.AppendLine($"⚕️ *Kendi Kendine Düzelenler:* Toplam {resolved.Count} asset normale döndü.");
            foreach (var item in resolved.OrderByDescending(x => x.Value).Take(40))
            {
                reportBuilder.AppendLine($"- `{item.Key}` : {item.Value} hatadan sonra düzeldi.");
            }
            
            if (resolved.Count > 40) reportBuilder.AppendLine($"_...ve listelenmeyen {resolved.Count - 40} asset daha._");
            reportBuilder.AppendLine();
        }

        if (active.Count == 0 && resolved.Count == 0)
        {
            reportBuilder.AppendLine("Şu ana kadar raporlanacak bir vukuat gerçekleşmedi.\n_Sistem kusursuz._");
        }
        else
        {
            reportBuilder.AppendLine("_Sistem aktif olarak izleniyor._");
        }

        await _alertService.SendAlertAsync(reportBuilder.ToString(), string.Empty, ct);
    }
}
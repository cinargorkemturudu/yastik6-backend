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
        
        // Telegram'a "Bana sadece şu ID'den sonraki mesajları ver" demek için offset tutuyoruz.
        long offset = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // timeout=30 ile Long Polling yapıyoruz. Sunucu 30 sn boyunca mesaj gelmesini bekler, yorulmaz.
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
                            // Bir sonraki döngüde aynı mesajları tekrar okumamak için offset'i 1 ileri kaydırıyoruz.
                            offset = updateIdElement.GetInt64() + 1;
                        }

                        // Mesajı, kimin attığını (chat_id) ve metnini güvenli şekilde ayıklıyoruz.
                        if (update.TryGetProperty("message", out var message) &&
                            message.TryGetProperty("chat", out var chat) &&
                            chat.TryGetProperty("id", out var chatIdElement) &&
                            message.TryGetProperty("text", out var textElement))
                        {
                            var chatId = chatIdElement.GetInt64().ToString();
                            var text = textElement.GetString();

                            // SINIR GÜVENLİĞİ: Gelen ID, bizim appsettings'te patron diye kaydettiğimiz ID mi?
                            if (chatId == _expectedChatId && text?.Trim().ToLower() == "/rapor")
                            {
                                _logger.LogInformation("Patrondan /rapor komutu alındı. Anlık durum iletiliyor...");
                                await SendSnapshotReportAsync(stoppingToken);
                            }
                            else if (chatId != _expectedChatId)
                            {
                                // Yabancı biri bota yazarsa asla cevap verme, sadece logla.
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
        // Defteri SIFIRLAMADAN sadece o anki fotoğrafını çekiyoruz
        var snapshot = _tracker.GetCurrentSnapshot();

        var reportBuilder = new StringBuilder();
        reportBuilder.AppendLine("📡 *Yastık6 Telsiz - Anlık Durum Raporu*");
        
        if (snapshot.Count > 0)
        {
            reportBuilder.AppendLine("Şu ana kadar susturulan anormallikler:\n");
            foreach (var item in snapshot.OrderByDescending(x => x.Value))
            {
                reportBuilder.AppendLine($"- `{item.Key}` : {item.Value} kez engellendi.");
            }
            reportBuilder.AppendLine("\n_Sistem aktif olarak izleniyor._");
        }
        else
        {
            reportBuilder.AppendLine("\nŞu ana kadar raporlanacak bir vukuat gerçekleşmedi.\n_Her şey yolunda patron._");
        }

        // Telsiz cevabının susturulmaması için cacheKey (parmak izi) kısmını null geçiyoruz.
        await _alertService.SendAlertAsync(reportBuilder.ToString(), string.Empty, ct);    }
}
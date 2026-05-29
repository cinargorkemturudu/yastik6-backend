using System.Text;
using System.Text.Json;

namespace Yastık6.Api.Infrastructure.Alerts;

public sealed class TelegramAlertService : IAlertService
{
    private readonly HttpClient _client;
    private readonly string _botToken;
    private readonly string _chatId;
    private readonly ILogger<TelegramAlertService> _logger;
    private readonly AlertTracker _tracker; 

    public TelegramAlertService(
        HttpClient client, 
        IConfiguration config, 
        ILogger<TelegramAlertService> logger, 
        AlertTracker tracker) 
    {
        _client = client;
        _logger = logger;
        _botToken = config["Telegram:BotToken"] ?? string.Empty;
        _chatId = config["Telegram:ChatId"] ?? string.Empty;
        _tracker = tracker; 
    }

    public async Task SendAlertAsync(string message, string cacheKey = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_botToken) || string.IsNullOrWhiteSpace(_chatId)) return;

        // Akıllı Defter Kontrolü: Bu hatayı daha önce attık mı?
        if (!string.IsNullOrEmpty(cacheKey) && !_tracker.ShouldSendAlert(cacheKey))
        {
            _logger.LogInformation("Sekreter engelledi: '{CacheKey}' bildirimi gün dönümüne kadar susturuldu ve sayaca eklendi.", cacheKey);
            return;
        }

        try
        {
            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            
            var payload = new 
            { 
                chat_id = _chatId, 
                text = $"🚨 *Yastık6 Watchdog* 🚨\n\n{message}", 
                parse_mode = "Markdown" 
            };
            
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            // [MİMARİ DOKUNUŞ]: Ölüm Çığlığı (Death Cry) Sigortası
            // Uygulamanın orijinal kapanma sinyalini (ct) görmezden geliyoruz.
            // Sadece bu HTTP isteğine özel 5 saniyelik bağımsız bir yaşam hakkı tanıyoruz.
            using var deathCryCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var response = await _client.PostAsync(url, content, deathCryCts.Token);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Watchdog havladı ama sesi Telegram'a ulaşmadı! Status: {StatusCode}", response.StatusCode);
            }
        }
        catch (TaskCanceledException)
        {
            // Eğer 5 saniye içinde Telegram cevap vermezse, dükkanı daha fazla bekletmeden çıkıyoruz.
            _logger.LogWarning("Ölüm Çığlığı 5 saniye içinde atılamadı, sistem kapanışına saygı duyularak işlem iptal edildi.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watchdog Telegram servisinde beklenmeyen bir hata.");
        }
    }
}
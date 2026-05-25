using System.Text;
using System.Text.Json;

namespace Yastık6.Api.Infrastructure.Alerts;

public sealed class TelegramAlertService : IAlertService
{
    private readonly HttpClient _client;
    private readonly string _botToken;
    private readonly string _chatId;
    private readonly ILogger<TelegramAlertService> _logger;
    
    // [DEĞİŞTİRİLDİ]: IMemoryCache (Geçici Çip) uçuruldu. 
    // Yerine listeleme ve sayma yeteneği olan kendi yazdığımız Akıllı Defter (AlertTracker) geldi.
    private readonly AlertTracker _tracker; 

    public TelegramAlertService(
        HttpClient client, 
        IConfiguration config, 
        ILogger<TelegramAlertService> logger, 
        AlertTracker tracker) // [DEĞİŞTİRİLDİ]
    {
        _client = client;
        _logger = logger;
        _botToken = config["Telegram:BotToken"] ?? string.Empty;
        _chatId = config["Telegram:ChatId"] ?? string.Empty;
        _tracker = tracker; // [DEĞİŞTİRİLDİ]
    }

    public async Task SendAlertAsync(string message, string cacheKey = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_botToken) || string.IsNullOrWhiteSpace(_chatId)) return;

        // [DEĞİŞTİRİLDİ]: Eskiden burada 30 dakika hesabı yapıyorduk. 
        // Artık topu tamamen Akıllı Deftere atıyoruz. "Bu hatayı bugün atayım mı?" diye soruyoruz.
        // Eğer defter "Hayır, ben bunu bugün attım, sayacı da 1 artırdım" derse sessizce çıkıyoruz.
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

            var response = await _client.PostAsync(url, content, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Watchdog havladı ama sesi Telegram'a ulaşmadı! Status: {StatusCode}", response.StatusCode);
            }
            
            // [SİLİNDİ]: Eskiden mesaj başarıyla gidince burada IMemoryCache'e 30 dakikalık kilit vuruyorduk.
            // Artık buna gerek kalmadı çünkü '_tracker.ShouldSendAlert' metodu ilk defa 'Evet' dediğinde
            // defter o hatanın tarihini otomatik olarak kendi içine kaydediyor. Kodumuz kısaldı ve temizlendi!
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watchdog Telegram servisinde beklenmeyen bir hata.");
        }
    }
}
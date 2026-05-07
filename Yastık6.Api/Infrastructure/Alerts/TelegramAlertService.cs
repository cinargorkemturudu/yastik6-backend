using System.Text;
using System.Text.Json;

namespace Yastık6.Api.Infrastructure.Alerts;

public sealed class TelegramAlertService : IAlertService
{
    private readonly HttpClient _client;
    private readonly string _botToken;
    private readonly string _chatId;
    private readonly ILogger<TelegramAlertService> _logger;

    public TelegramAlertService(HttpClient client, IConfiguration config, ILogger<TelegramAlertService> logger)
    {
        _client = client;
        _logger = logger;
        _botToken = config["Telegram:BotToken"] ?? string.Empty;
        _chatId = config["Telegram:ChatId"] ?? string.Empty;
    }

    public async Task SendAlertAsync(string message, CancellationToken ct = default)
    {
        // Şifreler girilmemişse sistemi yorma, sessizce dön
        if (string.IsNullOrWhiteSpace(_botToken) || string.IsNullOrWhiteSpace(_chatId)) return;

        try
        {
            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            
            // Markdown formatında havalı bir mesaj paketi hazırlıyoruz
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watchdog Telegram servisinde beklenmeyen bir hata.");
        }
    }
}
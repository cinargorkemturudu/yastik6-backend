using System.Collections.Concurrent;

namespace Yastık6.Api.Infrastructure.Alerts;

public sealed class AlertTracker
{
    // Aktif olarak hata veren assetler ve toplam hata sayıları
    private readonly ConcurrentDictionary<string, int> _activeAlerts = new();
    
    // Düzelen assetler ve düzelmeden önceki toplam hata sayıları (Rehabilitasyon)
    private readonly ConcurrentDictionary<string, int> _resolvedAlerts = new();
    
    // Seq/Telegram spamını engellemek için son bildirim zamanları
    private readonly ConcurrentDictionary<string, DateTime> _lastAlertSent = new();

    // 1 saatlik susturucu süresi
    private readonly TimeSpan _silenceDuration = TimeSpan.FromHours(1);

    public bool ShouldSendAlert(string cacheKey)
    {
        if (string.IsNullOrEmpty(cacheKey)) return false;

        // Hatayı sayaca ekle (ilk kez geliyorsa 1 yap, varsa 1 artır)
        _activeAlerts.AddOrUpdate(cacheKey, 1, (_, count) => count + 1);

        var now = DateTime.UtcNow;

        // Eğer daha önce alert gönderildiyse ve 1 saat geçmediyse sus (false dön)
        if (_lastAlertSent.TryGetValue(cacheKey, out var lastSent))
        {
            if ((now - lastSent) < _silenceDuration)
            {
                return false;
            }
        }

        // İlk kez geliyorsa veya 1 saat dolduysa süreyi güncelle ve konuş (true dön)
        _lastAlertSent[cacheKey] = now;
        return true;
    }

    // [YENİ] Doğru veri geldiğinde sabıkayı temizle
    public bool MarkAsResolved(string cacheKey)
    {
        if (string.IsNullOrEmpty(cacheKey)) return false;

        // Eğer aktif sabıkalılarda varsa, oradan sil ve düzelenlere ekle
        if (_activeAlerts.TryRemove(cacheKey, out var pastFailCount))
        {
            _resolvedAlerts.TryAdd(cacheKey, pastFailCount);
            
            // Alert süresini de temizle ki ileride tekrar bozulursa anında haber versin
            _lastAlertSent.TryRemove(cacheKey, out _);
            
            return true; // "Evet, bu yeni düzeldi, Seq'e müjdeyi verebilirsin" anlamında
        }

        return false; // Zaten düzgündü veya listede yoktu
    }

    // [GÜNCELLENDİ] Telsiz raporu için her iki durumu da dön
    public (Dictionary<string, int> Active, Dictionary<string, int> Resolved) GetCurrentSnapshot()
    {
        return (
            new Dictionary<string, int>(_activeAlerts),
            new Dictionary<string, int>(_resolvedAlerts)
        );
    }

    // [GÜNCELLENDİ] Gece yarısı resetlendiğinde her iki listeyi de dön ve temizle
    public (Dictionary<string, int> Active, Dictionary<string, int> Resolved) GetAndClearDailySummary()
    {
        var summaryActive = new Dictionary<string, int>(_activeAlerts);
        var summaryResolved = new Dictionary<string, int>(_resolvedAlerts);
        
        _activeAlerts.Clear();
        _resolvedAlerts.Clear();
        _lastAlertSent.Clear();
        
        return (summaryActive, summaryResolved);
    }
}
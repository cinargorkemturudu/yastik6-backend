using System.Collections.Concurrent;

namespace Yastık6.Api.Infrastructure.Alerts;

public sealed class AlertTracker
{
    private readonly ConcurrentDictionary<string, int> _supressedAlerts = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastAlertSent = new();

    public bool ShouldSendAlert(string cacheKey)
    {
        var today = DateTime.Today;

        if (_lastAlertSent.TryGetValue(cacheKey, out var lastSent) && lastSent.Date == today)
        {
            _supressedAlerts.AddOrUpdate(cacheKey, 1, (_, count) => count + 1);
            return false;
        }
        _lastAlertSent[cacheKey] = DateTime.Now;
        return true;
    }

    public Dictionary<string, int> GetCurrentSnapshot()
    {
        return new Dictionary<string, int>(_supressedAlerts);
    }

    public Dictionary<string, int> GetAndClearDailySummary()
    {
        var summary = new Dictionary<string, int>(_supressedAlerts);
        
        _supressedAlerts.Clear();
        _lastAlertSent.Clear();
        return summary;
    }
}
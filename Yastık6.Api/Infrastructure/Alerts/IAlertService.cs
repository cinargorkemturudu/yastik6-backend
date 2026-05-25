namespace Yastık6.Api.Infrastructure.Alerts;

public interface IAlertService
{
    Task SendAlertAsync(string message, string cacheKey = null, CancellationToken ct = default);
}
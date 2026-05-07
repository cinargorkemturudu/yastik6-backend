namespace Yastık6.Api.Infrastructure.Alerts;

public interface IAlertService
{
    Task SendAlertAsync(string message, CancellationToken ct = default);
}
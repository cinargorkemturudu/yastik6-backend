using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using Yastık6.Api.Infrastructure.Alerts; // [EKLENDİ]: Bildirim kontratına erişim sağlandı

namespace Yastık6.Api.Infrastructure.ExceptionHandling;

public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly IAlertService _alertService; // [EKLENDİ]: Sekreter servis tanımı

    // [EKLENDİ]: Hata hunisinin içerisine Telegram bildirim motoru enjekte edildi
    public GlobalExceptionHandler(IAlertService alertService)
    {
        _alertService = alertService;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        Log.Error(exception, "API'de yakalanmayan bir hata oluştu: {Message}", exception.Message);

        // [EKLENDİ]: Zamanla değişen metinler yerine hatanın saf sınıf ismi (örn: SqliteException, NullReferenceException) parmak izi yapıldı
        var exceptionTypeName = exception.GetType().Name;

        // [EKLENDİ]: Telegram bildirimi, zaman damgalarından arındırılmış bu çelik gibi parmak iziyle birlikte fırlatıldı
        await _alertService.SendAlertAsync(
            $"*Hata Tipi:* {exceptionTypeName}\n*Mesaj:* {exception.Message}\n*Path:* {httpContext.Request.Path}", 
            exceptionTypeName, 
            cancellationToken);

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Sunucu Hatası",
            Detail = "İşleminiz sırasında beklenmeyen bir hata oluştu. Lütfen daha sonra tekrar deneyin.",
            Instance = httpContext.Request.Path
        };

        httpContext.Response.StatusCode = problemDetails.Status.Value;

        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

        return true; 
    }
}
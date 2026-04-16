using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace Yastık6.Api.Infrastructure.ExceptionHandling;

public sealed class GlobalExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // 1. Hatayı arka planda Serilog ile dosyaya ve konsola kaydediyoruz ki biz ne olduğunu bilelim
        Log.Error(exception, "API'de yakalanmayan bir hata oluştu: {Message}", exception.Message);

        // 2. Kullanıcıya (Flutter'a) döneceğimiz standart ve güvenli JSON şablonu
        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Sunucu Hatası",
            Detail = "İşleminiz sırasında beklenmeyen bir hata oluştu. Lütfen daha sonra tekrar deneyin.",
            Instance = httpContext.Request.Path
        };

        httpContext.Response.StatusCode = problemDetails.Status.Value;

        // 3. Yanıtı JSON olarak basıyoruz
        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

        // true dönerek ".NET'e bu hatayı ben hallettim, sen kendi çirkin HTML sayfanı basma" diyoruz.
        return true; 
    }
}
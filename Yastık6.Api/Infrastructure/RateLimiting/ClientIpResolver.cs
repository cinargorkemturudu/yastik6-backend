using System.Net;

namespace Yastık6.Api.Infrastructure.RateLimiting;

public static class ClientIpResolver
{
    // Production sits behind Cloudflare -> nginx, so RemoteIpAddress is a Cloudflare edge IP shared by
    // many visitors. Cloudflare puts the real visitor IP in CF-Connecting-IP. The header could be forged
    // by someone reaching nginx directly, which is why the public tier also has a global cap.
    public const string CloudflareHeader = "CF-Connecting-IP";

    public static string Resolve(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(CloudflareHeader, out var header) &&
            IPAddress.TryParse(header.ToString(), out var cloudflareIp))
        {
            return cloudflareIp.ToString();
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}

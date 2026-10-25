using System.Net;
using System.Net.Sockets;

namespace Yastık6.Api.Infrastructure.RateLimiting;

public static class ClientIpResolver
{
    // Production sits behind Cloudflare -> nginx, so RemoteIpAddress is a Cloudflare edge IP shared by
    // many visitors. Cloudflare puts the real visitor IP in CF-Connecting-IP. The header could be forged
    // by someone reaching nginx directly, which is why the public tier also has a global cap.
    public const string CloudflareHeader = "CF-Connecting-IP";

    public static IPAddress? Resolve(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(CloudflareHeader, out var header) &&
            IPAddress.TryParse(header.ToString(), out var cloudflareIp))
        {
            return cloudflareIp;
        }

        return context.Connection.RemoteIpAddress;
    }

    // One rate-limit bucket per visitor. A single IPv6 subscriber usually owns a whole /64
    // (18 quintillion addresses), so per-address buckets would let them rotate past the limit.
    public static string PartitionKey(HttpContext context)
    {
        var ip = Resolve(context);
        if (ip == null) return "unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return ip.ToString();

        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }
}

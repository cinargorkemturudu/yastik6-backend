namespace Yastık6.Api.Infrastructure.RateLimiting;

public static class RateLimitPolicies
{
    // Mobile app traffic (keyed routes).
    public const string MobileApp = "PublicApi";

    // Open, keyless routes under /api/public for people evaluating the project.
    public const string PublicDemo = "PublicDemo";

    public const string PublicPathPrefix = "/api/public";
}

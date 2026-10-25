namespace Yastık6.Api.Infrastructure.RateLimiting;

public static class RateLimitPolicies
{
    // Mobile app traffic (keyed routes).
    public const string MobileApp = "PublicApi";

    // Open, keyless routes for people evaluating the project; limited by PublicDemoLimiter.
    public const string PublicPathPrefix = "/api/public";
}

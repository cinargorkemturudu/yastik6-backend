using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Yastık6.Api.Infrastructure.RateLimiting;

namespace Yastık6.Api.Tests.Infrastructure;

public class ClientIpResolverTests
{
    private static DefaultHttpContext Context(string? cloudflareIp, string remoteIp = "172.68.1.1")
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        if (cloudflareIp != null) context.Request.Headers[ClientIpResolver.CloudflareHeader] = cloudflareIp;
        return context;
    }

    [Fact]
    public void Resolve_PrefersCloudflareHeader_OverEdgeIp()
    {
        ClientIpResolver.Resolve(Context("203.0.113.7")).Should().Be("203.0.113.7");
    }

    [Fact]
    public void Resolve_AcceptsIpv6Header()
    {
        ClientIpResolver.Resolve(Context("2001:db8::1")).Should().Be("2001:db8::1");
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("")]
    [InlineData("203.0.113.7, 10.0.0.1")]
    public void Resolve_FallsBackToConnectionIp_WhenHeaderIsNotASingleIp(string header)
    {
        ClientIpResolver.Resolve(Context(header)).Should().Be("172.68.1.1");
    }

    [Fact]
    public void Resolve_UsesConnectionIp_WhenHeaderIsMissing()
    {
        ClientIpResolver.Resolve(Context(null)).Should().Be("172.68.1.1");
    }
}

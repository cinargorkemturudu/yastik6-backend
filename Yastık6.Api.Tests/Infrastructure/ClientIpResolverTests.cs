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
    public void PartitionKey_PrefersCloudflareHeader_OverEdgeIp()
    {
        ClientIpResolver.PartitionKey(Context("203.0.113.7")).Should().Be("203.0.113.7");
    }

    [Fact]
    public void PartitionKey_GroupsIpv6ByItsSlash64()
    {
        ClientIpResolver.PartitionKey(Context("2001:db8:aa:bb:1:2:3:4")).Should().Be("2001:db8:aa:bb::/64");
        ClientIpResolver.PartitionKey(Context("2001:db8:aa:bb:ffff::9")).Should().Be("2001:db8:aa:bb::/64");
    }

    [Fact]
    public void PartitionKey_TreatsIpv4MappedIpv6AsIpv4()
    {
        ClientIpResolver.PartitionKey(Context("::ffff:203.0.113.7")).Should().Be("203.0.113.7");
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("")]
    [InlineData("203.0.113.7, 10.0.0.1")]
    public void PartitionKey_FallsBackToConnectionIp_WhenHeaderIsNotASingleIp(string header)
    {
        ClientIpResolver.PartitionKey(Context(header)).Should().Be("172.68.1.1");
    }

    [Fact]
    public void PartitionKey_UsesConnectionIp_WhenHeaderIsMissing()
    {
        ClientIpResolver.PartitionKey(Context(null)).Should().Be("172.68.1.1");
    }
}

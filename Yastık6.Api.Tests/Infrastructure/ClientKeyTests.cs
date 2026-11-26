using FluentAssertions;
using Yastık6.Api.Infrastructure.ClientAuth;

namespace Yastık6.Api.Tests.Infrastructure;

public class ClientKeyTests
{
    [Fact]
    public void Matches_IdenticalKey_ReturnsTrue()
    {
        ClientKey.Matches("test-mobile-key", "test-mobile-key").Should().BeTrue();
    }

    [Theory]
    [InlineData("test-mobile-kez")]   // same length, last character differs
    [InlineData("test-mobile-ke")]    // prefix of the real key
    [InlineData("test-mobile-key-x")] // real key plus extra
    [InlineData("")]
    public void Matches_WrongKey_ReturnsFalse(string provided)
    {
        ClientKey.Matches(provided, "test-mobile-key").Should().BeFalse();
    }

    [Fact]
    public void Matches_NullKey_ReturnsFalse()
    {
        ClientKey.Matches(null, "test-mobile-key").Should().BeFalse();
    }
}

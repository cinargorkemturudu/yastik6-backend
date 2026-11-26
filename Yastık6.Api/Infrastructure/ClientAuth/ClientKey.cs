using System.Security.Cryptography;
using System.Text;

namespace Yastık6.Api.Infrastructure.ClientAuth;

public static class ClientKey
{
    // Constant-time comparison, so response timing can't reveal how many leading characters matched.
    public static bool Matches(string? provided, string expected)
    {
        if (provided is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(expected));
    }
}

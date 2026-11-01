using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nightwave.Server.Security;

/// <summary>Hashes the UI password with PBKDF2. Stored as <c>iterations.salt.hash</c>.</summary>
public static class AdminPassword
{
    public const int MinimumLength = 8;

    private const int Iterations = 310_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);

        return string.Create(CultureInfo.InvariantCulture, $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}");
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');

        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations))
        {
            return false;
        }

        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[1]), iterations, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

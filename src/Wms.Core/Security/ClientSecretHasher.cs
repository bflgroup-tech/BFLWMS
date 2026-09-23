using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Wms.Core.Security;

/// <summary>
/// PBKDF2 hashing for API client secrets. No password-hashing convention exists
/// elsewhere in this codebase (Blazor UI auth is Entra ID/OIDC only, no local
/// credentials), so this follows the same shape as ASP.NET Core Identity's
/// PasswordHasher (version byte + iteration count + salt + subkey, base64)
/// rather than inventing something new.
/// </summary>
public static class ClientSecretHasher
{
    private const int SaltSize = 16;
    private const int SubkeySize = 32;
    private const int Iterations = 100_000;

    /// <summary>Generates a random, URL-safe secret/id token.</summary>
    public static string GenerateToken(int byteLength = 32)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var subkey = Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, SubkeySize);

        var result = new byte[1 + 4 + SaltSize + SubkeySize];
        result[0] = 1;
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(1, 4), Iterations);
        salt.CopyTo(result.AsSpan(5, SaltSize));
        subkey.CopyTo(result.AsSpan(5 + SaltSize, SubkeySize));
        return Convert.ToBase64String(result);
    }

    public static bool Verify(string secret, string hash)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(hash); }
        catch (FormatException) { return false; }

        if (bytes.Length != 1 + 4 + SaltSize + SubkeySize || bytes[0] != 1) return false;

        var iterations = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(1, 4));
        var salt = bytes.AsSpan(5, SaltSize).ToArray();
        var expectedSubkey = bytes.AsSpan(5 + SaltSize, SubkeySize).ToArray();
        var actualSubkey = Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA256, SubkeySize);
        return CryptographicOperations.FixedTimeEquals(actualSubkey, expectedSubkey);
    }
}

using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using MiniWms.Application.Authentication;

namespace MiniWms.Infrastructure.Authentication;

public sealed class OpaqueRefreshTokenCodec : IRefreshTokenCodec
{
    private const int SecretBytes = 32;
    private const int SecretCharacters = 43;
    private const int TokenCharacters = 32 + 1 + 32 + 1 + SecretCharacters;

    public RefreshTokenMaterial Create(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A user identifier is required.", nameof(userId));
        }

        var tokenId = Guid.NewGuid();
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        try
        {
            var encodedSecret = Base64UrlEncoder.Encode(secret);
            var hash = Convert.ToHexString(SHA256.HashData(secret));
            return new RefreshTokenMaterial($"{userId:N}.{tokenId:N}.{encodedSecret}", tokenId, hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public PresentedRefreshToken? Parse(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != TokenCharacters)
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out var userId) || userId == Guid.Empty ||
            !Guid.TryParseExact(parts[1], "N", out var tokenId) || tokenId == Guid.Empty || parts[2].Length != SecretCharacters ||
            parts[2].Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            return null;
        }

        byte[] secret;
        try
        {
            secret = Base64UrlEncoder.DecodeBytes(parts[2]);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }

        try
        {
            if (secret.Length != SecretBytes || !string.Equals(Base64UrlEncoder.Encode(secret), parts[2], StringComparison.Ordinal))
            {
                return null;
            }
            return new PresentedRefreshToken(userId, tokenId, Convert.ToHexString(SHA256.HashData(secret)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public bool Matches(string suppliedHash, string storedHash)
    {
        if (suppliedHash.Length != 64 || storedHash.Length != 64)
        {
            return false;
        }
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(suppliedHash), Convert.FromHexString(storedHash));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

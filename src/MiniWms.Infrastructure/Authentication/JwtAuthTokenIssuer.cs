using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using MiniWms.Application.Authentication;
using MiniWms.Domain.Entities;

namespace MiniWms.Infrastructure.Authentication;

public sealed class JwtAuthTokenIssuer : IAuthTokenIssuer
{
    private readonly JwtSettings _settings;
    private readonly SigningCredentials _credentials;

    public JwtAuthTokenIssuer(JwtSettings settings)
    {
        settings.Validate();
        _settings = settings;
        _credentials = new SigningCredentials(new SymmetricSecurityKey(settings.GetSigningKeyBytes()), SecurityAlgorithms.HmacSha256);
    }

    public IssuedAccessToken Create(AuthUserDetails user, AuthSession session, DateTimeOffset now, TimeSpan accessTokenLifetime)
    {
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
        var requestedExpiry = now.Add(accessTokenLifetime);
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(
            (requestedExpiry < session.ExpiresAt ? requestedExpiry : session.ExpiresAt).ToUnixTimeSeconds());
        if (expiresAt <= issuedAt)
        {
            throw new AuthException(AuthErrorKind.InvalidSession, "The session has expired. Sign in again.");
        }

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.UserId.ToString("D")),
            new Claim("sid", session.SessionId.ToString("D")),
            new Claim("auth_version", session.AuthVersion.ToString(CultureInfo.InvariantCulture)),
            new Claim("role", user.Role),
            new Claim("name", user.FullName),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new Claim(JwtRegisteredClaimNames.Iat, issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
        };
        var token = new JwtSecurityToken(
            _settings.Issuer,
            _settings.Audience,
            claims,
            issuedAt.UtcDateTime,
            expiresAt.UtcDateTime,
            _credentials);
        return new IssuedAccessToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}

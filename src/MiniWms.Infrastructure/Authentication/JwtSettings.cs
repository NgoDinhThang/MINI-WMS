using Microsoft.Extensions.Configuration;

namespace MiniWms.Infrastructure.Authentication;

public sealed class JwtSettings
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "MiniWms.Api";
    public string Audience { get; set; } = "MiniWms.Client";
    public string SigningKeyBase64 { get; set; } = string.Empty;

    public static JwtSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = new JwtSettings
        {
            Issuer = configuration["Jwt:Issuer"] ?? "MiniWms.Api",
            Audience = configuration["Jwt:Audience"] ?? "MiniWms.Client",
            SigningKeyBase64 = configuration["Jwt:SigningKeyBase64"] ?? string.Empty
        };
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience))
        {
            throw new InvalidOperationException("Jwt:Issuer and Jwt:Audience are required.");
        }

        if (GetSigningKeyBytes().Length < 32)
        {
            throw new InvalidOperationException("Jwt:SigningKeyBase64 must encode at least 32 cryptographically random bytes.");
        }
    }

    public byte[] GetSigningKeyBytes()
    {
        if (string.IsNullOrWhiteSpace(SigningKeyBase64))
        {
            throw new InvalidOperationException("Jwt:SigningKeyBase64 is required. Configure it in User Secrets or environment configuration.");
        }

        try
        {
            return Convert.FromBase64String(SigningKeyBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Jwt:SigningKeyBase64 must contain a Base64-encoded signing key.", exception);
        }
    }
}

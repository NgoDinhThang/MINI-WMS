namespace MiniWms.Application.Authentication;

public enum AuthErrorKind
{
    Validation,
    InvalidCredentials,
    InvalidSession,
    Forbidden,
    NotFound,
    Infrastructure
}

public sealed class AuthException : Exception
{
    public AuthException(AuthErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public AuthErrorKind Kind { get; }

    public IReadOnlyDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();
}

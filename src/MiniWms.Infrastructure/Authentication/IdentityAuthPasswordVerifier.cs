using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using MiniWms.Application.Authentication;
using MiniWms.Domain.Entities;

namespace MiniWms.Infrastructure.Authentication;

public sealed class IdentityAuthPasswordVerifier(IPasswordHasher<User> passwordHasher) : IAuthPasswordVerifier
{
    private static readonly User DummyUser = new();
    private static readonly Lazy<string> DummyHash = new(() =>
        new PasswordHasher<User>().HashPassword(DummyUser, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

    public bool Verify(User? user, string password)
    {
        var candidate = user ?? DummyUser;
        var storedHash = user?.PasswordHash ?? DummyHash.Value;
        try
        {
            var result = passwordHasher.VerifyHashedPassword(candidate, storedHash, password);
            return user is not null && result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

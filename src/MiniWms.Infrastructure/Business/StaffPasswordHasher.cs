using Microsoft.AspNetCore.Identity;
using MiniWms.Application.Business;
using MiniWms.Domain.Entities;

namespace MiniWms.Infrastructure.Business;

public sealed class StaffPasswordHasher(IPasswordHasher<User> hasher) : IStaffPasswordHasher
{
    public string Hash(User user, string password) => hasher.HashPassword(user, password);
}

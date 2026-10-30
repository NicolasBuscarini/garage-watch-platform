using GarageWatch.Application.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace GarageWatch.Infrastructure.Identity;

public enum RegistrationOutcome
{
    InvalidInput,
    Accepted
}

public sealed class IdentityRegistrationService(
    IIamUnitOfWork unitOfWork,
    IIamAuditEventRepository auditEvents,
    UserManager<ApplicationUser> users,
    IOptions<IdentityOptions> identityOptions,
    TimeProvider timeProvider)
{
    public async Task<RegistrationOutcome> RegisterAsync(string? username, string? password,
        string? confirmPassword, CancellationToken cancellationToken = default)
    {
        if (!IsValid(username, password, confirmPassword, identityOptions.Value))
            return RegistrationOutcome.InvalidInput;

        await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = username,
                IsActive = true,
                EmailConfirmed = false,
                LockoutEnabled = true
            };

            var created = await users.CreateAsync(user, password!);
            if (!created.Succeeded)
            {
                _ = users.PasswordHasher.HashPassword(new ApplicationUser { Id = user.Id }, password!);
                return;
            }

            var assigned = await users.AddToRoleAsync(user, "User");
            if (!assigned.Succeeded)
                throw new InvalidOperationException("Não foi possível atribuir o papel básico à nova conta.");

            auditEvents.Append(null, user.Id, "user.registered", null, timeProvider.GetUtcNow());
        }, cancellationToken);

        return RegistrationOutcome.Accepted;
    }

    private static bool IsValid(string? username, string? password, string? confirmPassword,
        IdentityOptions options)
    {
        if (string.IsNullOrEmpty(username) || username.Length is < 3 or > 64 ||
            string.IsNullOrEmpty(options.User.AllowedUserNameCharacters) ||
            username.Any(character => !options.User.AllowedUserNameCharacters.Contains(character)) ||
            string.IsNullOrEmpty(password) || password.Length < options.Password.RequiredLength ||
            password.Length > 256 || password.Distinct().Count() < options.Password.RequiredUniqueChars ||
            (options.Password.RequireDigit && !password.Any(char.IsDigit)) ||
            (options.Password.RequireLowercase && !password.Any(char.IsLower)) ||
            (options.Password.RequireUppercase && !password.Any(char.IsUpper)) ||
            (options.Password.RequireNonAlphanumeric && !password.Any(character => !char.IsLetterOrDigit(character))))
            return false;

        return string.Equals(password, confirmPassword, StringComparison.Ordinal);
    }
}

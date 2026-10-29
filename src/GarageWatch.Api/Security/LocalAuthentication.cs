using System.Security.Cryptography;
using System.Text;
using System.Security.Claims;

namespace GarageWatch.Api.Security;

public sealed record LocalAuthenticationOptions(string Username, byte[] PasswordHash, string Role)
{
    public static LocalAuthenticationOptions FromEnvironment(IConfiguration configuration)
    {
        var username = configuration["AUTH_USERNAME"];
        var password = configuration["AUTH_PASSWORD"];
        var role = configuration["AUTH_ROLE"] ?? "Viewer";
        if (string.IsNullOrWhiteSpace(username) || username.Length > 64 ||
            string.IsNullOrEmpty(password) || password == "replace-with-a-local-password-of-6-or-more-characters" ||
            password.Length < 6 || password.Length > 256 ||
            role is not ("Administrator" or "Viewer"))
            throw new InvalidOperationException("Autenticação local não configurada ou fora dos limites.");
        return new(username, SHA256.HashData(Encoding.UTF8.GetBytes(password)), role);
    }

    public bool Verify(string? username, string? password)
    {
        var suppliedUser = SHA256.HashData(Encoding.UTF8.GetBytes(username ?? ""));
        var expectedUser = SHA256.HashData(Encoding.UTF8.GetBytes(Username));
        var suppliedPassword = SHA256.HashData(Encoding.UTF8.GetBytes(password ?? ""));
        return CryptographicOperations.FixedTimeEquals(suppliedUser, expectedUser)
            & CryptographicOperations.FixedTimeEquals(suppliedPassword, PasswordHash);
    }
}

public sealed record LoginRequest(string Username, string Password);
public sealed record AuthenticatedUser(string Username, string Role);

public static class AuthenticationClaims
{
    public const string Name = ClaimTypes.Name;
    public const string Role = ClaimTypes.Role;
}

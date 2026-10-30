using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Identity;

public sealed record IdentityBootstrapOptions(string? Username, string? Password, string? Email)
{
    public const string DatabasePathKey = "IDENTITY_DB_PATH";
    public const string DefaultDatabasePath = "/data/identity/garagewatch.db";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(Password);

    public static IdentityBootstrapOptions FromConfiguration(IConfiguration configuration)
    {
        var username = configuration["AUTH_USERNAME"];
        var password = configuration["AUTH_PASSWORD"];
        var email = configuration["AUTH_EMAIL"];
        return new(username?.Trim(), password, string.IsNullOrWhiteSpace(email) ? null : email.Trim());
    }

    public void ValidateForBootstrap()
    {
        if (!IsConfigured || Username!.Length > 64 || Password!.Length < 6 || Password.Length > 256 ||
            Password.StartsWith("replace-with-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Configuração de bootstrap IAM inválida; use credencial exclusiva de administrador.");
    }
}

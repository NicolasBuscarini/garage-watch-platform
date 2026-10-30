namespace GarageWatch.Api.Security;

public sealed record LoginRequest(string Username, string Password);
public sealed record AuthenticatedUser(string Username, string Role, bool CanMonitor = false);
public sealed record RegisterRequest(string? Username, string? Password, string? ConfirmPassword);

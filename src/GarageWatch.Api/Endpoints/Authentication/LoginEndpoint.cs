using GarageWatch.Api.Endpoints;
using GarageWatch.Api.Security;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Authentication;

public sealed class LoginEndpoint() : BaseEndpoint("Authentication", "Login", "Create a local session",
    "Validates credentials and issues the same-origin HttpOnly session cookie. Invalid or inactive accounts receive the same 401 response.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/auth/login", async (LoginRequest request, UserManager<ApplicationUser> users,
            SignInManager<ApplicationUser> signInManager) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 64 ||
                string.IsNullOrEmpty(request.Password) || request.Password.Length > 256)
                return Results.Unauthorized();
            var account = await users.FindByNameAsync(request.Username);
            if (account is null || !account.IsActive)
            {
                _ = users.PasswordHasher.HashPassword(new ApplicationUser(), request.Password);
                return Results.Unauthorized();
            }
            var result = await signInManager.PasswordSignInAsync(request.Username, request.Password,
                isPersistent: false, lockoutOnFailure: true);
            return result.Succeeded ? Results.NoContent() : Results.Unauthorized();
        }).AllowAnonymous().RequireRateLimiting("login")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status429TooManyRequests);
}

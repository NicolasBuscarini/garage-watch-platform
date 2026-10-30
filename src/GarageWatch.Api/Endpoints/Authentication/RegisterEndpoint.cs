using GarageWatch.Api.Endpoints;
using GarageWatch.Api.Security;
using GarageWatch.Infrastructure.Identity;

namespace GarageWatch.Api.Endpoints.Authentication;

public sealed class RegisterEndpoint() : BaseEndpoint("Authentication", "Register", "Register a local User account",
    "Creates only a User with no camera grants. Registration is loopback/same-origin only and does not perform email verification or account recovery.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/auth/register", async (RegisterRequest request, HttpContext context,
            IdentityRegistrationService registration, CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var outcome = await registration.RegisterAsync(request.Username, request.Password,
                request.ConfirmPassword, cancellationToken);
            return outcome == RegistrationOutcome.InvalidInput
                ? Results.BadRequest(new ApiErrorResponse("invalid_registration"))
                : Results.Accepted();
        }).AllowAnonymous().RequireRateLimiting("registration")
            .Produces(StatusCodes.Status202Accepted).Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status429TooManyRequests);
}

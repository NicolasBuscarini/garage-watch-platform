using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class ListKnownPeopleEndpoint() : BaseEndpoint("Known people", "AdminListKnownPeople",
    "List consent-backed people", "Lists active minimal roster records; no face images or embeddings are stored.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/known-people", async (int? page, int? pageSize, KnownPersonService people,
            CancellationToken cancellationToken) =>
        {
            var currentPage = page ?? 1;
            var currentPageSize = pageSize ?? 25;
            try
            {
                var result = await people.ListAsync(currentPage, currentPageSize, cancellationToken);
                return Results.Ok(new KnownPersonPageResponse(result.Items.Select(item => new KnownPersonSummaryResponse(
                    item.PublicId, item.DisplayName, item.Purpose, item.AuthorizationRecordedAtUtc,
                    item.CreatedAtUtc, item.Version)).ToArray(), result.Page, result.PageSize, result.Total));
            }
            catch (ArgumentOutOfRangeException) { return Results.BadRequest(new ApiErrorResponse("invalid_pagination")); }
        }).RequireAuthorization("Administrator")
            .Produces<KnownPersonPageResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

public sealed class CreateKnownPersonEndpoint() : BaseEndpoint("Known people", "AdminCreateKnownPerson",
    "Register an authorized person", "Records the administrator's authorization attestation and purpose; this does not enroll biometric samples.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/admin/known-people", async (CreateKnownPersonRequest request, HttpContext context,
            ClaimsPrincipal principal, UserManager<ApplicationUser> users, KnownPersonService people,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            try
            {
                var created = await people.CreateAsync(actor.Id, request.DisplayName, request.Purpose,
                    request.AuthorizationConfirmed, cancellationToken);
                return Results.Created($"/api/v1/admin/known-people/{created.PublicId}", created);
            }
            catch (ArgumentException) { return Results.BadRequest(new ApiErrorResponse("invalid_known_person")); }
        }).RequireAuthorization("Administrator")
            .Produces<KnownPersonResult>(StatusCodes.Status201Created)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

public sealed class RemoveKnownPersonEndpoint() : BaseEndpoint("Known people", "AdminRemoveKnownPerson",
    "Remove a registered person", "Removes identifying roster fields and retains only a tombstone and minimal audit reference.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/api/v1/admin/known-people/{personId}", async (string personId, HttpContext context,
            ClaimsPrincipal principal, UserManager<ApplicationUser> users, KnownPersonService people,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            try
            {
                await people.RemoveAsync(actor.Id, personId, cancellationToken);
                return Results.NoContent();
            }
            catch (KeyNotFoundException) { return Results.NotFound(new ApiErrorResponse("known_person_not_found")); }
        }).RequireAuthorization("Administrator")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

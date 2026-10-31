using GarageWatch.Api.Contracts;
using GarageWatch.Api.Endpoints;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class ReplaceUserAccessProfilesEndpoint() : BaseEndpoint("User administration", "AdminReplaceUserAccessProfiles",
    "Assign access profiles to a user", "Replaces profile assignments and their explicit camera/context scopes atomically. Permissions are not editable on a user; Administrator is never assignable.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPut("/api/v1/admin/users/{userId}/access-rules", async (string userId,
            SetUserAccessProfilesRequest request, HttpContext context, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, AccessProfileService profiles, IamDbContext database,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            if (request.Assignments is null) return Results.BadRequest(new ApiErrorResponse("invalid_assignments"));
            try
            {
                if (!PublicIdFormat.IsValid(userId, PublicIdPrefixes.UserPrefix))
                    return Results.NotFound(new ApiErrorResponse("user_not_found"));
                var targetId = await database.Users.Where(item => item.PublicId == userId)
                    .Select(item => (Guid?)item.Id).SingleOrDefaultAsync(cancellationToken);
                if (targetId is null) return Results.NotFound(new ApiErrorResponse("user_not_found"));
                var versionIds = request.Assignments.Select(item => item.ProfileVersionPublicId).ToArray();
                if (versionIds.Any(value => !PublicIdFormat.IsValid(value, PublicIdPrefixes.AccessProfileVersionPrefix)))
                    return Results.BadRequest(new ApiErrorResponse("invalid_assignments"));
                var versions = await database.AccessProfileVersions.AsNoTracking()
                    .Where(item => versionIds.Contains(item.PublicId!))
                    .ToDictionaryAsync(item => item.PublicId!, item => item.Id, cancellationToken);
                if (versions.Count != versionIds.Distinct(StringComparer.Ordinal).Count())
                    return Results.BadRequest(new ApiErrorResponse("invalid_assignments"));
                var scopes = request.Assignments.SelectMany(item => item.Scopes).ToArray();
                if (scopes.Any(scope => !PublicIdFormat.IsValid(scope.ContextId, PublicIdPrefixes.ContextPrefix)
                    || !PublicIdFormat.IsValid(scope.CameraId, PublicIdPrefixes.CameraPrefix)))
                    return Results.BadRequest(new ApiErrorResponse("invalid_assignments"));
                var cameras = await database.CameraCatalog.AsNoTracking()
                    .Where(camera => scopes.Select(scope => scope.CameraId).Contains(camera.PublicId!))
                    .Select(camera => new
                    {
                        camera.PublicId,
                        camera.CameraId,
                        camera.ContextId,
                        ContextPublicId = camera.Context.PublicId
                    })
                    .ToArrayAsync(cancellationToken);
                var scopesByPublicIds = cameras.ToDictionary(camera => (camera.ContextPublicId!, camera.PublicId!),
                    camera => new GarageWatch.Application.Authorization.PermissionScope(camera.ContextId, camera.CameraId));
                var internalAssignments = request.Assignments.Select(item => new ProfileAssignmentRequest(
                    versions[item.ProfileVersionPublicId], item.Scopes.Select(scope =>
                        scopesByPublicIds.TryGetValue((scope.ContextId, scope.CameraId), out var mapped)
                            ? mapped : throw new ArgumentException("Escopo inválido.")).ToArray())).ToArray();
                await profiles.ReplaceAssignmentsAsync(actor.Id, targetId.Value, internalAssignments, cancellationToken);
                return Results.NoContent();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                              KeyNotFoundException or UnauthorizedAccessException)
            {
                return EndpointSecurity.AdminIdentityError(exception);
            }
        }).RequireAuthorization("Administrator")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

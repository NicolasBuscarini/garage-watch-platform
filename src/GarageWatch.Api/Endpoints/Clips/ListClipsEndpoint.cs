using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Clips;

public sealed class ListClipsEndpoint() : BaseEndpoint("Clips", "ListClips", "List authorized clips",
    "Returns a bounded chronological page of clips filtered by camera, UTC interval and media state.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/clips", async (string? cameraId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc,
            string? state, int? page, int? pageSize, ClaimsPrincipal principal, UserManager<ApplicationUser> users,
            ClipLibraryService library, CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            var admin = await users.IsInRoleAsync(user, "Administrator");
            try
            {
                var result = await library.ListAsync(user.Id, admin, cameraId, fromUtc, toUtc, state,
                    page ?? 1, pageSize ?? 25, cancellationToken);
                var items = result.Items.Select(item => new ClipListItemResponse(item.PublicId, item.CameraId,
                    item.StartedAtUtc, item.EndedAtUtc, item.State, item.ArchiveStatus, item.AnalysisStatus,
                    item.ThumbnailUrl, item.SizeBytes, item.TriggerReason, item.TriggerConfigVersion)).ToArray();
                return Results.Ok(new ClipPageResponse(items, result.Page, result.PageSize, result.Total));
            }
            catch (ArgumentException) { return Results.BadRequest(new ApiErrorResponse("invalid_clip_query")); }
            catch (InvalidOperationException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        }).RequireAuthorization()
            .Produces<ClipPageResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

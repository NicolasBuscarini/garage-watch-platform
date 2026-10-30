using GarageWatch.Api.Contracts;
using GarageWatch.Api.Endpoints;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class ListUsersEndpoint() : BaseEndpoint("User administration", "AdminListUsers",
    "List user accounts", "Returns a stable page of minimized account data; email, password hashes, and tokens are not exposed.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/users", async (int? page, int? pageSize, UserManager<ApplicationUser> users,
            CancellationToken cancellationToken) =>
        {
            var currentPage = page ?? 1;
            var currentPageSize = pageSize ?? 25;
            if (currentPage < 1 || currentPageSize is < 1 or > 100)
                return Results.BadRequest(new ApiErrorResponse("invalid_pagination"));
            var query = users.Users.AsNoTracking().OrderBy(user => user.NormalizedUserName).ThenBy(user => user.Id);
            var totalCount = await query.CountAsync(cancellationToken);
            var offset = ((long)currentPage - 1) * currentPageSize;
            var items = offset >= totalCount
                ? []
                : await query.Skip((int)offset).Take(currentPageSize)
                    .Select(user => new AdminUserSummaryResponse(user.Id, user.UserName ?? string.Empty, user.IsActive))
                    .ToArrayAsync(cancellationToken);
            return Results.Ok(new AdminUserPageResponse(items, currentPage, currentPageSize, totalCount));
        }).RequireAuthorization("Administrator")
            .Produces<AdminUserPageResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

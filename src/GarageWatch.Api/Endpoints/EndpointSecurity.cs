using GarageWatch.Application.Cameras;

namespace GarageWatch.Api.Endpoints;

internal static class EndpointSecurity
{
    public static bool IsSameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers["Origin"].ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var suppliedOrigin) ||
            !string.IsNullOrEmpty(suppliedOrigin.UserInfo) || suppliedOrigin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(suppliedOrigin.Query) || !string.IsNullOrEmpty(suppliedOrigin.Fragment))
            return false;

        var requestOriginText = $"{context.Request.Scheme}://{context.Request.Host}";
        return Uri.TryCreate(requestOriginText, UriKind.Absolute, out var requestOrigin) &&
            suppliedOrigin.IsLoopback && requestOrigin.IsLoopback &&
            string.Equals(suppliedOrigin.GetLeftPart(UriPartial.Authority),
                requestOrigin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }

    public static IResult CatalogError(Exception exception) => exception switch
    {
        CatalogValidationException => Results.BadRequest(new ApiErrorResponse("invalid_catalog_request")),
        CatalogNotFoundException => Results.NotFound(new ApiErrorResponse("catalog_item_not_found")),
        CatalogConflictException => Results.Conflict(new ApiErrorResponse("catalog_conflict")),
        UnauthorizedAccessException => Results.Forbid(),
        _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
    };

    public static IResult AdminIdentityError(Exception exception) => exception switch
    {
        ArgumentException => Results.BadRequest(new ApiErrorResponse("invalid_admin_request")),
        KeyNotFoundException => Results.NotFound(new ApiErrorResponse("admin_resource_not_found")),
        InvalidOperationException => Results.Conflict(new ApiErrorResponse("admin_operation_conflict")),
        UnauthorizedAccessException => Results.Forbid(),
        _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
    };
}

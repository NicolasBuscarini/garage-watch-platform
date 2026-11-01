using GarageWatch.Application.Cameras;

namespace GarageWatch.Api.Endpoints;

internal static class EndpointSecurity
{
    public static bool IsSameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers["Origin"].ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var suppliedOrigin) ||
            (suppliedOrigin.Scheme != Uri.UriSchemeHttp && suppliedOrigin.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(suppliedOrigin.UserInfo) || suppliedOrigin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(suppliedOrigin.Query) || !string.IsNullOrEmpty(suppliedOrigin.Fragment))
            return false;

        var requestHost = context.Request.Host.Value;
        if (string.IsNullOrWhiteSpace(requestHost) ||
            !string.Equals(suppliedOrigin.Authority, requestHost, StringComparison.OrdinalIgnoreCase))
            return false;

        // Local direct access must match scheme as well. For a TLS-terminating tunnel, the
        // upstream request can be HTTP while the browser Origin is HTTPS; exact authority
        // matching still prevents cross-origin browser writes without trusting forwarded headers.
        if (suppliedOrigin.IsLoopback)
            return string.Equals(suppliedOrigin.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase);

        return suppliedOrigin.Scheme == Uri.UriSchemeHttps;
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

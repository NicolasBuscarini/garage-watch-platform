namespace GarageWatch.Api.Contracts;

public sealed record AdminUserSummaryResponse(Guid Id, string UserName, bool IsActive);

public sealed record AdminUserPageResponse(IReadOnlyList<AdminUserSummaryResponse> Items, int Page, int PageSize,
    int TotalCount);

public sealed record AdminUserCameraGrantResponse(string ContextId, string CameraId, string? CameraName,
    DateTimeOffset GrantedAtUtc, DateTimeOffset? RevokedAtUtc);

public sealed record AdminUserDetailsResponse(Guid Id, string UserName, bool IsActive, IReadOnlyList<string> Roles,
    IReadOnlyList<AdminUserCameraGrantResponse> CameraGrants);

public sealed record SetUserRolesRequest(IReadOnlyList<string>? Roles);

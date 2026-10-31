using GarageWatch.Infrastructure.Identity;

namespace GarageWatch.Api.Contracts;

public sealed record AdminUserSummaryResponse(string PublicId, string UserName, bool IsActive);

public sealed record AdminUserPageResponse(IReadOnlyList<AdminUserSummaryResponse> Items, int Page, int PageSize,
    int TotalCount);

public sealed record AdminUserDetailsResponse(string PublicId, string UserName, bool IsActive, bool IsAdministrator,
    IReadOnlyList<AccessProfileAssignmentSummary> AccessProfiles);

public sealed record PublicScopeRequest(string ContextId, string CameraId);
public sealed record PublicProfileAssignmentRequest(string ProfileVersionPublicId, IReadOnlyList<PublicScopeRequest> Scopes);
public sealed record SetUserAccessProfilesRequest(IReadOnlyList<PublicProfileAssignmentRequest>? Assignments);

public sealed record CreateAccessProfileRequest(string Name, IReadOnlyList<string>? PermissionKeys);

public sealed record PublishAccessProfileVersionRequest(long ExpectedVersion, IReadOnlyList<string>? PermissionKeys);

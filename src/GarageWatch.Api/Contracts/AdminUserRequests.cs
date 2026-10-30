using GarageWatch.Infrastructure.Identity;

namespace GarageWatch.Api.Contracts;

public sealed record AdminUserSummaryResponse(Guid Id, string UserName, bool IsActive);

public sealed record AdminUserPageResponse(IReadOnlyList<AdminUserSummaryResponse> Items, int Page, int PageSize,
    int TotalCount);

public sealed record AdminUserDetailsResponse(Guid Id, string UserName, bool IsActive, bool IsAdministrator,
    IReadOnlyList<AccessProfileAssignmentSummary> AccessProfiles);

public sealed record SetUserAccessProfilesRequest(IReadOnlyList<ProfileAssignmentRequest>? Assignments);

public sealed record CreateAccessProfileRequest(string Name, IReadOnlyList<string>? PermissionKeys);

public sealed record PublishAccessProfileVersionRequest(long ExpectedVersion, IReadOnlyList<string>? PermissionKeys);

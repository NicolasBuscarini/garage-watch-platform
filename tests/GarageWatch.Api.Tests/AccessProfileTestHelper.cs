using GarageWatch.Application.Authorization;
using GarageWatch.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GarageWatch.Api.Tests;

internal static class AccessProfileTestHelper
{
    public static async Task AssignVideoAsync(IServiceProvider services, Guid administratorId, Guid userId,
        string contextId, string cameraId)
    {
        using var scope = services.CreateScope();
        var profiles = scope.ServiceProvider.GetRequiredService<AccessProfileService>();
        var video = Assert.Single(await profiles.ListAsync(CancellationToken.None), item => item.Name == "Live somente vídeo");
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var versionId = await database.AccessProfileVersions.AsNoTracking()
            .Where(item => item.PublicId == video.CurrentVersionPublicId).Select(item => item.Id)
            .SingleAsync();
        await profiles.ReplaceAssignmentsAsync(administratorId, userId,
            [new ProfileAssignmentRequest(versionId, [new PermissionScope(contextId, cameraId)])],
            CancellationToken.None);
    }
}

using GarageWatch.Infrastructure.Identity;

namespace GarageWatch.Infrastructure.Capture;

public static class ClipPublicIdAllocator
{
    public static string GenerateUnique(IPublicIdGenerator generator, ISet<string> reservedIds)
    {
        return PublicIdAllocator.GenerateUnique(generator, PublicIdPrefixes.ClipPrefix, reservedIds);
    }
}

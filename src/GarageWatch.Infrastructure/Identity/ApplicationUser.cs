using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public bool IsActive { get; set; } = true;
}

using GarageWatch.Domain;

namespace GarageWatch.Application.Profiles;

public interface ISystemProfileSource
{
    SystemProfile Get();
}

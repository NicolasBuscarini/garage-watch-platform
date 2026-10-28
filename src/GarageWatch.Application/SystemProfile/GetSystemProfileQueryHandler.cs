using GarageWatch.Domain;
using MediatR;

namespace GarageWatch.Application.Profiles;

public sealed class GetSystemProfileQueryHandler(ISystemProfileSource source)
    : IRequestHandler<GetSystemProfileQuery, SystemProfile>
{
    public Task<SystemProfile> Handle(GetSystemProfileQuery request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(source.Get());
    }
}

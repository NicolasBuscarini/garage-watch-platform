using GarageWatch.Domain;
using MediatR;

namespace GarageWatch.Application.Profiles;

public sealed record GetSystemProfileQuery : IRequest<SystemProfile>;

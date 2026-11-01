namespace GarageWatch.Application.Cameras;

public sealed record ActiveCameraAddress(string Address);

public interface ICameraConnectionManager
{
    Task<ActiveCameraAddress> GetAddressAsync(CancellationToken cancellationToken = default);
    Task<ActiveCameraAddress> UpdateAddressAsync(string expectedAddress, string address,
        CancellationToken cancellationToken = default);
}

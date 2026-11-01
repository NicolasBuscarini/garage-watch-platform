namespace GarageWatch.Api.Contracts;

public sealed record UpdateCameraAddressRequest(string ExpectedAddress, string Address);
public sealed record CameraConnectionResponse(string CameraPublicId, string Address, bool IsCaptureCamera);

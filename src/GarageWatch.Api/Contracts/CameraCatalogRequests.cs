namespace GarageWatch.Api.Contracts;

public sealed record CreateCameraContextRequest(string Name);

public sealed record UpdateCameraContextRequest(long Version, string Name, bool IsEnabled);

public sealed record CreateCameraCatalogRequest(string ContextId, string Name);

public sealed record UpdateCameraCatalogRequest(long Version, string ContextId, string Name, bool IsEnabled);

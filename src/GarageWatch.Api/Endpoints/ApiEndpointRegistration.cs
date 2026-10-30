namespace GarageWatch.Api.Endpoints;

public static class ApiEndpointRegistration
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder routes)
    {
        BaseEndpoint[] endpoints =
        [
            new Health.LiveEndpoint(), new Health.ReadyEndpoint(),
            new Authentication.LoginEndpoint(), new Authentication.RegisterEndpoint(),
            new Authentication.LogoutEndpoint(), new Authentication.SessionEndpoint(),
            new Authentication.MeEndpoint(), new Clients.HeartbeatEndpoint(),
            new Cameras.ListCamerasEndpoint(),
            new Admin.ListContextsEndpoint(), new Admin.CreateContextEndpoint(),
            new Admin.UpdateContextEndpoint(), new Admin.DeactivateContextEndpoint(),
            new Admin.ListCamerasEndpoint(), new Admin.CreateCameraEndpoint(),
            new Admin.UpdateCameraEndpoint(), new Admin.DeactivateCameraEndpoint(),
            new Admin.ListUsersEndpoint(), new Admin.GetUserEndpoint(), new Admin.DeactivateUserEndpoint(),
            new Admin.ListPermissionCatalogEndpoint(), new Admin.ListAccessProfilesEndpoint(),
            new Admin.CreateAccessProfileEndpoint(), new Admin.PublishAccessProfileVersionEndpoint(),
            new Admin.ReplaceUserAccessProfilesEndpoint(),
            new Live.GetPlaylistEndpoint(), new Live.GetSegmentEndpoint(),
            new System.GetSystemProfileEndpoint()
        ];

        foreach (var endpoint in endpoints)
            endpoint.Map(routes);

        return routes;
    }
}

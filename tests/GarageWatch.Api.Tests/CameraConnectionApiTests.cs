using System.Net;
using System.Net.Http.Json;
using GarageWatch.Api.Contracts;
using GarageWatch.Application.Cameras;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class CameraConnectionApiTests
{
    [Fact]
    public async Task Administrator_can_read_and_update_only_active_camera_address_with_origin_and_audit()
    {
        var bridge = new TestCameraConnectionManager("192.168.0.120");
        await using var factory = new ApiFactory(useCameraCapture: true, cameraConnectionManager: bridge);
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "Contract-Password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");

        string cameraPublicId;
        using (var scope = factory.Services.CreateScope())
            cameraPublicId = (await scope.ServiceProvider.GetRequiredService<IamDbContext>().CameraCatalog
                .AsNoTracking().Select(item => item.PublicId).SingleAsync())!;

        var path = $"/api/v1/admin/cameras/{cameraPublicId}/connection";
        var current = await client.GetFromJsonAsync<CameraConnectionResponse>(path);
        Assert.Equal("192.168.0.120", current!.Address);
        Assert.True(current.IsCaptureCamera);

        client.DefaultRequestHeaders.Remove("Origin");
        var rejectedOrigin = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(new UpdateCameraAddressRequest("192.168.0.120", "192.168.0.121"))
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(rejectedOrigin)).StatusCode);
        Assert.Equal("192.168.0.120", bridge.Address);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var invalid = await client.PutAsJsonAsync(path,
            new UpdateCameraAddressRequest("192.168.0.120", "rtsp://192.168.0.121"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var publicAddress = await client.PutAsJsonAsync(path,
            new UpdateCameraAddressRequest("192.168.0.120", "8.8.8.8"));
        Assert.Equal(HttpStatusCode.BadRequest, publicAddress.StatusCode);
        using var stale = await client.PutAsJsonAsync(path,
            new UpdateCameraAddressRequest("192.168.0.119", "192.168.0.121"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var update = await client.PutAsJsonAsync(path,
            new UpdateCameraAddressRequest("192.168.0.120", "camera-garage.lan"));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("camera-garage.lan", bridge.Address);
        using var auditScope = factory.Services.CreateScope();
        Assert.Equal(1, await auditScope.ServiceProvider.GetRequiredService<IamDbContext>().IamAuditEvents
            .CountAsync(entry => entry.Action == "camera.connection.address.updated"));
    }

    private sealed class TestCameraConnectionManager(string address) : ICameraConnectionManager
    {
        public string Address { get; private set; } = address;

        public Task<ActiveCameraAddress> GetAddressAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ActiveCameraAddress(Address));

        public Task<ActiveCameraAddress> UpdateAddressAsync(string expectedAddress, string next,
            CancellationToken cancellationToken = default)
        {
            if (expectedAddress != Address) throw new CameraAddressConflictException();
            Address = next;
            return Task.FromResult(new ActiveCameraAddress(Address));
        }
    }
}

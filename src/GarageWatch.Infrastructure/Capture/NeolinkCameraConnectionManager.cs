using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GarageWatch.Application.Cameras;
using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Capture;

public sealed class NeolinkCameraConnectionManager(
    IHttpClientFactory clients,
    IConfiguration configuration) : ICameraConnectionManager
{
    private const string ClientName = "neolink-control";
    private string? Token => configuration["NEOLINK_CONTROL_TOKEN"];

    public async Task<ActiveCameraAddress> GetAddressAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "/v1/camera-address");
        using var response = await clients.CreateClient(ClientName).SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw Translate(response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<AddressResponse>(cancellationToken: cancellationToken);
        return new ActiveCameraAddress(result?.Address ?? throw new HttpRequestException("Neolink returned an invalid response."));
    }

    public async Task<ActiveCameraAddress> UpdateAddressAsync(string expectedAddress, string address,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Put, "/v1/camera-address");
        request.Content = JsonContent.Create(new UpdateAddressRequest(expectedAddress, address));
        using var response = await clients.CreateClient(ClientName).SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw Translate(response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<AddressResponse>(cancellationToken: cancellationToken);
        return new ActiveCameraAddress(result?.Address ?? throw new HttpRequestException("Neolink returned an invalid response."));
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var token = Token;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Neolink camera control is not configured.");
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Garage-Control-Token", token);
        return request;
    }

    private static Exception Translate(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Conflict => new CameraAddressConflictException(),
        HttpStatusCode.BadRequest => new ArgumentException("Invalid camera address."),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new InvalidOperationException("Neolink rejected the control request."),
        _ => new HttpRequestException("Neolink camera control is unavailable.", null, status)
    };

    private sealed record AddressResponse([property: JsonPropertyName("address")] string Address);
    private sealed record UpdateAddressRequest(
        [property: JsonPropertyName("expectedAddress")] string ExpectedAddress,
        [property: JsonPropertyName("address")] string Address);
}

public sealed class CameraAddressConflictException : Exception { }

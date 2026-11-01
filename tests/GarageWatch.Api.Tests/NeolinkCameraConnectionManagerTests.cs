using System.Net;
using System.Text;
using GarageWatch.Application.Cameras;
using GarageWatch.Infrastructure.Capture;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class NeolinkCameraConnectionManagerTests
{
    [Fact]
    public async Task Bridge_client_uses_internal_token_and_camel_case_expected_address_contract()
    {
        var handler = new RecordingHandler();
        var manager = new NeolinkCameraConnectionManager(
            new SingleClientFactory(new HttpClient(handler)),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NEOLINK_CONTROL_TOKEN"] = "contract-token",
            }).Build());

        Assert.Equal("192.168.0.120", (await manager.GetAddressAsync()).Address);
        Assert.Equal("camera.local", (await manager.UpdateAddressAsync("192.168.0.120", "camera.local")).Address);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("contract-token", request.Token));
        Assert.Equal("GET /v1/camera-address", handler.Requests[0].Target);
        Assert.Equal("PUT /v1/camera-address", handler.Requests[1].Target);
        Assert.Contains("\"expectedAddress\":\"192.168.0.120\"", handler.Requests[1].Body);
        Assert.Contains("\"address\":\"camera.local\"", handler.Requests[1].Body);
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            client.BaseAddress ??= new Uri("http://neolink:8555");
            return client;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest($"{request.Method} {request.RequestUri!.PathAndQuery}",
                request.Headers.GetValues("X-Garage-Control-Token").Single(), body));
            var payload = request.Method == HttpMethod.Get
                ? "{\"address\":\"192.168.0.120\"}"
                : "{\"address\":\"camera.local\"}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record CapturedRequest(string Target, string Token, string Body);
}

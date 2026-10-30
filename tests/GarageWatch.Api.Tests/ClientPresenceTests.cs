using System.Net;
using System.Net.Http.Json;
using GarageWatch.Api.Presence;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class ClientPresenceTests
{
    [Fact]
    public async Task Distinct_tabs_using_the_same_account_are_counted_once_each()
    {
        await using var factory = new ApiFactory();
        using var anonymous = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var denied = await anonymous.PutAsJsonAsync("/api/v1/clients/presence", new { clientId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var login = await anonymous.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "Contract-Password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];
        using var sameAccount = factory.CreateClient(new() { AllowAutoRedirect = false });
        sameAccount.DefaultRequestHeaders.Add("Cookie", cookie);

        var firstTab = Guid.NewGuid();
        var secondTab = Guid.NewGuid();
        using var first = await sameAccount.PutAsJsonAsync("/api/v1/clients/presence", new { clientId = firstTab });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(1, (await first.Content.ReadFromJsonAsync<PresenceResponse>())!.ActiveClients);

        using var second = await sameAccount.PutAsJsonAsync("/api/v1/clients/presence", new { clientId = secondTab });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, (await second.Content.ReadFromJsonAsync<PresenceResponse>())!.ActiveClients);

        using var duplicateHeartbeat = await sameAccount.PutAsJsonAsync("/api/v1/clients/presence", new { clientId = firstTab });
        Assert.Equal(HttpStatusCode.OK, duplicateHeartbeat.StatusCode);
        var current = await duplicateHeartbeat.Content.ReadFromJsonAsync<PresenceResponse>();
        Assert.NotNull(current);
        Assert.Equal(2, current.ActiveClients);
        Assert.Equal(30, current.LeaseSeconds);

        for (var index = 2; index < ClientPresenceRegistry.MaximumClients; index++)
        {
            using var additional = await sameAccount.PutAsJsonAsync("/api/v1/clients/presence",
                new { clientId = Guid.NewGuid() });
            Assert.Equal(HttpStatusCode.OK, additional.StatusCode);
        }
        using var atCapacity = await sameAccount.PutAsJsonAsync("/api/v1/clients/presence",
            new { clientId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.TooManyRequests, atCapacity.StatusCode);
        var full = await atCapacity.Content.ReadFromJsonAsync<CapacityResponse>();
        Assert.NotNull(full);
        Assert.Equal(ClientPresenceRegistry.MaximumClients, full.ActiveClients);

        using var invalid = await sameAccount.PutAsJsonAsync("/api/v1/clients/presence", new { clientId = Guid.Empty });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public void Heartbeats_renew_leases_expire_old_tabs_and_enforce_capacity()
    {
        var clock = new ControlledTimeProvider(DateTimeOffset.Parse("2026-10-29T12:00:00Z"));
        var registry = new ClientPresenceRegistry(clock);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.True(registry.Heartbeat(first, out var count));
        Assert.Equal(1, count);
        Assert.True(registry.Heartbeat(first, out count));
        Assert.Equal(1, count);
        Assert.True(registry.Heartbeat(second, out count));
        Assert.Equal(2, count);

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(registry.Heartbeat(first, out count));
        Assert.Equal(1, count);

        for (var index = 0; index < ClientPresenceRegistry.MaximumClients - 1; index++)
            Assert.True(registry.Heartbeat(Guid.NewGuid(), out count));
        Assert.Equal(ClientPresenceRegistry.MaximumClients, count);
        Assert.False(registry.Heartbeat(Guid.NewGuid(), out count));
        Assert.Equal(ClientPresenceRegistry.MaximumClients, count);
    }

    private sealed record PresenceResponse(int ActiveClients, double LeaseSeconds);
    private sealed record CapacityResponse(string Code, int ActiveClients);

    private sealed class ControlledTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}

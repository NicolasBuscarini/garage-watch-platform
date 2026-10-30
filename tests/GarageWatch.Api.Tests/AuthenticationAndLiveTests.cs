using System.Net;
using System.Net.Http.Json;
using GarageWatch.Api.Live;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class AuthenticationAndLiveTests
{
    [Fact]
    public void SlidingPlaylistAssignsIncreasingMediaSequenceNumbers()
    {
        var sequence = new LivePlaylistSequence();
        Assert.Equal(0, sequence.GetStartSequence(["segment-a", "segment-b", "segment-c"], 8));
        Assert.Equal(1, sequence.GetStartSequence(["segment-b", "segment-c", "segment-d"], 8));
        Assert.Equal(2, sequence.GetStartSequence(["segment-c", "segment-d", "segment-e"], 8));
        Assert.False(LivePlaylistSequence.StartsDiscontinuity(["epoch-a", "epoch-a"], 0));
        Assert.False(LivePlaylistSequence.StartsDiscontinuity(["epoch-a", "epoch-a"], 1));
        Assert.True(LivePlaylistSequence.StartsDiscontinuity(["epoch-a", "epoch-b"], 1));
    }

    [Theory]
    [InlineData("Valid-Password-123!", true)]
    [InlineData("123456", true)]
    [InlineData("12345", false)]
    [InlineData("short", false)]
    [InlineData("replace-with-a-local-password-of-6-or-more-characters", false)]
    public void BootstrapPasswordMustMeetMinimumAndRejectExample(string password, bool accepted)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AUTH_USERNAME"] = "contract-user",
            ["AUTH_PASSWORD"] = password,
        }).Build();

        if (accepted)
        {
            var options = IdentityBootstrapOptions.FromConfiguration(configuration);
            Assert.True(options.IsConfigured);
            options.ValidateForBootstrap();
        }
        else
            Assert.Throws<InvalidOperationException>(() => IdentityBootstrapOptions.FromConfiguration(configuration).ValidateForBootstrap());
    }

    [Fact]
    public async Task CameraAndLiveEndpointsRequireSessionAndLoginIssuesHttpOnlyCookie()
    {
        await using var factory = new ApiFactory();
        using var anonymous = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var panel = await anonymous.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, panel.StatusCode);
        var html = await panel.Content.ReadAsStringAsync();
        Assert.Contains("Garagem IA · Painel local", html, StringComparison.Ordinal);
        Assert.Contains("<div id=\"root\"></div>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn.", html, StringComparison.OrdinalIgnoreCase);
        var scriptPath = System.Text.RegularExpressions.Regex.Match(html,
            "src=\"(?<path>/assets/[^\"]+\\.js)\"").Groups["path"].Value;
        var stylePath = System.Text.RegularExpressions.Regex.Match(html,
            "href=\"(?<path>/assets/[^\"]+\\.css)\"").Groups["path"].Value;
        Assert.NotEmpty(scriptPath);
        Assert.NotEmpty(stylePath);
        using var staticScript = await anonymous.GetAsync(scriptPath);
        Assert.Equal(HttpStatusCode.OK, staticScript.StatusCode);
        Assert.Equal("no-store", staticScript.Headers.CacheControl?.ToString());
        var appScript = await staticScript.Content.ReadAsStringAsync();
        Assert.Contains("/api/v1/auth/login", appScript, StringComparison.Ordinal);
        Assert.Contains("/api/v1/auth/register", appScript, StringComparison.Ordinal);
        Assert.Contains("garage-watch.theme.v1", appScript, StringComparison.Ordinal);
        Assert.Contains("withCredentials", appScript, StringComparison.Ordinal);
        Assert.Contains("Telas do painel", appScript, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceMappingURL", appScript, StringComparison.Ordinal);
        var hlsChunk = System.Text.RegularExpressions.Regex.Match(appScript,
            "import\\(`\\./(?<file>hls-[^`]+\\.js)`\\)").Groups["file"].Value;
        Assert.NotEmpty(hlsChunk);
        using var hlsBundle = await anonymous.GetAsync($"/assets/{hlsChunk}");
        Assert.Equal(HttpStatusCode.OK, hlsBundle.StatusCode);
        var css = await anonymous.GetStringAsync(stylePath);
        Assert.Contains(".container", css, StringComparison.Ordinal);
        Assert.Contains("data-theme", css, StringComparison.Ordinal);
        Assert.Contains(":focus-visible", css, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion", css, StringComparison.Ordinal);
        using var favicon = await anonymous.GetAsync("/favicon.svg");
        Assert.Equal(HttpStatusCode.OK, favicon.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/app.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/app.css")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/js/player.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/vendor/hls.min.js")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/cameras")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/live/garage-01/index.m3u8")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/health/ready")).StatusCode);
        var anonymousSession = await anonymous.GetFromJsonAsync<Dictionary<string, bool>>("/api/v1/auth/session");
        Assert.NotNull(anonymousSession);
        Assert.False(anonymousSession["authenticated"]);

        using var login = await anonymous.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "Contract-Password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("garagewatch.session=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        var session = factory.CreateClient(new() { AllowAutoRedirect = false });
        session.DefaultRequestHeaders.Add("Cookie", cookie.Split(';', 2)[0]);
        var authenticatedSession = await session.GetFromJsonAsync<Dictionary<string, bool>>("/api/v1/auth/session");
        Assert.NotNull(authenticatedSession);
        Assert.True(authenticatedSession["authenticated"]);
        using var cameras = await session.GetAsync("/api/v1/cameras");
        Assert.Equal(HttpStatusCode.OK, cameras.StatusCode);
        var body = await cameras.Content.ReadAsStringAsync();
        Assert.Contains("capture-test", body, StringComparison.Ordinal);
        Assert.DoesNotContain("rtsp", body, StringComparison.OrdinalIgnoreCase);
        using var cameraJson = System.Text.Json.JsonDocument.Parse(body);
        Assert.False(cameraJson.RootElement[0].TryGetProperty("segments", out _));
        Assert.False(cameraJson.RootElement[0].TryGetProperty("gaps", out _));
        Assert.False(cameraJson.RootElement[0].TryGetProperty("adapter", out _));
        Assert.True(cameraJson.RootElement[0].TryGetProperty("streamEpoch", out _));
        long initialSegmentCount = 0;

        using var liveness = await session.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        HttpResponseMessage? readiness = null;
        var readinessDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < readinessDeadline)
        {
            readiness?.Dispose();
            readiness = await session.GetAsync("/health/ready");
            if (readiness.StatusCode == HttpStatusCode.OK) break;
            await Task.Delay(50);
        }
        Assert.NotNull(readiness);
        using (readiness)
        {
            Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
            using var readinessJson = System.Text.Json.JsonDocument.Parse(await readiness.Content.ReadAsStringAsync());
            Assert.Equal("ready", readinessJson.RootElement.GetProperty("status").GetString());
            Assert.Equal("capturing", readinessJson.RootElement.GetProperty("capture").GetProperty("state").GetString());
            var capture = readinessJson.RootElement.GetProperty("capture");
            Assert.True(capture.GetProperty("segments").GetInt64() >= 1);
            Assert.True(capture.GetProperty("gaps").GetInt64() >= 0);
            var spool = readinessJson.RootElement.GetProperty("capture").GetProperty("spool");
            Assert.Equal("available", spool.GetProperty("status").GetString());
            Assert.Equal(4, spool.GetProperty("segmentLimit").GetInt32());
            Assert.InRange(spool.GetProperty("segments").GetInt32(), 1, 4);
            Assert.Equal(capture.GetProperty("segments").GetInt64(), spool.GetProperty("segments").GetInt64());
            initialSegmentCount = spool.GetProperty("segments").GetInt64();
        }
        using var profileResponse = await session.GetAsync("/api/v1/system-profile");
        Assert.Equal(HttpStatusCode.OK, profileResponse.StatusCode);
        using var profileJson = System.Text.Json.JsonDocument.Parse(await profileResponse.Content.ReadAsStringAsync());
        Assert.Equal("garage-watch-platform", profileJson.RootElement.GetProperty("serviceName").GetString());
        Assert.True(profileJson.RootElement.TryGetProperty("applicationVersion", out _));

        HttpResponseMessage? playlist = null;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            playlist?.Dispose();
            playlist = await session.GetAsync("/api/v1/live/capture-test/index.m3u8");
            if (playlist.StatusCode == HttpStatusCode.OK) break;
            await Task.Delay(100);
        }
        Assert.NotNull(playlist);
        using (playlist)
        {
            Assert.Equal(HttpStatusCode.OK, playlist.StatusCode);
            var m3u8 = await playlist.Content.ReadAsStringAsync();
            Assert.Contains("#EXTM3U", m3u8, StringComparison.Ordinal);
            var firstSequence = ReadMediaSequence(m3u8);
            var segmentUrl = m3u8.Split('\n').First(line => line.StartsWith("/api/v1/live/", StringComparison.Ordinal)).Trim();
            using var segment = await session.GetAsync(segmentUrl);
            Assert.Equal(HttpStatusCode.OK, segment.StatusCode);
            Assert.True((await segment.Content.ReadAsByteArrayAsync()).Length > 0);

            var segmentDeadline = DateTime.UtcNow.AddSeconds(15);
            var playlistStartAdvanced = false;
            long refreshedSequence = firstSequence;
            while (DateTime.UtcNow < segmentDeadline)
            {
                using var refreshedPlaylist = await session.GetAsync("/api/v1/live/capture-test/index.m3u8");
                if (refreshedPlaylist.StatusCode == HttpStatusCode.OK)
                {
                    var refreshedBody = await refreshedPlaylist.Content.ReadAsStringAsync();
                    refreshedSequence = ReadMediaSequence(refreshedBody);
                    var refreshedFirstSegment = refreshedBody.Split('\n')
                        .First(line => line.StartsWith("/api/v1/live/", StringComparison.Ordinal)).Trim();
                    if (refreshedSequence > firstSequence && refreshedFirstSegment != segmentUrl)
                    {
                        playlistStartAdvanced = true;
                        break;
                    }
                }
                await Task.Delay(100);
            }
            Assert.True(playlistStartAdvanced,
                $"Expected media sequence to advance with the sliding playlist, received {firstSequence} then {refreshedSequence}; initial segments: {initialSegmentCount}.");
        }
        using var logout = await session.PostAsync("/api/v1/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        session.DefaultRequestHeaders.Remove("Cookie");
        var loggedOutSession = await session.GetFromJsonAsync<Dictionary<string, bool>>("/api/v1/auth/session");
        Assert.NotNull(loggedOutSession);
        Assert.False(loggedOutSession["authenticated"]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/v1/cameras")).StatusCode);
    }

    [Fact]
    public async Task InvalidCredentialsAreRejectedWithoutRedirectOrCookie()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "wrong-password-123" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Viewer_can_monitor_but_cannot_read_administrative_status()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var viewer = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = "viewer-user",
                Email = "viewer@example.invalid",
                IsActive = true
            };
            var created = await userManager.CreateAsync(viewer, "Viewer-Password-123!");
            Assert.True(created.Succeeded, string.Join(",", created.Errors.Select(error => error.Code)));
            Assert.True((await userManager.AddToRoleAsync(viewer, "Viewer")).Succeeded);
            var administrator = (await userManager.FindByNameAsync("contract-user"))!;
            var captureOptions = scope.ServiceProvider.GetRequiredService<GarageWatch.Infrastructure.Capture.CaptureOptions>();
            await scope.ServiceProvider.GetRequiredService<IdentityAdministrationService>()
                .GrantCameraAsync(administrator.Id, viewer.Id, "garage", captureOptions.CameraId);
        }
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "viewer-user", password = "Viewer-Password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);

        var me = await client.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/auth/me");
        Assert.NotNull(me);
        Assert.Equal("Viewer", me["role"]);
        using var cameras = await client.GetAsync("/api/v1/cameras");
        Assert.Equal(HttpStatusCode.OK, cameras.StatusCode);
        using var cameraJson = System.Text.Json.JsonDocument.Parse(await cameras.Content.ReadAsStringAsync());
        Assert.True(cameraJson.RootElement[0].TryGetProperty("liveUrl", out _));
        Assert.False(cameraJson.RootElement[0].TryGetProperty("segments", out _));
        Assert.False(cameraJson.RootElement[0].TryGetProperty("gaps", out _));

        using var readiness = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.Forbidden, readiness.StatusCode);
        using var profile = await client.GetAsync("/api/v1/system-profile");
        Assert.Equal(HttpStatusCode.Forbidden, profile.StatusCode);
        using var presence = await client.PutAsJsonAsync("/api/v1/clients/presence", new { clientId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, presence.StatusCode);
    }

    private static long ReadMediaSequence(string playlist)
    {
        var line = playlist.Split('\n').First(value => value.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal));
        return long.Parse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }
        return count;
    }
}

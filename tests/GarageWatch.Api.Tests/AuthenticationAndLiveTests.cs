using System.Net;
using System.Net.Http.Json;
using GarageWatch.Api.Security;
using GarageWatch.Api.Live;
using Microsoft.Extensions.Configuration;
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
    [InlineData("abc123", true)]
    [InlineData("abc12", false)]
    [InlineData("replace-with-a-local-password-of-6-or-more-characters", false)]
    public void LocalPasswordMustMeetSixCharacterMinimumAndRejectExample(string password, bool accepted)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AUTH_USERNAME"] = "contract-user",
            ["AUTH_PASSWORD"] = password,
            ["AUTH_ROLE"] = "Viewer"
        }).Build();

        if (accepted)
            Assert.NotNull(LocalAuthenticationOptions.FromEnvironment(configuration));
        else
            Assert.Throws<InvalidOperationException>(() => LocalAuthenticationOptions.FromEnvironment(configuration));
    }

    [Fact]
    public async Task CameraAndLiveEndpointsRequireSessionAndLoginIssuesHttpOnlyCookie()
    {
        await using var factory = new ApiFactory();
        using var anonymous = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var panel = await anonymous.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, panel.StatusCode);
        var html = await panel.Content.ReadAsStringAsync();
        Assert.Contains("Painel da garagem", html, StringComparison.Ordinal);
        Assert.Contains("/vendor/hls.min.js", html, StringComparison.Ordinal);
        Assert.Contains("/vendor/bootstrap.min.css", html, StringComparison.Ordinal);
        Assert.Contains("/app.css", html, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn.", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("for=\"username\"", html, StringComparison.Ordinal);
        Assert.Contains("for=\"password\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Vídeo ao vivo da câmera\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Servidores e conexões\"", html, StringComparison.Ordinal);
        Assert.Contains("Não configurados", html, StringComparison.Ordinal);
        Assert.Contains("id=\"api-health\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"capture-health\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"gateway-health\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"gateway-health\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"client-count\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Clientes conectados\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"player-toggle\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"player-sync\"", html, StringComparison.Ordinal);
        var cameraSection = html.IndexOf("CÂMERA ATIVA", StringComparison.Ordinal);
        var monitorSection = html.IndexOf("MONITORAMENTO", StringComparison.Ordinal);
        var operationsSection = html.IndexOf("Saúde do sistema", StringComparison.Ordinal);
        Assert.True(cameraSection >= 0 && cameraSection < monitorSection && monitorSection < operationsSection);
        Assert.Contains("<video id=\"live\" class=\"live-video\" controls playsinline preload=\"none\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("autoplay muted", html, StringComparison.Ordinal);
        var bootstrapCss = await anonymous.GetByteArrayAsync("/vendor/bootstrap.min.css");
        Assert.Equal("a7695108838ea4708a547d2d07fb37c4889eb663fb18c3761677ed6a50c4fe41",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bootstrapCss)).ToLowerInvariant());
        var bootstrapText = System.Text.Encoding.UTF8.GetString(bootstrapCss);
        Assert.Contains(".container", bootstrapText, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceMappingURL", bootstrapText, StringComparison.Ordinal);
        var appCss = await anonymous.GetStringAsync("/app.css");
        Assert.Contains(":focus-visible", appCss, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion", appCss, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 575.98px)", appCss, StringComparison.Ordinal);
        using var favicon = await anonymous.GetAsync("/favicon.svg");
        Assert.Equal(HttpStatusCode.OK, favicon.StatusCode);
        var appScript = await anonymous.GetStringAsync("/app.js");
        Assert.Contains("Falha HLS (", appScript, StringComparison.Ordinal);
        Assert.Contains("maxBufferLength: 12", appScript, StringComparison.Ordinal);
        Assert.Contains("maxMaxBufferLength: 18", appScript, StringComparison.Ordinal);
        Assert.Contains("xhr.withCredentials = true", appScript, StringComparison.Ordinal);
        Assert.Contains("/health/live", appScript, StringComparison.Ordinal);
        Assert.Contains("/health/ready", appScript, StringComparison.Ordinal);
        Assert.Contains("/api/v1/system-profile", appScript, StringComparison.Ordinal);
        Assert.Contains("/api/v1/system-profile", appScript, StringComparison.Ordinal);
        Assert.Contains("/api/v1/clients/presence", appScript, StringComparison.Ordinal);
        Assert.Contains("crypto.randomUUID()", appScript, StringComparison.Ordinal);
        Assert.Contains("setInterval(() => void sendClientHeartbeat(), 10000)", appScript, StringComparison.Ordinal);
        Assert.Contains("hls.liveSyncPosition", appScript, StringComparison.Ordinal);
        Assert.Contains("video.seekable.end(range)", appScript, StringComparison.Ordinal);
        Assert.Contains("video.currentTime = target", appScript, StringComparison.Ordinal);
        Assert.Contains("camera.liveUrl", appScript, StringComparison.Ordinal);
        Assert.Contains("delete video.dataset.url", appScript, StringComparison.Ordinal);
        Assert.Contains("capture_source_unavailable", appScript, StringComparison.Ordinal);
        Assert.Contains("Clique em reproduzir para iniciar áudio e vídeo.", html, StringComparison.Ordinal);
        var player = await anonymous.GetByteArrayAsync("/vendor/hls.min.js");
        Assert.Equal("de49f5a3084f73c9e3e4e2999d6e789f8ea5aff0120d295631ecb9bf1480b3ea",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(player)).ToLowerInvariant());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/cameras")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/live/garage-01/index.m3u8")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/health/ready")).StatusCode);
        var anonymousSession = await anonymous.GetFromJsonAsync<Dictionary<string, bool>>("/api/v1/auth/session");
        Assert.NotNull(anonymousSession);
        Assert.False(anonymousSession["authenticated"]);

        using var login = await anonymous.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "contract-password-12345" });
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
        var initialSegmentCount = cameraJson.RootElement[0].GetProperty("segments").GetInt64();

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
            var spool = readinessJson.RootElement.GetProperty("capture").GetProperty("spool");
            Assert.Equal("available", spool.GetProperty("status").GetString());
            Assert.Equal(4, spool.GetProperty("segmentLimit").GetInt32());
            Assert.InRange(spool.GetProperty("segments").GetInt32(), 1, 4);
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

    private static long ReadMediaSequence(string playlist)
    {
        var line = playlist.Split('\n').First(value => value.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal));
        return long.Parse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], System.Globalization.CultureInfo.InvariantCulture);
    }
}

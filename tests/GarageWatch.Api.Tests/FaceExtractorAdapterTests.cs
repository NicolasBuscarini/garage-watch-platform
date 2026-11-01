using System.Security.Cryptography;
using GarageWatch.Application.Biometrics;
using GarageWatch.Infrastructure.Biometrics;
using GarageWatch.Infrastructure.Capture;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class FaceExtractorAdapterTests
{
    private const int ImageSize = 64;

    [Fact]
    public async Task Extract_enrolls_the_detected_face_into_a_128_dimension_embedding()
    {
        using var scope = new FixtureScope(FaceScore.High, faces: 1);
        var embedding = await scope.Extractor.ExtractAsync(Crop(), CancellationToken.None);

        Assert.Equal("face-embedding.v1", embedding.SchemaVersion);
        Assert.Equal("opencv-zoo-sface", embedding.ModelId);
        Assert.Equal("2021dec", embedding.ModelVersion);
        Assert.Equal(128, embedding.Dimensions);
        Assert.True(embedding.IsValid);
        Assert.All(embedding.Values, value => Assert.True(float.IsFinite(value)));
    }

    [Fact]
    public async Task Extract_rejects_images_without_a_face_or_with_several_faces()
    {
        using var empty = new FixtureScope(FaceScore.None, faces: 1);
        var none = await Assert.ThrowsAsync<FaceExtractionException>(() =>
            empty.Extractor.ExtractAsync(Crop(), CancellationToken.None).AsTask());
        Assert.Equal("face_not_detected", none.Code);

        using var several = new FixtureScope(FaceScore.High, faces: 2);
        var multiple = await Assert.ThrowsAsync<FaceExtractionException>(() =>
            several.Extractor.ExtractAsync(Crop(), CancellationToken.None).AsTask());
        Assert.Equal("multiple_faces", multiple.Code);
    }

    [Fact]
    public async Task Extract_rejects_faces_below_the_configured_minimum_size()
    {
        using var scope = new FixtureScope(FaceScore.High, faces: 1, minimumPixels: 256);
        var tooSmall = await Assert.ThrowsAsync<FaceExtractionException>(() =>
            scope.Extractor.ExtractAsync(Crop(), CancellationToken.None).AsTask());
        Assert.Equal("face_too_small", tooSmall.Code);
    }

    [Fact]
    public async Task Extract_rejects_unreadable_image_bytes_without_touching_the_models()
    {
        using var scope = new FixtureScope(FaceScore.High, faces: 1);
        var crop = new GarageWatch.Application.Analysis.FaceCropV1("image/jpeg", ImageSize, ImageSize,
            new byte[] { 0x01, 0x02, 0x03 });
        var failure = await Assert.ThrowsAsync<FaceExtractionException>(() =>
            scope.Extractor.ExtractAsync(crop, CancellationToken.None).AsTask());
        Assert.Equal("image_decode_failed", failure.Code);
    }

    [Fact]
    public async Task Extract_fails_closed_when_artifacts_are_missing_or_tampered()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"garagewatch-face-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var detectorPath = Path.Combine(directory, "detector.onnx");
            var recognizerPath = Path.Combine(directory, "recognizer.onnx");
            await File.WriteAllTextAsync(detectorPath, "not-a-model");
            await File.WriteAllTextAsync(recognizerPath, "not-a-model");
            var options = new FaceExtractorOptions("opencv-zoo-sface", "2021dec", detectorPath, new string('a', 64),
                recognizerPath, new string('b', 64), .9f, .3f, 16, 640, 1, 20);
            var runtime = new FaceExtractorRuntime(options, NullLogger<FaceExtractorRuntime>.Instance);
            Assert.False(runtime.Status.Enabled);
            Assert.Equal(FaceExtractorRuntime.ChecksumMismatchState, runtime.Status.State);
            using var extractor = new OnnxFaceEnrollmentExtractor(runtime,
                new CaptureOptions("synthetic", "camera-1", directory, 1, 10, 1_048_576, null, "ffmpeg", "ffprobe"));
            var failure = await Assert.ThrowsAsync<FaceExtractionException>(() =>
                extractor.ExtractAsync(Crop(), CancellationToken.None).AsTask());
            Assert.Equal("face_extractor_not_configured", failure.Code);

            var missing = new FaceExtractorOptions("opencv-zoo-sface", "2021dec",
                Path.Combine(directory, "absent.onnx"), new string('a', 64), recognizerPath, new string('b', 64),
                .9f, .3f, 16, 640, 1, 20);
            Assert.Equal(FaceExtractorRuntime.ArtifactUnavailableState,
                new FaceExtractorRuntime(missing, NullLogger<FaceExtractorRuntime>.Instance).Status.State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static GarageWatch.Application.Analysis.FaceCropV1 Crop()
    {
        var path = Path.Combine(Path.GetTempPath(), $"garagewatch-face-{Guid.NewGuid():N}.jpg");
        try
        {
            RunFfmpeg(path);
            var bytes = File.ReadAllBytes(path);
            return new GarageWatch.Application.Analysis.FaceCropV1("image/jpeg", ImageSize, ImageSize, bytes);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void RunFfmpeg(string destination)
    {
        var start = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
                     $"color=c=gray:s={ImageSize}x{ImageSize}", "-frames:v", "1", "-y", destination
                 })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("ffmpeg_start_failed");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
    }

    private enum FaceScore
    {
        None,
        High
    }

    /// <summary>Builds a detector fixture with N face cells at stride 8 and a constant SFace output.</summary>
    private sealed class FixtureScope : IDisposable
    {
        private readonly string _directory;
        public OnnxFaceEnrollmentExtractor Extractor { get; }

        public FixtureScope(FaceScore score, int faces, int minimumPixels = 16)
        {
            _directory = Path.Combine(Path.GetTempPath(), $"garagewatch-face-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            var detectorPath = Path.Combine(_directory, "detector.onnx");
            var recognizerPath = Path.Combine(_directory, "recognizer.onnx");
            File.WriteAllBytes(detectorPath, BuildDetector(score, faces));
            File.WriteAllBytes(recognizerPath, OnnxFixture.CreateModel([("data", [1, 3, 112, 112])],
                ("fc1", [1, 128], Enumerable.Range(0, 128).Select(index => index * 0.01f).ToArray())));
            var options = new FaceExtractorOptions("opencv-zoo-sface", "2021dec", detectorPath, Sha256(detectorPath),
                recognizerPath, Sha256(recognizerPath), .9f, .3f, minimumPixels, 640, 1, 20);
            var runtime = new FaceExtractorRuntime(options, NullLogger<FaceExtractorRuntime>.Instance);
            Assert.True(runtime.Status.Enabled, runtime.Status.State);
            Extractor = new OnnxFaceEnrollmentExtractor(runtime,
                new CaptureOptions("synthetic", "camera-1", _directory, 1, 10, 1_048_576, null, "ffmpeg", "ffprobe"));
        }

        public void Dispose()
        {
            Extractor.Dispose();
            Directory.Delete(_directory, recursive: true);
        }

        private static byte[] BuildDetector(FaceScore score, int faces)
        {
            var cells = new Dictionary<int, int>
            {
                [8] = (ImageSize / 8) * (ImageSize / 8),
                [16] = (ImageSize / 16) * (ImageSize / 16),
                [32] = (ImageSize / 32) * (ImageSize / 32)
            };
            var outputs = new List<(string, int[], float[])>();
            foreach (var (stride, count) in cells)
            {
                var cls = new float[count];
                var obj = new float[count];
                var bbox = new float[count * 4];
                var kps = new float[count * 10];
                if (stride == 8 && score == FaceScore.High)
                {
                    (int Row, int Column)[] cells8 = faces == 2 ? [(2, 2), (6, 6)] : [(2, 2)];
                    foreach (var (row, column) in cells8)
                    {
                        var index = row * (ImageSize / 8) + column;
                        var centerX = column * stride;
                        var centerY = row * stride;
                        cls[index] = 1f;
                        obj[index] = .95f;
                        bbox[index * 4] = 0;
                        bbox[index * 4 + 1] = 0;
                        bbox[index * 4 + 2] = MathF.Log(32f / stride);
                        bbox[index * 4 + 3] = MathF.Log(32f / stride);
                        (int X, int Y)[] landmarks = [(-8, -8), (8, -8), (0, 4), (-8, 12), (8, 12)];
                        for (var point = 0; point < 5; point++)
                        {
                            kps[index * 10 + point * 2] = (centerX + landmarks[point].X) / stride - column;
                            kps[index * 10 + point * 2 + 1] = (centerY + landmarks[point].Y) / stride - row;
                        }
                    }
                }
                outputs.Add(($"cls_{stride}", [1, count, 1], cls));
                outputs.Add(($"obj_{stride}", [1, count, 1], obj));
                outputs.Add(($"bbox_{stride}", [1, count, 4], bbox));
                outputs.Add(($"kps_{stride}", [1, count, 10], kps));
            }
            return OnnxFixture.CreateModel([("input", [1, 3, ImageSize, ImageSize])], outputs.ToArray());
        }

        private static string Sha256(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
    }
}

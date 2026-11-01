using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GarageWatch.Infrastructure.Biometrics;

/// <summary>Face candidate in original image coordinates: box plus the five YuNet landmarks.</summary>
public sealed record DetectedFace(float X, float Y, float Width, float Height, IReadOnlyList<FacePoint> Landmarks)
{
    public bool IsValid => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Width) &&
                           float.IsFinite(Height) && Width > 0 && Height > 0 && Landmarks.Count == 5;
}

public sealed record FacePoint(float X, float Y);

/// <summary>
/// YuNet (opencv_zoo 2023mar) inference over an in-memory RGB image. Pre/post-processing is implemented
/// locally because the project only depends on the ONNX Runtime adapter.
/// </summary>
public sealed class YuNetFaceDetector : IDisposable
{
    private static readonly int[] Strides = [8, 16, 32];
    private readonly FaceExtractorOptions _options;
    private readonly InferenceSession _session;

    public YuNetFaceDetector(FaceExtractorOptions options)
    {
        _options = options;
        using var sessionOptions = new SessionOptions
        {
            IntraOpNumThreads = options.IntraOpThreads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };
        _session = new InferenceSession(options.DetectorModelPath!, sessionOptions);
        foreach (var name in OutputNames())
            if (!_session.OutputMetadata.ContainsKey(name))
                throw new InvalidOperationException("face_detector_outputs_incompatible");
    }

    public IReadOnlyList<DetectedFace> Detect(RgbImage image)
    {
        var scale = Math.Min(1f, (float)_options.MaximumInputSize / Math.Max(image.Width, image.Height));
        var width = Math.Max(32, (int)(image.Width * scale) / 32 * 32);
        var height = Math.Max(32, (int)(image.Height * scale) / 32 * 32);
        var scaleX = (float)width / image.Width;
        var scaleY = (float)height / image.Height;
        var input = Resize(image, width, height);
        using var results = _session.Run(
            [NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(input, [1, 3, height, width]))],
            OutputNames());
        var candidates = new List<DetectedFace>(32);
        foreach (var stride in Strides)
        {
            var cols = width / stride;
            var rows = height / stride;
            candidates.AddRange(YuNetPostProcess.Decode(
                results.First(result => result.Name == $"cls_{stride}").AsTensor<float>().ToArray(),
                results.First(result => result.Name == $"obj_{stride}").AsTensor<float>().ToArray(),
                results.First(result => result.Name == $"bbox_{stride}").AsTensor<float>().ToArray(),
                results.First(result => result.Name == $"kps_{stride}").AsTensor<float>().ToArray(),
                cols, rows, stride, _options.ScoreThreshold));
        }
        var kept = YuNetPostProcess.Suppress(candidates, _options.NmsThreshold);
        return kept
            .Select(face => face with
            {
                X = face.X / scaleX,
                Y = face.Y / scaleY,
                Width = face.Width / scaleX,
                Height = face.Height / scaleY,
                Landmarks = face.Landmarks.Select(point => new FacePoint(point.X / scaleX, point.Y / scaleY)).ToArray()
            })
            .ToArray();
    }

    public void Dispose() => _session.Dispose();

    internal static string[] OutputNames() =>
        Strides.SelectMany(stride => new[] { $"cls_{stride}", $"obj_{stride}", $"bbox_{stride}", $"kps_{stride}" })
            .ToArray();

    private static float[] Resize(RgbImage image, int width, int height)
    {
        var plane = width * height;
        var tensor = new float[3 * plane];
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(image.Height - 1, (int)((y + 0.5f) * image.Height / height));
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(image.Width - 1, (int)((x + 0.5f) * image.Width / width));
                var source = (sourceY * image.Width + sourceX) * 3;
                var target = y * width + x;
                tensor[target] = image.Pixels[source];
                tensor[plane + target] = image.Pixels[source + 1];
                tensor[2 * plane + target] = image.Pixels[source + 2];
            }
        }
        return tensor;
    }
}

/// <summary>Pure YuNet decoding/NMS helpers, kept separate so they can be unit tested.</summary>
public static class YuNetPostProcess
{
    public static IReadOnlyList<DetectedFace> Decode(float[] cls, float[] obj, float[] bbox, float[] kps, int cols,
        int rows, int stride, float scoreThreshold)
    {
        var cells = cols * rows;
        if (cells <= 0 || cls.Length < cells || obj.Length < cells || bbox.Length < cells * 4 ||
            kps.Length < cells * 10) return [];
        var faces = new List<DetectedFace>(cells);
        for (var index = 0; index < cells; index++)
        {
            var classScore = cls[index];
            var objectScore = obj[index];
            if (!float.IsFinite(classScore) || !float.IsFinite(objectScore) ||
                classScore is < 0 or > 1 || objectScore is < 0 or > 1) continue;
            var score = MathF.Sqrt(classScore * objectScore);
            if (score < scoreThreshold) continue;
            var row = index / cols;
            var column = index % cols;
            var centerX = (bbox[index * 4] + column) * stride;
            var centerY = (bbox[index * 4 + 1] + row) * stride;
            var width = MathF.Exp(bbox[index * 4 + 2]) * stride;
            var height = MathF.Exp(bbox[index * 4 + 3]) * stride;
            if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0) continue;
            var landmarks = new FacePoint[5];
            for (var point = 0; point < 5; point++)
                landmarks[point] = new FacePoint((kps[index * 10 + point * 2] + column) * stride,
                    (kps[index * 10 + point * 2 + 1] + row) * stride);
            var face = new DetectedFace(centerX - width / 2, centerY - height / 2, width, height, landmarks);
            if (face.IsValid) faces.Add(face);
        }
        return faces;
    }

    public static IReadOnlyList<DetectedFace> Suppress(IReadOnlyList<DetectedFace> faces, float threshold)
    {
        var ordered = faces.OrderByDescending(face => face.Width * face.Height).ToArray();
        var kept = new List<DetectedFace>(ordered.Length);
        foreach (var candidate in ordered)
        {
            if (kept.Any(face => IntersectionOverUnion(face, candidate) > threshold)) continue;
            kept.Add(candidate);
        }
        return kept;
    }

    public static float IntersectionOverUnion(DetectedFace left, DetectedFace right)
    {
        var x1 = Math.Max(left.X, right.X);
        var y1 = Math.Max(left.Y, right.Y);
        var x2 = Math.Min(left.X + left.Width, right.X + right.Width);
        var y2 = Math.Min(left.Y + left.Height, right.Y + right.Height);
        var width = Math.Max(0, x2 - x1);
        var height = Math.Max(0, y2 - y1);
        var intersection = width * height;
        var union = left.Width * left.Height + right.Width * right.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}

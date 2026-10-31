using GarageWatch.Application.Analysis;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GarageWatch.Infrastructure.Analysis;

public sealed class OnnxObjectDetector : IObjectDetector, IDisposable
{
    private readonly OnnxDetectorOptions _options;
    private readonly Lazy<InferenceSession?> _session;

    public OnnxObjectDetector(OnnxDetectorOptions options)
    {
        _options = options;
        _session = new Lazy<InferenceSession?>(() =>
        {
            if (!options.Enabled) return null;
            if (!File.Exists(options.ModelPath)) throw new InvalidOperationException("detector_model_unavailable");
            var sessionOptions = new SessionOptions
            {
                IntraOpNumThreads = options.IntraOpThreads,
                InterOpNumThreads = options.InterOpThreads,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
            };
            using var sessionOptionsLifetime = sessionOptions;
            var session = new InferenceSession(options.ModelPath!, sessionOptions);
            if (!session.InputMetadata.ContainsKey(options.InputName) || !session.OutputMetadata.ContainsKey(options.OutputName))
            {
                session.Dispose();
                throw new InvalidOperationException("Configured ONNX input/output names are incompatible.");
            }
            return session;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string ModelVersion => _options.ModelVersion;

    public ValueTask<IReadOnlyList<ObjectDetection>> DetectAsync(DetectionFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = _session.Value;
        if (session is null) throw new InvalidOperationException("detector_not_configured");
        if (frame.Width != _options.Width || frame.Height != _options.Height
            || frame.RgbNchw.Length != checked(3 * _options.Width * _options.Height))
            throw new InvalidDataException("detector_input_shape_invalid");
        var tensor = new DenseTensor<float>(frame.RgbNchw.ToArray(), [1, 3, _options.Height, _options.Width]);
        using var results = session.Run([NamedOnnxValue.CreateFromTensor(_options.InputName, tensor)], [_options.OutputName]);
        cancellationToken.ThrowIfCancellationRequested();
        var output = results.First().AsTensor<float>();
        if (output.Length % 6 != 0 || output.Length > 6 * 10000)
            throw new InvalidDataException("detector_output_shape_invalid");
        var values = output.ToArray();
        var detections = new List<ObjectDetection>(Math.Min(values.Length / 6, 256));
        for (var offset = 0; offset < values.Length; offset += 6)
        {
            var classValue = values[offset + 5];
            if (!float.IsFinite(classValue) || classValue < 0 || classValue != MathF.Truncate(classValue)
                || classValue >= _options.Labels.Length
                || values[offset + 4] < _options.ScoreThreshold) continue;
            var box = new DetectionBox(values[offset], values[offset + 1], values[offset + 2], values[offset + 3]);
            if (!box.IsValid || !float.IsFinite(values[offset + 4]) || values[offset + 4] > 1) continue;
            var classId = (int)classValue;
            detections.Add(new ObjectDetection(_options.Labels[classId], classId, values[offset + 4], box));
            if (detections.Count == 256) break;
        }
        return ValueTask.FromResult<IReadOnlyList<ObjectDetection>>(detections);
    }

    public void Dispose()
    {
        if (_session.IsValueCreated) _session.Value?.Dispose();
    }
}

using GarageWatch.Application.Biometrics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GarageWatch.Infrastructure.Biometrics;

/// <summary>Enrollment-time extraction adapter: YuNet detection + SFace embedding over ONNX Runtime CPU.</summary>
public sealed class OnnxFaceEnrollmentExtractor : Application.Biometrics.IFaceEnrollmentExtractor, IDisposable
{
    private readonly FaceExtractorRuntime _runtime;
    private readonly Capture.CaptureOptions _capture;
    private readonly Lazy<YuNetFaceDetector> _detector;
    private readonly Lazy<InferenceSession> _recognizer;

    public OnnxFaceEnrollmentExtractor(FaceExtractorRuntime runtime, Capture.CaptureOptions capture)
    {
        _runtime = runtime;
        _capture = capture;
        _detector = new Lazy<YuNetFaceDetector>(() => new YuNetFaceDetector(runtime.Options),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _recognizer = new Lazy<InferenceSession>(CreateRecognizerSession, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string ModelId => _runtime.Options.ModelId;
    public string ModelVersion => _runtime.Options.ModelVersion;
    public string AdapterId => "onnxruntime-cpu";

    public async ValueTask<Application.Analysis.FaceEmbeddingV1> ExtractAsync(
        Application.Analysis.FaceCropV1 crop, CancellationToken cancellationToken)
    {
        if (!crop.IsValid) throw new Application.Biometrics.FaceExtractionException("invalid_biometric_image");
        if (!_runtime.Status.Enabled)
            throw new Application.Biometrics.FaceExtractionException("face_extractor_not_configured");
        var image = await FaceImageDecoder.DecodeAsync(_capture.FfmpegPath, crop.EncodedImage.ToArray(),
            _runtime.Options.TimeoutSeconds, cancellationToken);
        if (image is null) throw new Application.Biometrics.FaceExtractionException("image_decode_failed");

        var faces = _detector.Value.Detect(image);
        if (faces.Count == 0) throw new Application.Biometrics.FaceExtractionException("face_not_detected");
        if (faces.Count > 1) throw new Application.Biometrics.FaceExtractionException("multiple_faces");
        var face = faces[0];
        if (Math.Min(face.Width, face.Height) < _runtime.Options.MinimumFacePixels)
            throw new Application.Biometrics.FaceExtractionException("face_too_small");

        SimilarityTransform transform;
        try
        {
            transform = FaceAlignment.Fit(face.Landmarks);
        }
        catch (ArgumentException)
        {
            throw new Application.Biometrics.FaceExtractionException("face_detection_invalid");
        }
        var tensor = FaceAlignment.Warp(image, transform);
        var values = Embed(tensor);
        var embedding = new Application.Analysis.FaceEmbeddingV1(
            Application.Analysis.FaceEmbeddingV1.CurrentSchemaVersion, ModelId, ModelVersion, values.Count, values);
        if (!embedding.IsValid) throw new Application.Biometrics.FaceExtractionException("face_embedding_invalid");
        return embedding;
    }

    public void Dispose()
    {
        if (_detector.IsValueCreated) _detector.Value.Dispose();
        if (_recognizer.IsValueCreated) _recognizer.Value.Dispose();
    }

    private IReadOnlyList<float> Embed(float[] tensor)
    {
        using var results = _recognizer.Value.Run(
            [NamedOnnxValue.CreateFromTensor("data",
                new DenseTensor<float>(tensor, [1, 3, FaceAlignment.OutputSize, FaceAlignment.OutputSize]))],
            ["fc1"]);
        var output = results.First().AsTensor<float>().ToArray();
        if (output.Length == 0 || output.Length > 4096 || output.Any(value => !float.IsFinite(value)))
            throw new Application.Biometrics.FaceExtractionException("face_embedding_invalid");
        return output;
    }

    private InferenceSession CreateRecognizerSession()
    {
        using var sessionOptions = new SessionOptions
        {
            IntraOpNumThreads = _runtime.Options.IntraOpThreads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };
        var session = new InferenceSession(_runtime.Options.RecognizerModelPath!, sessionOptions);
        if (!session.InputMetadata.ContainsKey("data") || !session.OutputMetadata.ContainsKey("fc1"))
        {
            session.Dispose();
            throw new InvalidOperationException("face_recognizer_outputs_incompatible");
        }
        return session;
    }
}

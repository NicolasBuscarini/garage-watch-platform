using GarageWatch.Infrastructure.Biometrics;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class FaceGeometryTests
{
    [Fact]
    public void YuNet_decoding_applies_cell_offsets_exp_size_and_score_threshold()
    {
        // 2x2 grid at stride 8: only cell (row 1, column 1) holds a face.
        var cls = new float[] { 0, 0, 0, 1 };
        var obj = new float[] { 0, 0, 0, 0.81f };
        var bbox = new float[16];
        bbox[14] = MathF.Log(2f);
        bbox[15] = MathF.Log(2f);
        var kps = new float[40];
        for (var point = 0; point < 5; point++)
        {
            kps[30 + point * 2] = point;
            kps[30 + point * 2 + 1] = 1;
        }

        var faces = YuNetPostProcess.Decode(cls, obj, bbox, kps, 2, 2, 8, 0.9f);
        var face = Assert.Single(faces);
        Assert.Equal(8f, face.X + face.Width / 2, 3);
        Assert.Equal(8f, face.Y + face.Height / 2, 3);
        Assert.Equal(16f, face.Width, 3);
        Assert.Equal(5, face.Landmarks.Count);
        Assert.Equal(8f, face.Landmarks[0].X, 3);

        // The same score below the threshold drops the candidate.
        Assert.Empty(YuNetPostProcess.Decode(cls, obj, bbox, kps, 2, 2, 8, 0.95f));
        // Invalid scores never become faces.
        Assert.Empty(YuNetPostProcess.Decode([5f, 0, 0, 0], obj, bbox, kps, 2, 2, 8, 0.1f));
    }

    [Fact]
    public void Non_maximum_suppression_keeps_the_largest_non_overlapping_face()
    {
        static DetectedFace Face(float x, float width) => new(x, 0, width, width,
            [new(0, 0), new(0, 0), new(0, 0), new(0, 0), new(0, 0)]);

        var kept = YuNetPostProcess.Suppress([Face(0, 30), Face(2, 31), Face(60, 20)], 0.3f);
        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, face => face.Width == 31);
        Assert.Contains(kept, face => face.Width == 20);
        Assert.True(YuNetPostProcess.IntersectionOverUnion(Face(0, 10), Face(0, 10)) > 0.99f);
    }

    [Fact]
    public void Similarity_fit_maps_detected_landmarks_onto_the_reference_template()
    {
        // Synthetic "detected" landmarks are the template scaled by 2, rotated 30° and shifted.
        var angle = MathF.PI / 6;
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        FacePoint[] template =
        [
            new(38.2946f / 112f, 51.6963f / 112f),
            new(73.5318f / 112f, 51.5014f / 112f),
            new(56.0252f / 112f, 71.7366f / 112f),
            new(41.5493f / 112f, 92.3655f / 112f),
            new(70.7299f / 112f, 92.2041f / 112f)
        ];
        var detected = template
            .Select(point => new FacePoint(
                (cos * point.X - sin * point.Y) * 2f + 0.4f,
                (sin * point.X + cos * point.Y) * 2f + 0.25f))
            .ToArray();

        var transform = FaceAlignment.Fit(detected);
        foreach (var (point, index) in template.Select((point, index) => (point, index)))
        {
            var mappedX = transform.Scale * (transform.Cos * detected[index].X - transform.Sin * detected[index].Y) +
                          transform.OffsetX;
            var mappedY = transform.Scale * (transform.Sin * detected[index].X + transform.Cos * detected[index].Y) +
                          transform.OffsetY;
            Assert.Equal(point.X, mappedX, 3);
            Assert.Equal(point.Y, mappedY, 3);
        }
    }

    [Fact]
    public void Warp_normalizes_rgb_into_the_sface_tensor_shape()
    {
        var pixels = new byte[112 * 112 * 3];
        Array.Fill(pixels, (byte)255);
        var image = new RgbImage(112, 112, pixels);
        var transform = new SimilarityTransform(1f, 1f, 0f, 0f, 0f);

        var tensor = FaceAlignment.Warp(image, transform);
        Assert.Equal(3 * 112 * 112, tensor.Length);
        var expected = (255f - 127.5f) / 128f;
        Assert.All(tensor, value => Assert.Equal(expected, value, 4));
    }

    [Fact]
    public void Degenerate_landmarks_are_rejected()
    {
        FacePoint[] identical = [new(1, 1), new(1, 1), new(1, 1), new(1, 1), new(1, 1)];
        Assert.Throws<ArgumentException>(() => FaceAlignment.Fit(identical));
        Assert.Throws<ArgumentException>(() => FaceAlignment.Fit([new(0, 0)]));
    }
}

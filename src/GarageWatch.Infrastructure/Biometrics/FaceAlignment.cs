namespace GarageWatch.Infrastructure.Biometrics;

/// <summary>
/// Umeyama/Park similarity fit (rotation + uniform scale + translation) between the five detected landmarks
/// and the canonical 112×112 ArcFace template used by SFace, plus bilinear warping.
/// </summary>
public static class FaceAlignment
{
    public const int OutputSize = 112;
    private const float ReferenceScale = 112f;

    private static readonly FacePoint[] Reference =
    [
        new(38.2946f / ReferenceScale, 51.6963f / ReferenceScale),
        new(73.5318f / ReferenceScale, 51.5014f / ReferenceScale),
        new(56.0252f / ReferenceScale, 71.7366f / ReferenceScale),
        new(41.5493f / ReferenceScale, 92.3655f / ReferenceScale),
        new(70.7299f / ReferenceScale, 92.2041f / ReferenceScale)
    ];

    /// <summary>
    /// Returns the similarity that maps the detected landmarks onto the reference template. Throws
    /// <see cref="ArgumentException"/> when the landmarks are degenerate (all points coincide or collinear).
    /// </summary>
    public static SimilarityTransform Fit(IReadOnlyList<FacePoint> landmarks)
    {
        if (landmarks.Count != 5) throw new ArgumentException("Cinco pontos faciais são obrigatórios.", nameof(landmarks));
        float sourceMeanX = 0, sourceMeanY = 0, targetMeanX = 0, targetMeanY = 0;
        for (var index = 0; index < 5; index++)
        {
            sourceMeanX += landmarks[index].X;
            sourceMeanY += landmarks[index].Y;
            targetMeanX += Reference[index].X;
            targetMeanY += Reference[index].Y;
        }
        sourceMeanX /= 5;
        sourceMeanY /= 5;
        targetMeanX /= 5;
        targetMeanY /= 5;
        float dot = 0, cross = 0, variance = 0;
        for (var index = 0; index < 5; index++)
        {
            var sourceX = landmarks[index].X - sourceMeanX;
            var sourceY = landmarks[index].Y - sourceMeanY;
            var targetX = Reference[index].X - targetMeanX;
            var targetY = Reference[index].Y - targetMeanY;
            dot += sourceX * targetX + sourceY * targetY;
            cross += sourceX * targetY - sourceY * targetX;
            variance += sourceX * sourceX + sourceY * sourceY;
        }
        if (variance < 1e-6f) throw new ArgumentException("Pontos faciais degenerados.", nameof(landmarks));
        var angle = MathF.Atan2(cross, dot);
        var scale = MathF.Sqrt(dot * dot + cross * cross) / variance;
        if (!float.IsFinite(scale) || scale <= 0) throw new ArgumentException("Pontos faciais degenerados.", nameof(landmarks));
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        var offsetX = targetMeanX - scale * (cos * sourceMeanX - sin * sourceMeanY);
        var offsetY = targetMeanY - scale * (sin * sourceMeanX + cos * sourceMeanY);
        return new SimilarityTransform(scale, cos, sin, offsetX, offsetY);
    }

    /// <summary>Aligns and normalizes the face into the SFace input tensor (112×112 RGB, (v-127.5)/128).</summary>
    public static float[] Warp(RgbImage image, SimilarityTransform transform)
    {
        var size = OutputSize;
        var plane = size * size;
        var tensor = new float[3 * plane];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var sourceX = (transform.Cos * (x - transform.OffsetX) + transform.Sin * (y - transform.OffsetY)) /
                              transform.Scale;
                var sourceY = (-transform.Sin * (x - transform.OffsetX) + transform.Cos * (y - transform.OffsetY)) /
                              transform.Scale;
                Sample(image, sourceX, sourceY, out var red, out var green, out var blue);
                var target = y * size + x;
                tensor[target] = (red - 127.5f) / 128f;
                tensor[plane + target] = (green - 127.5f) / 128f;
                tensor[2 * plane + target] = (blue - 127.5f) / 128f;
            }
        }
        return tensor;
    }

    private static void Sample(RgbImage image, float x, float y, out float red, out float green, out float blue)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || x < 0 || y < 0 || x > image.Width - 1 || y > image.Height - 1)
        {
            red = 0;
            green = 0;
            blue = 0;
            return;
        }
        var x0 = (int)x;
        var y0 = (int)y;
        var x1 = Math.Min(image.Width - 1, x0 + 1);
        var y1 = Math.Min(image.Height - 1, y0 + 1);
        var fx = x - x0;
        var fy = y - y0;
        red = Blend(image, x0, y0, x1, y1, fx, fy, 0);
        green = Blend(image, x0, y0, x1, y1, fx, fy, 1);
        blue = Blend(image, x0, y0, x1, y1, fx, fy, 2);
    }

    private static float Blend(RgbImage image, int x0, int y0, int x1, int y1, float fx, float fy, int channel)
    {
        var top = image.Pixels[(y0 * image.Width + x0) * 3 + channel] * (1 - fx) +
                  image.Pixels[(y0 * image.Width + x1) * 3 + channel] * fx;
        var bottom = image.Pixels[(y1 * image.Width + x0) * 3 + channel] * (1 - fx) +
                     image.Pixels[(y1 * image.Width + x1) * 3 + channel] * fx;
        return top * (1 - fy) + bottom * fy;
    }
}

/// <summary>Similarity mapping source points to template points: target = scale · R(angle) · source + offset.</summary>
public sealed record SimilarityTransform(float Scale, float Cos, float Sin, float OffsetX, float OffsetY);

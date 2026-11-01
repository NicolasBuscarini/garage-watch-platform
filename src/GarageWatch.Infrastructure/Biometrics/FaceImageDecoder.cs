using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GarageWatch.Infrastructure.Biometrics;

/// <summary>Decoded RGB image kept only in memory for one enrollment request.</summary>
public sealed record RgbImage(int Width, int Height, byte[] Pixels)
{
    public bool IsValid => Width is > 0 and <= 2048 && Height is > 0 and <= 2048 && Pixels.Length == Width * Height * 3;
}

/// <summary>
/// Decodes JPEG/PNG bytes with the existing FFmpeg boundary into a PPM stream (header carries the real
/// dimensions), so a lying client cannot influence the pixel buffer size.
/// </summary>
public static class FaceImageDecoder
{
    private const int MaximumPixels = 2048 * 2048;

    public static async Task<RgbImage?> DecodeAsync(string ffmpegPath, byte[] encoded, int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-nostdin", "-hide_banner", "-loglevel", "error", "-i", "pipe:0", "-frames:v", "1",
                     "-f", "image2pipe", "-c:v", "ppm", "-"
                 })
            start.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null) return null;
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var stdout = ReadPpmAsync(process.StandardOutput.BaseStream, timeout.Token);
            await process.StandardInput.BaseStream.WriteAsync(encoded, timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var image = await stdout;
            await stderr;
            if (process.ExitCode != 0) return null;
            return image is { IsValid: true } ? image : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            return null;
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            process?.Dispose();
        }
    }

    private static async Task<RgbImage?> ReadPpmAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new List<byte>(64);
        while (header.Count < 64)
        {
            var buffer = new byte[1];
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return null;
            header.Add(buffer[0]);
            var text = Encoding.ASCII.GetString(header.ToArray());
            if (!TryParseHeader(text, out var width, out var height, out var headerLength)) continue;
            if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels) return null;
            var pixels = new byte[width * height * 3];
            var remaining = pixels.Length;
            var offset = 0;
            while (remaining > 0)
            {
                var readBytes = await stream.ReadAsync(pixels.AsMemory(offset, remaining), cancellationToken);
                if (readBytes == 0) return null;
                offset += readBytes;
                remaining -= readBytes;
            }
            _ = headerLength;
            return new RgbImage(width, height, pixels);
        }
        return null;
    }

    internal static bool TryParseHeader(string text, out int width, out int height, out int headerLength)
    {
        width = 0;
        height = 0;
        headerLength = 0;
        if (!text.StartsWith("P6", StringComparison.Ordinal)) return false;
        var index = 2;
        if (!TryReadNumber(text, ref index, out width) || !TryReadNumber(text, ref index, out height)) return false;
        if (!TryReadNumber(text, ref index, out var maximum) || maximum != 255) return false;
        if (index >= text.Length || (text[index] is not ('\n' or ' ' or '\r' or '\t'))) return false;
        headerLength = index + 1;
        return width > 0 && height > 0;
    }

    private static bool TryReadNumber(string text, ref int index, out int value)
    {
        value = 0;
        while (index < text.Length && text[index] is ' ' or '\n' or '\r' or '\t') index++;
        var digits = 0;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            value = value * 10 + (text[index] - '0');
            index++;
            digits++;
            if (digits > 5) return false;
        }
        return digits > 0;
    }
}

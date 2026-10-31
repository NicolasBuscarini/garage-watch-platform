using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Identity;

public sealed record ClipAnalysisDispatchOptions(int QueueCapacity, int LeaseSeconds, string DefaultAnalysisVersion)
{
    public static ClipAnalysisDispatchOptions FromConfiguration(IConfiguration configuration)
    {
        var capacity = configuration.GetValue<int?>("CLIP_ANALYSIS_QUEUE_CAPACITY") ?? 8;
        var leaseSeconds = configuration.GetValue<int?>("CLIP_ANALYSIS_LEASE_SECONDS") ?? 60;
        var version = configuration["CLIP_ANALYSIS_DEFAULT_VERSION"]?.Trim() ?? "analysis-v1";
        if (capacity is < 1 or > 64 || leaseSeconds is < 30 or > 300 || !IsSafeVersion(version))
            throw new InvalidOperationException("A configuração de despacho de análise de clipes é inválida.");
        return new(capacity, leaseSeconds, version);
    }

    public static bool IsSafeVersion(string value) => value.Length is > 0 and <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}

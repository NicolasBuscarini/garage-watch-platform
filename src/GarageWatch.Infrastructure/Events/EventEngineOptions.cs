using System.Text.Json;
using System.Text.Json.Serialization;
using GarageWatch.Application.Events;
using GarageWatch.Infrastructure.Capture;
using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Events;

public sealed record EventEngineOptions(EventRuleSetV1? Rules, string? ConfigurationError, string JournalDirectory,
    int MaximumEvents, long MaximumJournalBytes)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static EventEngineOptions FromConfiguration(IConfiguration configuration, CaptureOptions capture)
    {
        var rulesPath = configuration["EVENT_RULES_FILE"]?.Trim();
        if (string.IsNullOrEmpty(rulesPath)) rulesPath = null;
        EventRuleSetV1? rules = null;
        string? error = null;
        if (rulesPath is not null)
        {
            if (!Path.IsPathFullyQualified(rulesPath)) error = "rules_path_not_absolute";
            else if (!File.Exists(rulesPath)) error = "rules_file_missing";
            else if (new FileInfo(rulesPath).Length > 262_144) error = "rules_file_too_large";
            else
            {
                try
                {
                    rules = JsonSerializer.Deserialize<EventRuleSetV1>(File.ReadAllText(rulesPath), JsonOptions);
                    error = EventRuleSetValidator.Validate(rules);
                    if (error is not null) rules = null;
                }
                catch (JsonException) { error = "rules_file_invalid"; }
                catch (IOException) { error = "rules_file_unavailable"; }
            }
        }

        var maximumEvents = ParseBounded(configuration["EVENT_JOURNAL_MAX_EVENTS"], 10_000, 1, 100_000);
        var maximumBytes = ParseBoundedLong(configuration["EVENT_JOURNAL_MAX_BYTES"], 67_108_864, 1_048_576,
            1_073_741_824);
        return new EventEngineOptions(rules, error, Path.Combine(capture.SpoolDirectory, "events"), maximumEvents,
            maximumBytes);
    }

    internal static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static int ParseBounded(string? raw, int fallback, int minimum, int maximum) =>
        int.TryParse(raw, out var value) && value >= minimum && value <= maximum ? value : fallback;

    private static long ParseBoundedLong(string? raw, long fallback, long minimum, long maximum) =>
        long.TryParse(raw, out var value) && value >= minimum && value <= maximum ? value : fallback;
}

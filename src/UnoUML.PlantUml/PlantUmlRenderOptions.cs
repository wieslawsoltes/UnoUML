using System.Collections.Frozen;
using System.Globalization;

namespace UnoUML.PlantUml;

/// <summary>Render-local capabilities and deterministic inputs for the PlantUML preprocessor.</summary>
public sealed record PlantUmlRenderOptions
{
    private IReadOnlyDictionary<string, string> definitions = FrozenDictionary<string, string>.Empty;

    /// <summary>Named source definitions consumed by !includedef. Values are snapshotted when options are initialized.</summary>
    public IReadOnlyDictionary<string, string> Definitions
    {
        get => definitions;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            definitions = value.ToFrozenDictionary(StringComparer.Ordinal);
        }
    }

    /// <summary>Resolves normalized virtual paths, standard-library identifiers and explicitly supplied URLs.</summary>
    public Func<string, string?>? ResolveInclude { get; init; }

    /// <summary>Optional immutable resource bundle, including libraries imported from ZIP archives.</summary>
    public PlantUmlResourceLibrary? Resources { get; init; }

    /// <summary>Loads source, JSON and artwork from a ZIP/JAR library into the current render's resource scope.</summary>
    public Func<string, PlantUmlResourceLibrary?>? ImportLibrary { get; init; }

    /// <summary>Receives !log and !dump_memory output.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Supplies environment values visible to %getenv. An absent value is an empty string.</summary>
    public Func<string, string?>? ResolveEnvironment { get; init; }

    public string? SourceFileName { get; init; }
    public DateTimeOffset? SourceFileModifiedAt { get; init; }
    public string? Arguments { get; init; }
    public Func<DateTimeOffset> UtcNow { get; init; } = static () => DateTimeOffset.UtcNow;
    public Func<int, int, int> RandomInteger { get; init; } = static (minimum, maximum) => Random.Shared.Next(minimum, maximum);
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;
    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;

    internal string? Resolve(string path) => ResolveInclude?.Invoke(path) ?? Resources?.Resolve(path) ?? BuiltinResources.Resolve(path);
}

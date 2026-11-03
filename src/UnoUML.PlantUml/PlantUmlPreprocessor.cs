using System.Diagnostics.CodeAnalysis;
using System.Collections.Frozen;

namespace UnoUML.PlantUml;

/// <summary>A zero-based original source location for one expanded physical line.</summary>
public sealed record SourceLocation(int SourceLine, string? File, int FileLine, bool Generated);

/// <summary>Expanded PlantUML text and a corresponding immutable map of physical source lines.</summary>
public sealed record PreprocessedSource(string Source, IReadOnlyList<SourceLocation> SourceMap)
{
    /// <summary>Render options supplied by !option directives, captured independently of emitted source text.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = FrozenDictionary<string, string>.Empty;
}

/// <summary>Runs TIM and legacy macros independently of a diagram layout engine.</summary>
public static class PlantUmlPreprocessor
{
    public static bool TryProcess(string source, [NotNullWhen(true)] out PreprocessedSource? result,
        PlantUmlRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        result = null;
        if (source.Length > 1_048_576) return false;
        options ??= new();
        var processor = new Preprocessor(options.Resolve, options);
        if (!processor.Expand(source.TrimStart('\uFEFF').ReplaceLineEndings("\n"), out var expanded)) return false;
        result = new(expanded, processor.GetSourceMap()) { Options = processor.GetOptions() };
        return true;
    }
}

internal sealed partial class Preprocessor
{
    internal IReadOnlyDictionary<string, string> GetOptions() => renderingOptions.ToFrozenDictionary(StringComparer.Ordinal);
    internal IReadOnlyList<SourceLocation> GetSourceMap() => Array.AsReadOnly(origins
        .Select(origin => new SourceLocation(origin.Root, origin.File, origin.Line, origin.Generated)).ToArray());
}

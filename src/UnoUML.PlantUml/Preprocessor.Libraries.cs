using System.Text;

namespace UnoUML.PlantUml;

internal sealed partial class Preprocessor
{
    private readonly HashSet<string[]> nativeLibrarySources = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> userGlobals = new(StringComparer.Ordinal);
    private StringBuilder? libraryProjection;
    private List<Origin>? libraryProjectionOrigins;
    private bool currentNativeLibrary, projectionDisabled;
    private int projectionSuppression;
    private Queue<(string Path, string? Text)>? libraryReads;

    internal string? LibraryProjection => projectionDisabled ? null : libraryProjection?.ToString();

    internal bool TryRenderPureLibrary(string source, out RenderedDiagram? diagram)
    {
        diagram = null;
        if (!PureLibrarySource(source)) return false;
        string? svg = C4Renderer.TryRender(source);
        DiagramKind kind = DiagramKind.C4;
        if (svg == null) { svg = ArchimateRenderer.TryRender(source); kind = DiagramKind.ArchiMate; }
        if (svg == null) return false;
        string library = "";
        foreach (var row in source.AsSpan().EnumerateLines())
        {
            var line = row.Trim();
            if (line.StartsWith("!include ", StringComparison.OrdinalIgnoreCase)) library = ResolvePath(line[8..].ToString(), null);
        }
        // These dependency chains and their default initialization are part of
        // the pinned literal-macro implementations. Every resolved body must
        // match the embedded source before that specialization can be used.
        string[] dependencies = library switch
        {
            "<c4/c4_context>" => [library, "<c4/c4>"],
            "<c4/c4_container>" => [library, "<c4/c4_context>", "<c4/c4>"],
            "<c4/c4_component>" or "<c4/c4_deployment>" => [library, "<c4/c4_container>", "<c4/c4_context>", "<c4/c4>"],
            "<archimate/archimate>" => [library, "<archimate/themes/shared_style>"],
            _ => []
        };
        if (dependencies.Length == 0) return false;
        try
        {
            libraryReads = new();
            foreach (string path in dependencies)
            {
                string? body = resolver?.Invoke(path);
                libraryReads.Enqueue((path, body));
                if (body == null || body != BuiltinResources.NativeLibrary(path)) return false;
            }
            diagram = new(kind, svg);
            return true;
        }
        catch (Exception error) when (IsHandledFailure(error)) { return true; }
    }

    private static bool PureLibrarySource(string source)
    {
        if (source.AsSpan().IndexOfAny('$', '%') >= 0 || source.Contains("/'", StringComparison.Ordinal) ||
            source.Contains("NEW_C4_STYLE", StringComparison.Ordinal) || source.Contains("ROUNDED_STYLE", StringComparison.Ordinal) ||
            source.Contains("NO_LAY_ROTATE", StringComparison.Ordinal)) return false;
        foreach (var row in source.AsSpan().EnumerateLines())
        {
            var line = row.Trim();
            if (line.IsEmpty || line[0] == '\'') continue;
            if (line.StartsWith("title ", StringComparison.OrdinalIgnoreCase) && line.Contains('(')) return false;
            bool quoted = false;
            foreach (char character in line)
            {
                if (character == '"') quoted = !quoted;
                if (quoted && character == '(') return false;
            }
        }
        return true;
    }

    private string? ResolveSource(string path)
    {
        // A custom dependency stops specialization at its first differing body.
        // Consume the already-observed prefix in order as the interpreter reaches
        // it, preserving one host call per include and its original return value.
        if (libraryReads is { Count: > 0 })
        {
            var read = libraryReads.Dequeue();
            if (read.Path != path) throw Unsupported();
            return read.Text;
        }
        return resolver?.Invoke(path);
    }

    private bool BeginLibrary(string key, string? body, string[] lines)
    {
        if (body == null || BuiltinResources.NativeLibrary(key) != body) return false;
        nativeLibrarySources.Add(lines);
        if (!preserveStandardLibraries) return false;
        if (projectionSuppression == 0 && IsDirectLibrary(key) && !projectionDisabled)
        {
            libraryProjection ??= new(output.ToString());
            libraryProjectionOrigins ??= [.. origins];
            if (key.EndsWith(".puml>", StringComparison.Ordinal)) key = key[..^6] + ">";
            AppendProjection("!include " + key, currentOrigin);
        }
        if (projectionSuppression == 0 && !IsDirectLibrary(key)) return false;
        projectionSuppression++;
        return true;
    }

    private void DisableProjection()
    {
        projectionDisabled = true;
        libraryProjection = null;
        libraryProjectionOrigins = null;
    }

    private void AppendProjection(string line, Origin origin)
    {
        if (libraryProjection == null || libraryProjectionOrigins == null) return;
        if (line.Length > MaxLine || libraryProjection.Length + line.Length + 1 > MaxBytes)
        {
            DisableProjection();
            return;
        }
        foreach (var physical in line.AsSpan().EnumerateLines())
        {
            if (libraryProjectionOrigins.Count >= 32000) { DisableProjection(); return; }
            if (libraryProjectionOrigins.Count > 0) libraryProjection.Append('\n');
            libraryProjection.Append(physical);
            libraryProjectionOrigins.Add(origin);
        }
    }

    private bool ProjectProcedure(Routine routine, CallArguments args, string prefix, string suffix, Origin origin)
    {
        if (projectionDisabled || libraryProjection == null || projectionSuppression != 0 || !nativeLibrarySources.Contains(routine.Lines)) return false;
        if (!string.IsNullOrWhiteSpace(prefix) || suffix.Trim() is not ("" or "{"))
        {
            DisableProjection();
            return false;
        }
        int identifiers = routine.Name.StartsWith("Rel", StringComparison.Ordinal) || routine.Name.StartsWith("BiRel", StringComparison.Ordinal) ||
            routine.Name.StartsWith("Lay", StringComparison.Ordinal) ? 2 : 1;
        var arguments = new List<string>();
        int positional = 0, supplied = 0;
        for (int index = 0; index < routine.Parameters.Length; index++)
        {
            var parameter = routine.Parameters[index];
            bool explicitValue = args.Named.TryGetValue(parameter.Name, out var value);
            if (!explicitValue && positional < args.Positional.Count) { explicitValue = true; value = args.Positional[positional++]; }
            if (!explicitValue) value = parameter.Default ?? throw Unsupported();
            if (explicitValue) supplied = index + 1;
            if (index < identifiers)
            {
                if (!LibraryMacro.Alias(value.Scalar)) { DisableProjection(); return false; }
                arguments.Add(value.Scalar);
            }
            else
            {
                if (value.Scalar.IndexOfAny(['"', '\r', '\n']) >= 0) { DisableProjection(); return false; }
                arguments.Add('"' + value.Scalar + '"');
            }
        }
        AppendProjection(prefix + routine.Name + "(" + string.Join(',', arguments.Take(supplied)) + ")" + suffix,
            origin with { Generated = origin.Generated && !origin.LiteralContinuation || !args.Literal });
        return !projectionDisabled;
    }
}

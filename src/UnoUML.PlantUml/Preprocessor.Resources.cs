using System.Globalization;
using System.Text.Json;

namespace UnoUML.PlantUml;

internal sealed partial class Preprocessor
{
    private sealed record Subpart(string[] Lines, int Begin, int End, string? File);
    private readonly List<PlantUmlResourceLibrary> imports = [];
    private readonly Dictionary<string, PlantUmlResourceLibrary?> assetArchives = new(StringComparer.Ordinal);
    private readonly Dictionary<string[], string?> resourceParents = new(ReferenceEqualityComparer.Instance);
    private Value? currentTheme;

    private static bool IsDirectLibrary(string requested)
    {
        string name = requested.ToLowerInvariant();
        return name is "<c4/c4>" or "<c4/c4_context>" or "<c4/c4_container>" or "<c4/c4_component>" or
            "<c4/c4_deployment>" or "<archimate/archimate>" or "<archimate/archimate.puml>";
    }

    private static string ResolvePath(string requested, string? parent)
    {
        string path = requested.Trim();
        parent = parent?.Replace('\\', '/');
        if (path.Length > 1 && path[0] is '\'' or '"' && path[^1] == path[0]) path = path[1..^1];
        path = path.Replace('\\', '/');
        if (path.Length == 0 || path.Length > MaxLine || path.Any(char.IsControl)) throw Unsupported();
        if (path.StartsWith("jar:", StringComparison.OrdinalIgnoreCase)) return path;
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") return absolute.AbsoluteUri;
        bool standard = path[0] == '<' && path[^1] == '>';
        if (standard) { path = path[1..^1]; parent = null; }
        else if (parent != null && Uri.TryCreate(parent, UriKind.Absolute, out var parentUri) && parentUri.Scheme is "http" or "https")
            return new Uri(parentUri, path).AbsoluteUri;
        string prefix = "";
        if (path.StartsWith('/')) { prefix = "/"; path = path.TrimStart('/'); parent = null; }
        else if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/')
        { prefix = path[..3]; path = path[3..]; parent = null; }
        if (path.IndexOfAny([':', '<', '>']) >= 0) throw Unsupported();
        var parts = new List<string>();
        if (parent != null)
        {
            bool parentStandard = parent.StartsWith('<') && parent.EndsWith('>');
            if (parentStandard) { parent = parent[1..^1]; standard = true; }
            if (parent.StartsWith('/')) { prefix = "/"; parent = parent.TrimStart('/'); }
            else if (parent.Length >= 3 && char.IsAsciiLetter(parent[0]) && parent[1] == ':' && parent[2] == '/')
            { prefix = parent[..3]; parent = parent[3..]; }
            int slash = parent.LastIndexOf('/');
            if (slash >= 0) parts.AddRange(parent[..slash].Split('/'));
        }
        foreach (string part in path.Split('/'))
        {
            if (part == ".") continue;
            if (part.Length == 0) throw Unsupported();
            if (part == "..") { if (parts.Count == 0) throw Unsupported(); parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        if (parts.Count == 0) throw Unsupported();
        string key = string.Join('/', parts);
        return standard ? "<" + key.ToLowerInvariant() + ">" : prefix + key;
    }

    private string? ResolveResource(string requested) => ResolveRequest(requested, CurrentFile).Text;

    internal string? ResolveAsset(string requested, int renderedLine)
    {
        string? file = (uint)renderedLine < (uint)origins.Count ? origins[renderedLine].ResourceFile ?? origins[renderedLine].File : null;
        file ??= options?.SourceFileName;
        try
        {
            int separator = requested.IndexOf('~');
            if (separator < 0 || options?.ImportLibrary == null) return ResolveRequest(requested, file).Text;
            if (ResolvePath(requested, file).StartsWith("jar:", StringComparison.OrdinalIgnoreCase))
                return ResolveRequest(requested, file).Text;
            string archive = ResolvePath(requested[..separator], file);
            string entry = ResolvePath(requested[(separator + 1)..], null);
            if (entry.StartsWith('/') || entry.Contains(':') || entry.StartsWith('<')) return null;
            if (!assetArchives.TryGetValue(archive, out var library))
            {
                if (assetArchives.Count >= 128) return null;
                assetArchives[archive] = library = options.ImportLibrary(archive);
            }
            return library?.Resolve(entry);
        }
        catch (Unhandled) { return null; }
        catch (ArgumentException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    private (string Key, string? Text) ResolveRequest(string requested, string? parent)
    {
        string key = ResolvePath(requested, parent);
        string? archivePath = null;
        if (imports.Count > 0 && parent != null)
        {
            try { archivePath = ResolvePath(requested, null); }
            catch (Unhandled) { }
        }
        for (int i = imports.Count - 1; i >= 0; i--)
        {
            if (imports[i].Resolve(key) is { } imported) return (key, imported);
            if (archivePath != null && imports[i].Resolve(archivePath) is { } relative) return (archivePath, relative);
        }
        return (key, resolver?.Invoke(key));
    }

    private string[] ReadLines(string? body)
    {
        if (body == null) throw Unsupported();
        if (body.Length > MaxBytes || (sourceChars += body.Length) > MaxBytes) throw Unsupported();
        return PrepareLines(body);
    }

    private static (int Begin, int End) DiagramRange(string[] lines, string? selector)
    {
        int index = 0;
        bool foundDiagram = false;
        for (int start = 0; start < lines.Length; start++)
        {
            string header = lines[start].Trim();
            if (!header.StartsWith("@start", StringComparison.OrdinalIgnoreCase)) continue;
            foundDiagram = true;
            int close = Array.FindIndex(lines, start + 1, l => l.TrimStart().StartsWith("@end", StringComparison.OrdinalIgnoreCase));
            if (close < 0) throw Unsupported();
            bool selected = selector == null && index == 0 || selector == index.ToString(CultureInfo.InvariantCulture);
            int idStart = header.IndexOf("(id=", StringComparison.Ordinal);
            if (idStart >= 0 && header.EndsWith(')')) selected |= selector == header[(idStart + 4)..^1];
            if (selected) return (start + 1, close);
            index++;
            start = close;
        }
        if (foundDiagram || selector != null) throw Unsupported();
        return (0, lines.Length);
    }

    private void Include(string requested, string mode, string? parent, int rootLine, Dictionary<string, Value> locals, int depth)
    {
        if (depth > 64) throw Unsupported();
        requested = requested.Trim();
        if (requested.Length > 1 && requested[0] is '\'' or '"' && requested[^1] == requested[0]) requested = requested[1..^1];
        string? selector = null;
        int bang = requested.LastIndexOf('!');
        if (bang >= 0) { selector = requested[(bang + 1)..]; requested = requested[..bang]; if (selector.Length == 0) throw Unsupported(); }
        var (key, body) = ResolveRequest(requested, parent);
        string identity = key + (selector == null ? "" : "!" + selector);
        if (visiting.Contains(identity) || included.Count >= 512) throw Unsupported();
        if (!included.Add(identity) && mode != "!include_many") { if (mode == "!include_once") throw Unsupported(); return; }
        string[] lines = ReadLines(body);
        var (begin, end) = DiagramRange(lines, selector);
        visiting.Add(identity);
        try { Run(lines, begin, end, key, rootLine, locals, depth); }
        finally { visiting.Remove(identity); }
    }

    private void IncludeSub(string requested, int rootLine, Dictionary<string, Value> locals, int depth)
    {
        int bang = requested.LastIndexOf('!');
        List<Subpart> selected;
        if (bang < 0)
        {
            if (!subparts.TryGetValue(requested.Trim(), out selected!)) throw Unsupported();
        }
        else
        {
            var (path, body) = ResolveRequest(requested[..bang], CurrentFile);
            string name = requested[(bang + 1)..].Trim();
            string[] lines = ReadLines(body);
            selected = [];
            for (int i = 0; i < lines.Length; i++)
            {
                string text = lines[i].Trim();
                if (!text.StartsWith("!startsub ", StringComparison.Ordinal)) continue;
                int stop = Match(lines, i + 1, lines.Length, "!startsub", "!endsub");
                if (Substitute(text[10..].Trim(), locals, depth) == name) selected.Add(new(lines, i + 1, stop, path));
                i = stop;
            }
            if (selected.Count == 0) throw Unsupported();
        }
        foreach (var subpart in selected) Run(subpart.Lines, subpart.Begin, subpart.End, subpart.File, rootLine, locals, depth);
    }

    private void ImportResource(string requested)
    {
        if (options?.ImportLibrary == null || imports.Count >= 128) throw Unsupported();
        string key = ResolvePath(requested, CurrentFile);
        imports.Add(options.ImportLibrary(key) ?? throw Unsupported());
    }

    private void IncludeDefinition(string name, int rootLine, Dictionary<string, Value> locals, int depth)
    {
        if (depth > 64 || options == null || !options.Definitions.TryGetValue(name, out string? source)) throw Unsupported();
        string[] lines = ReadLines(source);
        resourceParents.Add(lines, CurrentFile);
        Run(lines, 0, lines.Length, "<definition/" + name + ">", rootLine, locals, depth);
    }

    private void IncludeTheme(string declaration, int rootLine, Dictionary<string, Value> locals, int depth)
    {
        int from = declaration.IndexOf(" from ", StringComparison.OrdinalIgnoreCase);
        string name = (from < 0 ? declaration : declaration[..from]).Trim();
        if (name.Length == 0 || name.Any(c => !char.IsLetterOrDigit(c) && c is not ('_' or '-'))) throw Unsupported();
        string requested = "<theme/" + name + ">";
        if (from >= 0)
        {
            string location = declaration[(from + 6)..].Trim();
            requested = location.StartsWith('<') && location.EndsWith('>') ?
                location[..^1].TrimEnd('/') + "/puml-theme-" + name + ".puml>" : location.TrimEnd('/') + "/puml-theme-" + name + ".puml";
        }
        var (key, body) = ResolveRequest(requested, CurrentFile);
        if (body == null && from < 0) (key, body) = ResolveRequest("puml-theme-" + name + ".puml", CurrentFile);
        string[] lines = ReadLines(body);
        resourceParents.Add(lines, CurrentFile);
        Value? themeMetadata = null;
        int begin = 0;
        if (lines.Length > 0 && lines[0] == "---")
        {
            int close = Array.FindIndex(lines, 1, line => line == "---");
            if (close < 0) close = lines.Length;
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < close; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0) entries[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes))
            {
                writer.WriteStartObject();
                foreach (var entry in entries) writer.WriteString(entry.Key, entry.Value);
                writer.WriteEndObject();
            }
            using var metadata = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)));
            themeMetadata = Value.Json(metadata.RootElement);
            begin = close + 1;
        }
        if (!visiting.Add(key)) throw Unsupported();
        try { Run(lines, begin, lines.Length, key, rootLine, locals, depth); }
        finally { visiting.Remove(key); currentTheme = themeMetadata; }
    }

    private Value GetCurrentTheme()
    {
        if (currentTheme is { } theme) return theme;
        using var empty = JsonDocument.Parse("{}");
        return Value.Json(empty.RootElement);
    }
}

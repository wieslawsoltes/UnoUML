using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UnoUML.Syntax;

/// <summary>One semantic row, including directories inferred from descendant paths.</summary>
internal sealed class FilesSourceNode(string path, string name, bool directory, int parent, int depth,
    string identity, SourceRow origin, SourceToken nameToken)
{
    internal readonly string Path = path, Name = name, Identity = identity;
    internal readonly bool Directory = directory;
    internal readonly int Parent = parent, Depth = depth;
    internal SourceRow Origin = origin;
    internal SourceToken NameToken = nameToken;
    internal int Declaration = -1;
}

internal sealed record FilesSourceDeclaration(string Path, int Node, SourceRow Row, SourceToken Token);

/// <summary>
/// A bounded, lossless index of literal files paths. It is shared by the editor
/// and native painter so inferred directories and their identities agree.
/// Unknown statements are rejected before any structural mutation is offered.
/// </summary>
internal sealed class FilesSource
{
    internal readonly List<FilesSourceNode> Nodes = [];
    internal readonly List<FilesSourceDeclaration> Declarations = [];
    internal readonly Dictionary<string, int> ByPath = new(StringComparer.Ordinal);
    internal SourceRow Header = null!, End = null!;
    internal const int MaxNodes = 4096, MaxDepth = 128, MaxPathLength = 2048;

    internal static FilesSource? Parse(string source)
    {
        var rows = StructuredText.Rows(source);
        if (rows == null) return null;
        var result = new FilesSource();
        // A slash suffix distinguishes a directory declaration. A file and a
        // directory may not claim the same literal path in an editable tree.
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        bool started = false, ended = false;
        foreach (var row in rows)
        {
            string text = row.Trimmed;
            if (text.Length == 0) continue;
            if (!started)
            {
                if (!StructuredText.Header(text, "files")) return null;
                result.Header = row; started = true; continue;
            }
            if (ended) return null;
            if (text.Equals("@endfiles", StringComparison.OrdinalIgnoreCase)) { result.End = row; ended = true; continue; }
            if (!TryPath(text, out var parts, out bool directory)) return null;
            int at = row.ContentStart;
            // Masked comments may follow a path, but cannot split its literal
            // spelling. Token offsets always address the untouched source.
            if (!source.AsSpan(at, text.Length).SequenceEqual(text)) return null;
            int parent = -1, position = at + 1;
            string prefix = "/", parentId = "";
            for (int p = 0; p < parts.Length; p++)
            {
                string name = parts[p], key = prefix + name;
                bool last = p == parts.Length - 1, isDirectory = !last || directory;
                string path = key + (isDirectory ? "/" : "");
                var nameToken = new SourceToken(position, name.Length);
                if (!slots.TryGetValue(key, out int index))
                {
                    if (result.Nodes.Count >= MaxNodes) return null;
                    index = result.Nodes.Count;
                    slots.Add(key, index); result.ByPath.Add(path, index);
                    result.Nodes.Add(new(path, name, isDirectory, parent, p + 1, Identity(parentId, name, isDirectory), row, nameToken));
                }
                var node = result.Nodes[index];
                if (node.Directory != isDirectory) return null;
                if (last)
                {
                    if (node.Declaration >= 0) return null;
                    node.Declaration = result.Declarations.Count;
                    node.Origin = row; node.NameToken = nameToken;
                    result.Declarations.Add(new(text, index, row, new(at, text.Length)));
                }
                parent = index; prefix = path; parentId = node.Identity; position += name.Length + 1;
            }
        }
        return ended ? result : null;
    }

    internal static bool TryPath(string path, out string[] parts, out bool directory)
    {
        parts = []; directory = false;
        if (path.Length is < 2 or > MaxPathLength || path[0] != '/' || path != path.Trim() || path.Contains("//", StringComparison.Ordinal)) return false;
        directory = path.EndsWith('/');
        parts = (directory ? path[1..^1] : path[1..]).Split('/');
        return parts.Length is > 0 and <= MaxDepth && parts.All(LiteralName);
    }

    internal static bool LiteralName(string name) => name.Length > 0 && name is not ("." or "..") &&
        StructuredText.Plain(name, MaxPathLength) && StructuredText.Unquoted(name) && name.IndexOfAny(['/', '\\', '%', '$', '\uFEFF']) < 0;

    // Keep exactly the original native Files identity contract. Components are
    // length-prefixed UTF-16 strings; hashing is culture and line-order invariant.
    private static string Identity(string parent, string name, bool directory)
    {
        var input = new StringBuilder();
        foreach (string component in new[] { parent, name, directory ? "directory" : "file" })
            input.Append(component.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(component);
        return "files:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString())).AsSpan(0, 16));
    }
}

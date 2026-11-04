using UnoUML.Syntax;

namespace UnoUML.PlantUml;

internal static partial class TreeDiagram
{
    private static string? TryRenderFiles(string source)
    {
        if (!StyleSheet.TryExtract(source, out source, out var styles) || FilesSource.Parse(source) is not { } files) return null;
        var skin = new ManagedSkin { Sheet = styles, Diagram = "filesdiagram" };
        string? skinBlock = null;
        int line = -1;
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            line++;
            var text = raw.Trim();
            if (text.IsEmpty || text.StartsWith("'")) continue;
            if (skinBlock != null || text.StartsWith("skinparam ", StringComparison.OrdinalIgnoreCase))
                if (!skin.Read(text.ToString(), ref skinBlock, false, line)) return null;
        }
        if (skinBlock != null || files.Nodes.Any(node => !SvgText.IsPlain(node.Name))) return null;
        var nodes = files.Nodes.Select(node => new Node(node.Name, node.Parent, node.Depth, node.Declaration < 0 ? -1 : node.Origin.Index)
        {
            Directory = node.Directory, File = node,
            // Large native listings remain renderable without opting into a
            // canvas graph larger than the geometry engine's node budget.
            CanvasId = files.Nodes.Count <= 2048 ? node.Identity : ""
        }).ToList();
        MeasureStyles(nodes, skin, true);
        return DrawFiles(nodes, skin);
    }
}

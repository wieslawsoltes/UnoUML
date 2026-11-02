using System.Diagnostics.CodeAnalysis;
using System.Xml;

namespace UnoUML.PlantUml;

public enum DiagramKind { Sequence, Graph, State, Activity, Chart, MindMap, WorkBreakdown, Files, Json, Yaml, Gantt, Timing, Packet }

public sealed record RenderedDiagram(DiagramKind Kind, string Svg);

/// <summary>Renders one complete PlantUML block without browser, Java or Graphviz dependencies.</summary>
public static class PlantUmlRenderer
{
    /// <summary>Returns false when the input requires another PlantUML implementation or exceeds the managed layout limits.</summary>
    public static bool TryRender(string source, [NotNullWhen(true)] out RenderedDiagram? diagram)
    {
        ArgumentNullException.ThrowIfNull(source);
        diagram = null;
        if (source.Length is 0 or > 1_048_576) return false;
        var header = source.AsSpan().TrimStart();
        int end = header.IndexOfAny('\r', '\n');
        if (end < 0) return false;
        var title = header[..end].TrimEnd();
        int sep = title.IndexOfAny(' ', '\t');
        var dialect = sep < 0 ? title : title[..sep];
        DiagramKind kind = dialect switch
        {
            "@startuml" => DiagramKind.Sequence,
            "@startmindmap" => DiagramKind.MindMap,
            "@startwbs" => DiagramKind.WorkBreakdown,
            "@startfiles" => DiagramKind.Files,
            "@startchart" => DiagramKind.Chart,
            "@startpacketdiag" => DiagramKind.Packet,
            "@startgantt" => DiagramKind.Gantt,
            "@startyaml" => DiagramKind.Yaml,
            "@startjson" => DiagramKind.Json,
            _ => (DiagramKind)(-1)
        };
        if ((int)kind < 0) return false;
        // Do not render a prefix of a file or silently discard an output name or option.
        if (sep >= 0) return false;
        try { XmlConvert.VerifyXmlChars(source); }
        catch (XmlException) { return false; }
        if (source.Contains('\r')) source = source.ReplaceLineEndings("\n");
        string? svg;
        if (kind == DiagramKind.Sequence)
        {
            svg = SequenceRenderer.TryRender(source);
            if (svg == null)
            {
                svg = GraphRenderer.TryRender(source, out var state);
                kind = state ? DiagramKind.State : DiagramKind.Graph;
            }
            if (svg == null) { kind = DiagramKind.Activity; svg = ActivityRenderer.TryRender(source); }
            if (svg == null) { kind = DiagramKind.Timing; svg = TimingRenderer.TryRender(source); }
        }
        else if (kind == DiagramKind.Chart) svg = ChartRenderer.TryRender(source);
        else if (kind == DiagramKind.Packet) svg = PacketRenderer.TryRender(source);
        else if (kind == DiagramKind.Gantt) svg = GanttRenderer.TryRender(source);
        else if (kind == DiagramKind.Json) svg = DataDiagram.RenderJson(source);
        else if (kind == DiagramKind.Yaml) svg = DataDiagram.RenderYaml(source);
        else svg = TreeDiagram.TryRender(source, kind);
        if (svg == null) return false;
        diagram = new(kind, svg);
        return true;
    }
}

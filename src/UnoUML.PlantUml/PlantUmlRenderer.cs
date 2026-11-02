using System.Diagnostics.CodeAnalysis;
using System.Xml;

namespace UnoUML.PlantUml;

public enum DiagramKind { Sequence, Graph, State, Activity, Chart, MindMap, WorkBreakdown, Files, Json, Yaml, Gantt, Timing, Packet, Network, Salt, Creole, Dot, Ebnf, Regex, C4, ArchiMate }

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
        bool bom = source[0] == '\uFEFF';
        var header = source.AsSpan(bom ? 1 : 0).TrimStart();
        int end = header.IndexOfAny('\r', '\n');
        if (end < 0) return false;
        var dialect = header[..end].TrimEnd();
        DiagramKind? selectedKind = dialect switch
        {
            "@startuml" => DiagramKind.Sequence,
            "@startmindmap" => DiagramKind.MindMap,
            "@startwbs" => DiagramKind.WorkBreakdown,
            "@startfiles" => DiagramKind.Files,
            "@startchart" => DiagramKind.Chart,
            "@startebnf" => DiagramKind.Ebnf,
            "@startregex" => DiagramKind.Regex,
            "@startdot" => DiagramKind.Dot,
            "@startcreole" => DiagramKind.Creole,
            "@startsalt" => DiagramKind.Salt,
            "@startnwdiag" => DiagramKind.Network,
            "@startpacketdiag" => DiagramKind.Packet,
            "@startgantt" => DiagramKind.Gantt,
            "@startyaml" => DiagramKind.Yaml,
            "@startjson" => DiagramKind.Json,
            _ => null
        };
        if (selectedKind is not { } kind) return false;
        // Rendering and source hit-testing use the same CR/LF line numbering.
        if (source.AsSpan().IndexOfAny('\u0085', '\u2028', '\u2029') >= 0) return false;
        try { XmlConvert.VerifyXmlChars(source); }
        catch (XmlException) { return false; }
        if (kind != DiagramKind.Json)
        {
            if (bom) source = source[1..];
            if (source.Contains('\r')) source = source.ReplaceLineEndings("\n");
        }
        string? svg = kind switch
        {
            DiagramKind.Sequence => RenderUml(source, out kind),
            DiagramKind.Dot => GraphRenderer.TryRenderDot(source),
            DiagramKind.Regex or DiagramKind.Ebnf => RailroadRenderer.TryRender(source, kind == DiagramKind.Regex),
            DiagramKind.Creole => CreoleRenderer.TryRender(source),
            DiagramKind.Salt => SaltRenderer.TryRender(source),
            DiagramKind.Chart => ChartRenderer.TryRender(source),
            DiagramKind.Network => NetworkRenderer.TryRender(source),
            DiagramKind.Packet => PacketRenderer.TryRender(source),
            DiagramKind.Gantt => GanttRenderer.TryRender(source),
            DiagramKind.Json => DataDiagram.RenderJson(source),
            DiagramKind.Yaml => DataDiagram.RenderYaml(source),
            _ => TreeDiagram.TryRender(source, kind)
        };
        if (svg == null) return false;
        diagram = new(kind, svg);
        return true;
    }

    private static string? RenderUml(string source, out DiagramKind kind)
    {
        // Recognize complete, unmodified standard-library contracts before the underlying UML grammars.
        if (source.Contains("!include", StringComparison.OrdinalIgnoreCase))
        {
            kind = DiagramKind.C4;
            string? macro = C4Renderer.TryRender(source);
            if (macro != null) return macro;
            kind = DiagramKind.ArchiMate;
            return ArchimateRenderer.TryRender(source);
        }
        kind = DiagramKind.Sequence;
        string? svg = SequenceRenderer.TryRender(source);
        if (svg != null) return svg;
        svg = GraphRenderer.TryRender(source, out var state);
        if (svg != null) { kind = state ? DiagramKind.State : DiagramKind.Graph; return svg; }
        kind = DiagramKind.Activity;
        svg = ActivityRenderer.TryRender(source);
        if (svg != null) return svg;
        kind = DiagramKind.Timing;
        return TimingRenderer.TryRender(source);
    }
}

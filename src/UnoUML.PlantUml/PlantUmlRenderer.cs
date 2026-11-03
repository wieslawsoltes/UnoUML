using System.Diagnostics.CodeAnalysis;
using System.Xml;

namespace UnoUML.PlantUml;

public enum DiagramKind { Sequence, Graph, State, Activity, Chart, MindMap, WorkBreakdown, Files, Json, Yaml, Gantt, Timing, Packet, Network, Salt, Creole, Dot, Ebnf, Regex, C4, ArchiMate, Chen, Ditaa, Math, LaTeX }

public sealed record RenderedDiagram(DiagramKind Kind, string Svg);

/// <summary>Renders one complete PlantUML block without browser, Java or Graphviz dependencies.</summary>
public static class PlantUmlRenderer
{
    /// <summary>Returns false when the input requires another PlantUML implementation or exceeds the managed layout limits.</summary>
    public static bool TryRender(string source, [NotNullWhen(true)] out RenderedDiagram? diagram) =>
        TryRender(source, null, out diagram);

    /// <summary>Renders with a host-supplied resolver for local includes. Paths are normalized and relative to the virtual root.</summary>
    public static bool TryRender(string source, Func<string,string?>? resolveInclude, [NotNullWhen(true)] out RenderedDiagram? diagram)
    {
        diagram = null;
        if (!TryPrepare(source, resolveInclude, out source, out var kind, out var preprocessor)) return false;
        string? svg = kind switch
        {
            DiagramKind.Chen => ChenRenderer.TryRender(source),
            DiagramKind.Ditaa => DitaaRenderer.TryRender(source),
            DiagramKind.Math or DiagramKind.LaTeX => MathRenderer.TryRender(source, kind == DiagramKind.LaTeX),
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
        diagram = new(kind, preprocessor == null ? svg : preprocessor.Remap(svg));
        return true;
    }

    // The combined and individual-sheet APIs share normalization, limits and TIM source mapping.
    internal static bool TryPrepare(string input, Func<string,string?>? resolveInclude,
        out string source, out DiagramKind kind, out Preprocessor? preprocessor)
    {
        source = input;
        ArgumentNullException.ThrowIfNull(source);
        kind = default;
        preprocessor = null;
        if (source.Length is 0 or > 1_048_576) return false;
        bool bom = source[0] == '\uFEFF';
        var header = source.AsSpan(bom ? 1 : 0).TrimStart();
        int end = header.IndexOfAny('\r', '\n');
        if (end < 0) return false;
        var dialect = header[..end].TrimEnd();
        int nameAt = dialect.IndexOfAny(' ', '\t');
        if (nameAt >= 0)
        {
            var name = dialect[nameAt..].Trim();
            // A filename is output metadata. Replacing it with spaces keeps every
            // UTF-16 token offset valid, including offsets in JSON field editing.
            if (name.IsEmpty || !(char.IsLetterOrDigit(name[0]) || name[0] == '_') ||
                name.ContainsAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-/")) return false;
            int prefix = source.Length - header.Length + nameAt;
            source = string.Concat(source.AsSpan(0,prefix),new string(' ',dialect.Length-nameAt),source.AsSpan(prefix+dialect.Length-nameAt));
            dialect = dialect[..nameAt];
        }
        DiagramKind? selectedKind = dialect switch
        {
            "@startuml" => DiagramKind.Sequence,
            "@startchen" => DiagramKind.Chen,
            "@startditaa" => DiagramKind.Ditaa,
            "@startmath" => DiagramKind.Math,
            "@startlatex" => DiagramKind.LaTeX,
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
        if (selectedKind is not { } selected) return false;
        kind = selected;
        // Rendering and source hit-testing use the same CR/LF line numbering.
        if (source.AsSpan().IndexOfAny('\u0085', '\u2028', '\u2029') >= 0) return false;
        try { XmlConvert.VerifyXmlChars(source); }
        catch (XmlException) { return false; }
        if (kind != DiagramKind.Json)
        {
            if (bom) source = source[1..];
            if (source.Contains('\r')) source = source.ReplaceLineEndings("\n");
        }
        if (kind is not (DiagramKind.Json or DiagramKind.Math or DiagramKind.LaTeX or DiagramKind.Ditaa) && Preprocessor.Needed(source))
        {
            preprocessor = new Preprocessor(resolveInclude);
            if (!preprocessor.Expand(source,out source)) return false;
            try { XmlConvert.VerifyXmlChars(source); }
            catch (XmlException) { return false; }
        }
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

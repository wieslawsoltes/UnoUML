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
        => TryRender(source, out diagram, new PlantUmlRenderOptions { ResolveInclude = resolveInclude });

    /// <summary>Renders using explicit resource, environment and clock capabilities. Options are isolated to this invocation.</summary>
    public static bool TryRender(string source, [NotNullWhen(true)] out RenderedDiagram? diagram, PlantUmlRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        return TryRenderPrepared(source, options, out diagram);
    }

    private static bool TryRenderPrepared(string source, PlantUmlRenderOptions options,
        [NotNullWhen(true)] out RenderedDiagram? diagram)
    {
        diagram = null;
        if (!TryPrepare(source, options, out source, out var kind, out var preprocessor, deferPreprocessing: true)) return false;
        if (preprocessor != null)
        {
            if (kind == DiagramKind.Sequence && preprocessor.TryRenderPureLibrary(source, out diagram)) return diagram != null;
            if (!preprocessor.Expand(source, out source)) return false;
            try { XmlConvert.VerifyXmlChars(source); }
            catch (XmlException) { return false; }
        }
        if (kind == DiagramKind.Sequence && preprocessor?.LibraryProjection is { } projected)
        {
            string? direct = RenderStyled(projected, DiagramKind.C4, C4Renderer.TryRender);
            DiagramKind directKind = DiagramKind.C4;
            if (direct == null)
            {
                direct = RenderStyled(projected, DiagramKind.ArchiMate, ArchimateRenderer.TryRender);
                directKind = DiagramKind.ArchiMate;
            }
            if (direct != null)
            {
                diagram = new(directKind, preprocessor.Remap(direct, projectedLibrary: true));
                return true;
            }
        }
        Preprocessor? resourceResolver = preprocessor;
        string? ResolveAsset(string path, int line) => (resourceResolver ??= new(options.Resolve, options)).ResolveAsset(path, line);
        if (!SpriteCatalog.TryExtract(source, ResolveAsset, out source, out var sprites,
            declarations: kind is not (DiagramKind.Ditaa or DiagramKind.Math or DiagramKind.LaTeX or DiagramKind.Dot))) return false;
        using var spriteScope = sprites.BeginScope();
        var renderOptions = preprocessor?.GetOptions();
        string? svg = kind switch
        {
            DiagramKind.Chen => RenderStyled(source, kind, ChenRenderer.TryRender),
            DiagramKind.Ditaa => DitaaRenderer.TryRender(source),
            DiagramKind.Math or DiagramKind.LaTeX => MathRenderer.TryRender(source, kind == DiagramKind.LaTeX),
            DiagramKind.Sequence => RenderUml(source, out kind, renderOptions),
            DiagramKind.Dot => GraphRenderer.TryRenderDot(source),
            DiagramKind.Regex => RenderStyled(source, kind, text => RailroadRenderer.TryRender(text, true, renderOptions)),
            DiagramKind.Ebnf => RenderStyled(source, kind, text => RailroadRenderer.TryRender(text, false, renderOptions)),
            DiagramKind.Creole => CreoleRenderer.TryRender(source),
            DiagramKind.Salt => RenderStyled(source, kind, SaltRenderer.TryRender),
            DiagramKind.Chart => RenderStyled(source, kind, ChartRenderer.TryRender),
            DiagramKind.Network => RenderStyled(source, kind, NetworkRenderer.TryRender),
            DiagramKind.Packet => RenderStyled(source, kind, PacketRenderer.TryRender),
            DiagramKind.Gantt => RenderStyled(source, kind, GanttRenderer.TryRender),
            DiagramKind.Json => RenderStyled(source, kind, DataDiagram.RenderJson),
            DiagramKind.Yaml => RenderStyled(source, kind, DataDiagram.RenderYaml),
            _ => TreeDiagram.TryRender(source, kind)
        };
        if (svg == null) return false;
        diagram = new(kind, preprocessor == null ? svg : preprocessor.Remap(svg));
        return true;
    }

    private static string? RenderStyled(string source, DiagramKind kind, Func<string, string?> render)
    {
        if (!source.Contains("skinparam", StringComparison.OrdinalIgnoreCase) && !source.Contains("<style", StringComparison.OrdinalIgnoreCase)) return render(source);
        if (!StyleDocument.TryPrepare(source, kind, out var cleaned, out var styles)) return null;
        using var styleScope = styles.Begin();
        var svg = render(cleaned);
        return svg == null ? null : styles.Apply(svg);
    }

    // The combined and individual-sheet APIs share normalization, limits and TIM source mapping.
    internal static bool TryPrepare(string input, Func<string,string?>? resolveInclude,
        out string source, out DiagramKind kind, out Preprocessor? preprocessor)
        => TryPrepare(input, new PlantUmlRenderOptions { ResolveInclude = resolveInclude }, out source, out kind, out preprocessor);

    internal static bool TryPrepare(string input, PlantUmlRenderOptions options,
        out string source, out DiagramKind kind, out Preprocessor? preprocessor, bool preserveLibraries = true, bool deferPreprocessing = false)
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
        // Mask a BOM without shifting any absolute UTF-16 source offsets.
        if (bom) source = " " + source[1..];
        if (Preprocessor.Needed(source))
        {
            preprocessor = new Preprocessor(options.Resolve, options, preserveLibraries);
            if (deferPreprocessing) return true;
            if (!preprocessor.Expand(source,out source)) return false;
            try { XmlConvert.VerifyXmlChars(source); }
            catch (XmlException) { return false; }
        }
        return true;
    }

    private static string? RenderUml(string source, out DiagramKind kind, IReadOnlyDictionary<string, string>? options)
    {
        // Recognize complete, unmodified standard-library contracts before the underlying UML grammars.
        if (source.Contains("!include", StringComparison.OrdinalIgnoreCase))
        {
            kind = DiagramKind.C4;
            string? macro = RenderStyled(source, kind, C4Renderer.TryRender);
            if (macro != null) return macro;
            kind = DiagramKind.ArchiMate;
            return RenderStyled(source, kind, ArchimateRenderer.TryRender);
        }
        kind = DiagramKind.Sequence;
        string? svg = SequenceRenderer.TryRender(source);
        if (svg != null) return svg;
        svg = GraphRenderer.TryRender(source, out var state);
        if (svg != null) { kind = state ? DiagramKind.State : DiagramKind.Graph; return svg; }
        kind = DiagramKind.Activity;
        svg = RenderStyled(source, kind, text => ActivityRenderer.TryRender(text, options));
        if (svg != null) return svg;
        kind = DiagramKind.Timing;
        return RenderStyled(source, kind, TimingRenderer.TryRender);
    }
}

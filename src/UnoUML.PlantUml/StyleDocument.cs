using System.Globalization;
using System.Xml.Linq;

namespace UnoUML.PlantUml;

/// <summary>Styles for dialects whose native painters expose source-linked semantic groups.</summary>
internal sealed class StyleDocument(ManagedSkin skin, DiagramKind kind, string[] lines)
{
    private static readonly HashSet<string> Metrics = new("fontsize padding margin maximumwidth minimumwidth width markersize barwidth".Split(' '), StringComparer.Ordinal);

    internal static bool TryPrepare(string source, DiagramKind kind, out string cleaned, out StyleDocument document)
    {
        document = null!;
        if (!StyleSheet.TryExtract(source, out cleaned, out var sheet)) return false;
        var skin = new ManagedSkin { Sheet = sheet, Diagram = DiagramName(kind) };
        char[]? mask = null;
        string? block = null, literal = null;
        int offset = 0, line = 0;
        foreach (var raw in cleaned.AsSpan().EnumerateLines())
        {
            string text = raw.Trim().ToString();
            if (literal != null)
            {
                if (text.Equals(literal, StringComparison.OrdinalIgnoreCase) || literal == "end ref" && text is "endref" or "end") literal = null;
            }
            else if (text.Length != 0 && text[0] != '\'' && (block != null || text.StartsWith("skinparam ", StringComparison.OrdinalIgnoreCase)))
            {
                if (!skin.Read(text, ref block, sequence: kind == DiagramKind.Sequence, sourceLine: line)) return false;
                mask ??= cleaned.ToCharArray();
                for (int i = offset; i < offset + raw.Length; i++) if (mask[i] != '\r') mask[i] = ' ';
            }
            else
            {
                string lower = text.ToLowerInvariant();
                if (lower is "legend" or "legend left" or "legend right" or "legend center") literal = "endlegend";
                else if (lower is "title" or "header" or "footer" or "caption") literal = "end " + lower;
                else if (lower.StartsWith("ref over ", StringComparison.Ordinal) && !text.Contains(':')) literal = "end ref";
                else if ((lower == "note" || lower.StartsWith("note ", StringComparison.Ordinal)) && !lower.StartsWith("note \"", StringComparison.Ordinal) && !text.Contains(':')) literal = "end note";
            }
            offset += raw.Length;
            if (offset < cleaned.Length && cleaned[offset] == '\r') offset++;
            if (offset < cleaned.Length && cleaned[offset] == '\n') offset++;
            line++;
        }
        if (block != null) return false;
        if (mask != null) cleaned = new(mask);
        document = new(skin, kind, source.Split('\n'));
        return true;
    }

    internal string? Apply(string svg)
    {
        if (skin.Sheet.IsEmpty && skin.Dpi == 96 && !skin.Handwritten) return svg;
        var root = XElement.Parse(svg, LoadOptions.PreserveWhitespace);
        foreach (var group in root.Descendants().Where(e => e.Name.LocalName == "g"))
        {
            if (group.Attribute("data-style-name") != null) continue;
            string css = ((string?)group.Attribute("class") ?? "").ToLowerInvariant();
            string source = int.TryParse((string?)group.Attribute("data-source-line"), NumberStyles.None, CultureInfo.InvariantCulture, out int line) &&
                (uint)line < (uint)lines.Length ? lines[line].Trim() : "";
            if (css.Length == 0 && group.Attribute("data-source-line") == null) continue;
            if (css.Length == 0 && group.Ancestors().Any(e => e.Attribute("data-style-name") != null)) continue;
            string name = Category(css, source);
            if (name.Length == 0) continue;
            group.SetAttributeValue("data-style-name", name);
            int stereotype = source.IndexOf("<<", StringComparison.Ordinal), end = source.IndexOf(">>", StringComparison.Ordinal);
            if (stereotype >= 0 && end > stereotype) group.SetAttributeValue("data-style-stereotype", source[(stereotype + 2)..end]);
        }
        foreach (var element in root.Descendants().Where(e => e.Name.LocalName is "text" or "path" or "line" or "polyline" or "polygon"))
        {
            if (element.Ancestors().Any(e => e.Attribute("data-style-name") != null)) continue;
            bool title = lines.Any(line => line.StartsWith("title ", StringComparison.OrdinalIgnoreCase) && line[6..].Trim() == element.Value);
            string name = element.Name.LocalName != "text" ? "arrow" : title ? "title" : kind switch
            {
                DiagramKind.Chart => "axis",
                DiagramKind.Gantt or DiagramKind.Timing => "timeline",
                DiagramKind.Packet => "header",
                DiagramKind.Network => "network",
                _ => "node"
            };
            element.SetAttributeValue("data-style-name", name);
        }
        foreach (var scope in root.DescendantsAndSelf().Where(e => e.Attribute("data-style-name") != null))
        {
            string name = (string)scope.Attribute("data-style-name")!;
            string stereotype = (string?)scope.Attribute("data-style-stereotype") ?? "";
            // A painter must measure typography and spacing before it can accept these values.
            // Rejecting a matching rule keeps layout-sensitive input on the reference path.
            var style = skin.Query(name, stereotype);
            if (style.Keys.Any(Metrics.Contains)) return null;
            // These painters do not expose the background used for each text run.
            if (style.TryGetValue("fontcolor", out var font) && font.Text == "automatic") return null;
            if (kind == DiagramKind.Chart && name.Split(' ').Contains("scatter", StringComparer.Ordinal) &&
                style.TryGetValue("markershape", out var marker) && marker.Text is not ("circle" or "square" or "triangle")) return null;
        }
        string background = skin.Get("backgroundcolor", "none");
        if (background != "none")
        {
            string[] box = ((string?)root.Attribute("viewBox") ?? "0 0 0 0").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (box.Length == 4)
                root.AddFirst(new XElement(root.Name.Namespace + "rect", new XAttribute("x", box[0]), new XAttribute("y", box[1]), new XAttribute("width", box[2]), new XAttribute("height", box[3]), new XAttribute("fill", background)));
        }
        return StyleDrawing.Apply(root, skin);
    }

    private string Category(string css, string source)
    {
        if (css is "link" or "message" or "dependency" or "edge") return "arrow";
        if (css.Contains("legend", StringComparison.Ordinal)) return "legend";
        if (css.Contains("note", StringComparison.Ordinal)) return "note";
        if (css.Contains("title", StringComparison.Ordinal)) return "title";
        if (kind == DiagramKind.Activity)
            return css.Replace("activity-", "", StringComparison.Ordinal) switch
            {
                "action" => "activity",
                "decision" or "switch" or "while" or "repeat" => "diamond",
                "fork" => "activitybar",
                "partition" => "composite",
                "swimlane" => "swimlane",
                "start" => "circle start",
                "stop" => "circle stop",
                "end" => "circle end",
                _ => ""
            };
        if (kind == DiagramKind.Chen)
            return source.StartsWith("entity ", StringComparison.OrdinalIgnoreCase) ? "chenentity" :
                source.StartsWith("relationship ", StringComparison.OrdinalIgnoreCase) ? "chenrelationship" : css == "entity" ? "chenattribute" : "";
        if (kind == DiagramKind.Gantt) return css == "task" ? source.Contains(" happens ", StringComparison.OrdinalIgnoreCase) ? "milestone" : "task" : css.Contains("milestone", StringComparison.Ordinal) ? "milestone" : "";
        if (kind == DiagramKind.Timing)
            return source.StartsWith("robust ", StringComparison.Ordinal) ? "robust" : source.StartsWith("concise ", StringComparison.Ordinal) ? "concise" :
                source.StartsWith("binary ", StringComparison.Ordinal) ? "binary" : source.StartsWith("clock ", StringComparison.Ordinal) ? "clock" : "timeline";
        if (kind == DiagramKind.Network) return css.Contains("network", StringComparison.Ordinal) ? "network" : "server";
        if (kind == DiagramKind.Packet) return "node";
        if (kind is DiagramKind.C4 or DiagramKind.ArchiMate) return "rectangle";
        if (kind == DiagramKind.Chart) return css.Length == 0 ? "node" : css;
        if (kind is DiagramKind.Regex or DiagramKind.Ebnf) return "node";
        return css.Length == 0 ? "node" : css;
    }

    private static string DiagramName(DiagramKind kind) => kind switch
    {
        DiagramKind.Activity => "activitydiagram",
        DiagramKind.Timing => "timingdiagram",
        DiagramKind.Chart => "chartdiagram",
        DiagramKind.Gantt => "ganttdiagram",
        DiagramKind.Network => "nwdiagdiagram",
        DiagramKind.Packet => "packetdiagdiagram",
        DiagramKind.Salt => "saltdiagram",
        DiagramKind.Chen => "cheneerdiagram",
        DiagramKind.Json => "jsondiagram",
        DiagramKind.Yaml => "yamldiagram",
        DiagramKind.Regex => "regex",
        DiagramKind.Ebnf => "ebnf",
        _ => "componentdiagram"
    };
}

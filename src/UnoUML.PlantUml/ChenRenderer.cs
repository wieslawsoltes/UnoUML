using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UnoUML.PlantUml;

/// <summary>Extended Chen entity/relationship notation, including composite attributes and specializations.</summary>
internal static partial class ChenRenderer
{
    internal enum Shape { Entity, Relationship, Attribute, Category }
    internal sealed record Item(string Id, string Label, Shape Shape, string Style, string Fill, int Line)
    {
        internal int Width => Shape switch
        {
            Shape.Category => 30,
            Shape.Relationship => Math.Max(78, 2 * SvgText.Measure(Label) + 24),
            _ => Math.Max(56, SvgText.Measure(Label) + (Shape == Shape.Attribute ? 34 : 24))
        };
        internal int Height => Shape switch { Shape.Relationship => 54, Shape.Category => 30, Shape.Attribute => 36, _ => 38 };
    }

    internal sealed record Link(string From, string To, string Label, bool Total, bool Subset, int Line);
    private const string Id = @"[\p{L}\p{N}_.]+";
    private const string Display = "(?:\"(?<caption>[^\"\\r\\n]+)\"\\s+as\\s+)?";
    private const string Name = Display + "(?<id>" + Id + ")";
    private const string Style = @"(?:\s*<<(?<style>[a-zA-Z]+)>>)?(?:\s+(?<fill>\#[0-9a-fA-F]{6}|\#[0-9a-fA-F]{3}))?";
    [GeneratedRegex(@"^(?<kind>entity|relationship)\s+" + Name + Style + @"\s*\{$", RegexOptions.CultureInvariant)]
    private static partial Regex Declaration();
    [GeneratedRegex("^" + Display + @"(?<id>[\p{L}\p{N}_.:][\p{L}\p{N} _.:]*?)" + Style + @"\s*(?<open>\{)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();
    [GeneratedRegex("^(?<from>" + Id + @")\s*(?<a>[-=])(?<label>[\p{L}\p{N}]+|\([\p{L}\p{N}]+,\s*[\p{L}\p{N}]+\)|[<>])?(?<b>[-=])\s*(?<to>" + Id + ")$", RegexOptions.CultureInvariant)]
    private static partial Regex Association();
    [GeneratedRegex("^(?<parent>" + Id + @")\s*(?<a>[-=])>(?<b>[-=])\s*(?<kind>[odU])\s*\{\s*(?<list>" + Id + @"(?:\s*,\s*" + Id + @")+)\s*\}$", RegexOptions.CultureInvariant)]
    private static partial Regex Category();

    internal static string? TryRender(string source)
    {
        if (source.Length > 350_000) return null;
        var items = new Dictionary<string, Item>(StringComparer.Ordinal);
        var links = new List<Link>();
        var scope = new Stack<Item>();
        bool opened = false, closed = false, horizontal = false;
        int line = -1, titleLine = -1;
        string title = "";
        foreach (var row in source.AsSpan().EnumerateLines())
        {
            line++;
            var span = row.Trim();
            if (span.IsEmpty || span[0] == '\'') continue;
            if (closed || span.Length > 1500 || items.Count >= 1500 || links.Count >= 4500) return null;
            if (!opened) { if (!span.SequenceEqual("@startchen")) return null; opened = true; continue; }
            if (span.SequenceEqual("@endchen")) { closed = true; continue; }
            if (span.SequenceEqual("}")) { if (!scope.TryPop(out _)) return null; continue; }
            string text = span.ToString();
            if (scope.TryPeek(out var owner))
            {
                var a = Attribute().Match(text);
                if (!a.Success) return null;
                var attr = Make(a, owner.Id + "/", Shape.Attribute, line);
                if (attr == null || !items.TryAdd(attr.Id, attr)) return null;
                links.Add(new(attr.Id, owner.Id, "", false, false, line));
                if (a.Groups["open"].Success) { if (scope.Count >= 24) return null; scope.Push(attr); }
                continue;
            }
            if (text == "left to right direction") { horizontal = true; continue; }
            if (text == "top to bottom direction") { horizontal = false; continue; }
            if (text.StartsWith("title ", StringComparison.Ordinal) && titleLine < 0)
            {
                title = text[6..].Trim();
                if (!SvgText.IsPlain(title)) return null;
                titleLine = line; continue;
            }
            var d = Declaration().Match(text);
            if (d.Success)
            {
                var item = Make(d, "", d.Groups["kind"].Value == "entity" ? Shape.Entity : Shape.Relationship, line);
                if (item == null || !items.TryAdd(item.Id, item)) return null;
                scope.Push(item);
                continue;
            }
            var relation = Association().Match(text);
            if (relation.Success)
            {
                string label = relation.Groups["label"].Value, a = relation.Groups["from"].Value, b = relation.Groups["to"].Value;
                bool subtype = label is ">" or "<";
                if (!items.TryGetValue(a, out var first) || !items.TryGetValue(b, out var second)) return null;
                if (subtype && (first.Shape != Shape.Entity || second.Shape != Shape.Entity)) return null;
                if (label == "<") (a, b) = (b, a);
                links.Add(new(a, b, subtype ? "" : label, relation.Groups["a"].Value == "=", subtype, line)); continue;
            }
            var c = Category().Match(text);
            if (!c.Success) return null;
            string parent = c.Groups["parent"].Value, code = c.Groups["kind"].Value, group = "#isa" + line.ToString(CultureInfo.InvariantCulture);
            if (!items.TryGetValue(parent, out var superclass) || superclass.Shape != Shape.Entity) return null;
            var children = c.Groups["list"].Value.Split(',', StringSplitOptions.TrimEntries);
            if (children.Length > 128 || children.Distinct(StringComparer.Ordinal).Count() != children.Length) return null;
            items.Add(group, new(group, code, Shape.Category, "", "#f1f1f1", line));
            links.Add(new(parent, group, "", c.Groups["a"].Value == "=", code == "U", line));
            foreach (var child in children)
            {
                if (child == parent || !items.TryGetValue(child, out var sub) || sub.Shape != Shape.Entity) return null;
                links.Add(new(group, child, "", false, code != "U", line));
            }
        }
        return closed && scope.Count == 0 && items.Count > 0 ? GraphRenderer.RenderChen(items, links, title, titleLine, horizontal) : null;
    }

    private static Item? Make(Match m, string prefix, Shape shape, int line)
    {
        string id = m.Groups["id"].Value.TrimEnd();
        string label = m.Groups["caption"].Success ? m.Groups["caption"].Value : id;
        string style = m.Groups["style"].Value, fill = m.Groups["fill"].Value;
        if (label.Length > 420 || !SvgText.IsPlain(label)) return null;
        if (style.Length != 0 && (shape, style) is not ((Shape.Entity, "weak") or (Shape.Relationship, "identifying") or (Shape.Attribute, "key" or "derived" or "multi"))) return null;
        return new(prefix + id, label, shape, style, fill.Length == 0 ? "#f1f1f1" : fill, line);
    }
}

internal static partial class GraphRenderer
{
    internal static string? RenderChen(Dictionary<string, ChenRenderer.Item> items, List<ChenRenderer.Link> links, string title, int titleLine, bool horizontal)
    {
        var graph = ArrangeCards(items.Values.Select(c => new SizedCard(c.Id, c.Label, c.Line, c.Width, c.Height)),
            links.Select(e => new SizedLink(e.From, e.To, e.Label, e.Line, "--", "", SvgText.Measure(e.Label, 12) + 10, 18)).ToList(), title, horizontal);
        if (graph == null) return null;
        var s = new StringBuilder(items.Count * 300 + links.Count * 250);
        int left = 0, top = 0, right = Math.Max(graph.Root.Width + 64, SvgText.Measure(title, 18) + 36), bottom = graph.Root.Height + (titleLine >= 0 ? 88 : 48);
        var routes = RouteEdges(graph);
        for (int i = 0; i < routes.Count; i++)
        {
            var p = routes[i].Points; var link = links[i];
            foreach (var v in p) { left = Math.Min(left, v.X - 16); right = Math.Max(right, v.X + 16); top = Math.Min(top, v.Y - 16); bottom = Math.Max(bottom, v.Y + 16); }
            string points = string.Join(" ", p.Select(v => FormattableString.Invariant($"{v.X},{v.Y}")));
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"link\" data-source-line=\"{link.Line}\" data-entity-1=\"{SvgText.Escape(link.From)}\" data-entity-2=\"{SvgText.Escape(link.To)}\"><title>{SvgText.Escape(link.Label)}</title><polyline points=\"{points}\" fill=\"none\" stroke=\"transparent\" stroke-width=\"9\"/><polyline points=\"{points}\" fill=\"none\" stroke=\"#313542\" stroke-width=\"{(link.Total ? 2 : 1)}\"/>");
            if (link.Label.Length > 0 || link.Subset)
            {
                Point center = Center(p);
                string caption = link.Subset ? "⊃" : link.Label;
                if (link.Subset)
                {
                    var a = p[0]; var b = p[^1];
                    int rotation = Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y) ? (b.X >= a.X ? 0 : 180) : (b.Y >= a.Y ? 90 : -90);
                    s.Append(CultureInfo.InvariantCulture, $"<g transform=\"rotate({rotation} {center.X} {center.Y})\"><rect x=\"{center.X - 7}\" y=\"{center.Y - 8}\" width=\"14\" height=\"16\" fill=\"white\"/>");
                    SemanticLabel(s, center.X, center.Y + 5, caption, 16, "#313542"); s.Append("</g>");
                }
                else SemanticLabel(s, center.X + SvgText.Measure(caption, 12) / 2 + 3, center.Y - 4, caption, 12, "#313542");
            }
            s.Append("</g>");
        }
        foreach (var n in graph.Nodes)
        {
            var c = items[n.Alias]; int x = n.X, y = n.Y, w = n.Width, h = n.Height, cx = x + w / 2, cy = y + h / 2;
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"entity\" data-source-line=\"{c.Line}\" data-qualified-name=\"{SvgText.Escape(c.Id)}\"><title>{SvgText.Escape(c.Label)}</title>");
            for (int inset = 0; ; inset += 4)
            {
                switch (c.Shape)
                {
                    case ChenRenderer.Shape.Entity: Rect(s, x + inset, y + inset, w - 2 * inset, h - 2 * inset, 0, c.Fill); break;
                    case ChenRenderer.Shape.Relationship: Path(s, $"M{cx} {y + inset}L{x + w - 2 * inset} {cy}L{cx} {y + h - inset}L{x + 2 * inset} {cy}Z", c.Fill); break;
                    default: s.Append(CultureInfo.InvariantCulture, $"<ellipse cx=\"{cx}\" cy=\"{cy}\" rx=\"{w / 2 - inset}\" ry=\"{h / 2 - inset}\" fill=\"{c.Fill}\" stroke=\"#313542\"{(c.Style == "derived" ? " stroke-dasharray=\"6 4\"" : "")}/>"); break;
                }
                if (inset > 0 || c.Style is not ("weak" or "identifying" or "multi")) break;
            }
            s.Append(CultureInfo.InvariantCulture, $"<text x=\"{cx}\" y=\"{cy + 5}\" text-anchor=\"middle\" font-size=\"14\" fill=\"#252837\"{(c.Style == "key" ? " text-decoration=\"underline\"" : "")}>{SvgText.Escape(c.Label)}</text></g>");
        }
        if (titleLine >= 0) SemanticLabel(s, (left + right) / 2, 30, title, 18, "#252837", bold: true);
        return FormattableString.Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{right - left}\" height=\"{bottom - top}\" viewBox=\"{left} {top} {right - left} {bottom - top}\" font-family=\"Arial, sans-serif\">{s}</svg>");
    }
}

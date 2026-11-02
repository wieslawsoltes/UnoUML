using System.Globalization;
using System.Text;
using static UnoUML.PlantUml.LibraryMacro;

namespace UnoUML.PlantUml;

/// <summary>Default Archimate standard-library elements and relationships with direct macro source maps.</summary>
internal static class ArchimateRenderer
{
    internal sealed record Element(string Id, string Type, string Label, int Line)
    {
        internal string Stereotype => "«" + Type.Replace('_', '-').ToLowerInvariant() + "»";
        internal string Fill => Type.Split('_')[0] switch { "Business" => "#FFFFCC", "Application" => "#C2F0FF", _ => "#C9FFC9" };
        internal int Width => Math.Max(SvgText.Measure(Label,14) + 34, SvgText.Measure(Stereotype,12) + 54);
    }
    internal sealed record Relation(string From, string To, string Type, string Direction, string Label, int Line);
    private static readonly HashSet<string> Types = ["Business_Actor", "Business_Role", "Business_Service", "Business_Process", "Application_Component", "Application_DataObject", "Technology_Node"];
    private static readonly HashSet<string> Relations = ["Assignment", "Association", "Composition", "Aggregation", "Specialization", "Realization", "Serving", "Access", "Triggering", "Flow", "Influence"];

    internal static string? TryRender(string source)
    {
        const string include = "!include <archimate/Archimate>";
        if (source.Length > 512_000 || !source.Contains(include, StringComparison.OrdinalIgnoreCase)) return null;
        var elements = new Dictionary<string, Element>(StringComparer.Ordinal);
        var relations = new List<Relation>();
        int titleLine = -1, index = -1;
        string title = "";
        bool opened = false, included = false, closed = false, horizontal = false;
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            index++;
            var line = raw.Trim();
            if (line.IsEmpty || line[0] == '\'') continue;
            if (line.Length > 4096 || closed) return null;
            if (!opened) { if (!line.SequenceEqual("@startuml")) return null; opened = true; continue; }
            if (line.SequenceEqual("@enduml")) { closed = true; continue; }
            if (line.Equals(include,StringComparison.OrdinalIgnoreCase)) { if (included) return null; included = true; continue; }
            if (line.StartsWith("title ", StringComparison.OrdinalIgnoreCase))
            {
                if (titleLine >= 0 || !SvgText.IsPlain(title = line[6..].Trim().ToString())) return null;
                titleLine = index; continue;
            }
            if (line.SequenceEqual("left to right direction")) { horizontal = true; continue; }
            if (line.SequenceEqual("top to bottom direction")) { horizontal = false; continue; }
            if (!included) return null;
            int open = line.IndexOf('(');
            if (open <= 0 || line[^1] != ')' || !Arguments(line[(open + 1)..^1], out var args)) return null;
            string name = line[..open].TrimEnd().ToString();
            if (Types.Contains(name))
            {
                if (elements.Count >= 1000 || args.Count != 2 || !Alias(args[0]) || !Literal(args[1], out string label) || label.Length == 0) return null;
                if (!elements.TryAdd(args[0], new(args[0], name, label, index))) return null;
                continue;
            }
            if (!name.StartsWith("Rel_",StringComparison.Ordinal) || args.Count is < 2 or > 3 || !Alias(args[0]) || !Alias(args[1]) || relations.Count >= 3000) return null;
            string suffix = name[4..], direction = "down";
            int separator = suffix.IndexOf('_');
            if (separator >= 0)
            {
                direction = suffix[(separator + 1)..] switch { "Left" => "left", "Right" => "right", "Up" => "up", "Down" => "down", _ => "" };
                if (direction.Length == 0) return null;
                suffix = suffix[..separator];
            }
            if (!Relations.Contains(suffix)) return null;
            string caption = "";
            if (args.Count == 3 && !Literal(args[2], out caption)) return null;
            relations.Add(new(args[0],args[1],suffix,direction,caption,index));
        }
        if (!included || !closed || elements.Count == 0 || relations.Any(r=>!elements.ContainsKey(r.From) || !elements.ContainsKey(r.To))) return null;
        return GraphRenderer.RenderArchimate(elements,relations,title,titleLine,horizontal);
    }
}

internal static partial class GraphRenderer
{
    internal static string? RenderArchimate(Dictionary<string,ArchimateRenderer.Element> elements,List<ArchimateRenderer.Relation> relations, string title, int titleLine, bool horizontal)
    {
        var graph = ArrangeCards(elements.Values.Select(c=>new SizedCard(c.Id,c.Label,c.Line,c.Width,54)),
            relations.Select(r=>new SizedLink(r.From,r.To,r.Label,r.Line,"--",r.Direction,SvgText.Measure(r.Label,13)+10,22)).ToList(),title,horizontal);
        if (graph == null) return null;
        var byLine = relations.ToDictionary(r => r.Line);
        var s = new StringBuilder(graph.Nodes.Count * 400 + relations.Count * 200);
        int left = 0, top = 0, right = Math.Max(graph.Root.Width+64, SvgText.Measure(title,19)+28), bottom = graph.Root.Height + (titleLine < 0 ? 54 : 94);
        foreach (var route in RouteEdges(graph))
        {
            var r = byLine[route.Edge.Line];
            var p = route.Points;
            foreach (var v in p) { left = Math.Min(left,v.X-20); top = Math.Min(top,v.Y-20); right = Math.Max(right,v.X+20); bottom = Math.Max(bottom,v.Y+20); }
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"link archimate-{r.Type.ToLowerInvariant()}\" data-source-line=\"{r.Line}\" data-entity-1=\"{r.From}\" data-entity-2=\"{r.To}\"><title>{SvgText.Escape(r.Label.Length==0 ? r.Type : r.Label)}</title>");
            var path = (Point[])p.Clone();
            var start = p[0]; var end = p[^1]; var a = p.FirstOrDefault(v=>v!=start,start); var b=p.LastOrDefault(v=>v!=end,end);
            string? startMark = r.Type switch { "Assignment" => "dot", "Aggregation" => "hollow-diamond", "Composition" => "solid-diamond", _ => null };
            string? endMark = r.Type switch { "Specialization" or "Realization" => "hollow-triangle", "Assignment" or "Triggering" or "Flow" => "solid-triangle", "Influence" or "Serving" => "arrow", _ => null };
            if (startMark != null) path[0] = Marker(s,start,a,startMark);
            if (endMark != null) path[^1] = Marker(s,end,b,endMark);
            string points=string.Join(" ",path.Select(v=>FormattableString.Invariant($"{v.X},{v.Y}")));
            s.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{points}\" fill=\"none\" stroke=\"transparent\" stroke-width=\"9\"/><polyline points=\"{points}\" fill=\"none\" stroke=\"#000000\"");
            if (r.Type is "Access" or "Realization") s.Append(" stroke-dasharray=\"1,3\"");
            if (r.Type is "Flow" or "Influence") s.Append(" stroke-dasharray=\"7,7\"");
            s.Append("/>");
            if (r.Label.Length > 0)
            {
                var center = Center(p);
                int w = SvgText.Measure(r.Label,13) + 8;
                s.Append(CultureInfo.InvariantCulture, $"<rect x=\"{center.X-w/2}\" y=\"{center.Y-18}\" width=\"{w}\" height=\"17\" fill=\"white\" fill-opacity=\"0.9\"/>");
                Text(s,center.X,center.Y-5,r.Label,13); left=Math.Min(left,center.X-w/2-6); right=Math.Max(right,center.X+w/2+6);
            }
            s.Append("</g>");
        }
        foreach (var n in graph.Nodes)
        {
            var element = elements[n.Alias];
            int radius = element.Type is "Business_Process" or "Business_Service" ? 12 : 0;
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"entity archimate\" data-source-line=\"{element.Line}\" data-qualified-name=\"{element.Id}\"><title>{SvgText.Escape(element.Label)}</title><rect x=\"{n.X}\" y=\"{n.Y}\" width=\"{n.Width}\" height=\"{n.Height}\" rx=\"{radius}\" fill=\"{element.Fill}\" stroke=\"#181818\" stroke-width=\"0.5\"/>");
            SemanticLabel(s,n.X+n.Width/2-8,n.Y+18,element.Stereotype,12,"#000000");
            SemanticLabel(s,n.X+n.Width/2,n.Y+39,element.Label,14,"#000000");
            string glyph = element.Type switch
            {
                "Business_Actor" => "M8 1a2.4 2.4 0 1 0 .01 0M8 6v5m-5-3h10m-5 3-4 5m4-5 4 5",
                "Business_Role" => "M4 4h8c4 0 4 8 0 8H4C0 12 0 4 4 4zM12 4c-3 0-3 8 0 8",
                "Business_Process" => "M1 5h8V2l6 6-6 6v-3H1z",
                "Business_Service" => "M5 4h6a4 4 0 0 1 0 8H5a4 4 0 0 1 0-8z",
                "Application_Component" => "M4 2h10v12H4zM1 5h5v2H1zM1 9h5v2H1z",
                "Application_DataObject" => "M2 2h12v12H2zM2 6h12",
                _ => "M1 5l4-3h10v9l-4 3H1zM1 5h10l4-3m-4 3v9"
            };
            s.Append(CultureInfo.InvariantCulture, $"<path class=\"archimate-type-glyph\" transform=\"translate({n.X+n.Width-21} {n.Y+5})\" d=\"{glyph}\" fill=\"none\" stroke=\"#181818\" stroke-width=\"1.2\" stroke-linejoin=\"round\"/></g>");
        }
        if (titleLine >= 0)
        {
            s.Append(CultureInfo.InvariantCulture,$"<g data-source-line=\"{titleLine}\">"); Text(s,(left+right)/2,30,title,19,bold:true);s.Append("</g>");
        }
        int width = right-left, height = bottom-top;
        return width is <=0 or >32768 || height is <=0 or >32768 ? null : FormattableString.Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"{left} {top} {width} {height}\" font-family=\"sans-serif\">{s}</svg>");
    }

    private static Point Marker(StringBuilder s,Point tip,Point neighbor,string kind)
    {
        int dx=Math.Sign(neighbor.X-tip.X),dy=Math.Sign(neighbor.Y-tip.Y);
        Point At(int distance,int lateral=0) => new(tip.X+dx*distance-dy*lateral,tip.Y+dy*distance+dx*lateral);
        if (kind=="dot")
        {
            Point center=At(4);
            s.Append(CultureInfo.InvariantCulture,$"<circle class=\"assignment-origin\" cx=\"{center.X}\" cy=\"{center.Y}\" r=\"4\" fill=\"#000000\"/>");return At(8);
        }
        bool diamond=kind.EndsWith("diamond",StringComparison.Ordinal), hollow=kind.StartsWith("hollow-",StringComparison.Ordinal);
        var points=diamond ? new[]{tip,At(6,4),At(12),At(6,-4)} :
            kind=="arrow" ? new[]{tip,At(10,4),At(7),At(10,-4)} : new[]{tip,At(hollow?15:9, hollow?5:4),At(hollow?15:9,hollow?-5:-4)};
        s.Append(CultureInfo.InvariantCulture,$"<polygon class=\"{kind}\" points=\"{string.Join(" ",points.Select(p=>FormattableString.Invariant($"{p.X},{p.Y}")))}\" fill=\"{(hollow?"none":"#000000")}\" stroke=\"#000000\"/>");
        return At(diamond?12:hollow?15:7);
    }
}

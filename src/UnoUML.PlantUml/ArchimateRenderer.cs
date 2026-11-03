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
            string glyph = TypeGlyph(element.Type);
            s.Append(CultureInfo.InvariantCulture, $"<path class=\"archimate-type-glyph\" transform=\"translate({n.X+n.Width-21} {n.Y+5})\" d=\"{glyph}\" fill=\"none\" stroke=\"#181818\" stroke-width=\"1.2\" stroke-linejoin=\"round\"/></g>");
        }
        if (titleLine >= 0)
        {
            s.Append(CultureInfo.InvariantCulture,$"<g data-source-line=\"{titleLine}\">"); Text(s,(left+right)/2,30,title,19,bold:true);s.Append("</g>");
        }
        int width = right-left, height = bottom-top;
        return width is <=0 or >32768 || height is <=0 or >32768 ? null : FormattableString.Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"{left} {top} {width} {height}\" font-family=\"sans-serif\">{s}</svg>");
    }

    private static string TypeGlyph(string stereotype)
    {
        string name = stereotype.Replace('_', '-').ToLowerInvariant();
        int separator = name.IndexOf('-');
        if (separator >= 0 && name[..separator] is "business" or "application" or "technology" or "physical" or "strategy" or "motivation" or "implementation")
            name = name[(separator + 1)..];
        return name switch
        {
            "actor" or "stakeholder" => "M8 1a2.4 2.4 0 1 0 .01 0M8 6v5m-5-3h10m-5 3-4 5m4-5 4 5",
            "role" => "M4 4h8c4 0 4 8 0 8H4C0 12 0 4 4 4zM12 4c-3 0-3 8 0 8",
            "collaboration" => "M6 3a5 5 0 1 0 .01 0M11 3a5 5 0 1 0 .01 0",
            "interface" => "M8 1a5 5 0 1 0 .01 0M8 11v4",
            "process" or "courseofaction" or "course-of-action" => "M1 5h8V2l6 6-6 6v-3H1z",
            "function" => "M2 12V4h12v8l-6-4z",
            "interaction" => "M2 3h4v10H2M14 3h-4v10h4M6 6h4m-4 4h4",
            "event" or "implementationevent" or "implementation-event" => "M1 3h10l4 5-4 5H1l4-5z",
            "service" => "M5 4h6a4 4 0 0 1 0 8H5a4 4 0 0 1 0-8z",
            "component" => "M4 2h10v12H4zM1 5h5v2H1zM1 9h5v2H1z",
            "dataobject" or "data-object" or "object" => "M2 2h12v12H2zM2 6h12",
            "contract" => "M2 2h12v12H2zM2 6h12M5 9h6m-6 3h6",
            "representation" => "M2 2h12v10c-4-3-8 3-12 0z",
            "product" => "M2 2h12v12H2zM2 6h7V2",
            "node" => "M1 5l4-3h10v9l-4 3H1zM1 5h10l4-3m-4 3v9",
            "device" => "M1 2h14v9H1zM5 14h6M8 11v3",
            "systemsoftware" or "system-software" => "M3 4h10v8H3zM1 2h10m-10 0v8M5 6h10v8H5z",
            "path" => "M1 6h14m-14 4h14M4 3L1 6l3 3m8-2 3 3-3 3",
            "communicationnetwork" or "communication-network" or "network" or "distributionnetwork" or "distribution-network" => "M2 2h4v4H2zM10 10h4v4h-4zM2 10h4v4H2zM6 4h6v6M4 6v4",
            "artifact" or "deliverable" => "M2 1h8l4 4v10H2zM10 1v4h4M5 8h6m-6 3h6",
            "equipment" => "M1 5l4-3h10v9l-4 3H1zM1 5h10l4-3M11 5v9M4 7h4v4H4z",
            "facility" => "M1 14V6l7-5 7 5v8zM6 14V9h4v5",
            "material" => "M2 3h12v10H2zM2 3l12 10M2 13 14 3",
            "resource" => "M1 4h14v9H1zM1 7h14",
            "capability" => "M1 4h14v9H1zM5 4v9m6-9v9",
            "driver" => "M8 1a7 7 0 1 0 .01 0M8 1v14M1 8h14",
            "assessment" => "M1 1h14v14H1zM8 4v5m0 3v1",
            "goal" => "M8 1a7 7 0 1 0 .01 0M8 5a3 3 0 1 0 .01 0",
            "outcome" => "M8 1l7 7-7 7-7-7zM4 8h8",
            "principle" => "M1 4h14m-7-3v13M1 14h14M3 4l-2 6h4zM13 4l-2 6h4z",
            "requirement" => "M1 4l4-3h10v10l-4 3H1zM4 6h8m-8 3h8",
            "constraint" => "M1 4l4-3h10v10l-4 3H1zM4 8h8",
            "meaning" => "M1 5c0-4 5-5 7-2 3-4 8-2 7 2 3 3-1 7-4 5-2 4-7 4-8 0-4 0-4-4-2-5z",
            "value" => "M8 2c9 0 9 12 0 12S-1 2 8 2z",
            "workpackage" or "work-package" => "M1 4h5V2h4v2h5v10H1zM1 8h14",
            "plateau" => "M1 6l4-3h10v8l-4 3H1zM1 6h10l4-3m-4 3v8M1 10h10l4-3",
            "gap" => "M2 1v14m12-14v14M2 8h12M5 5 2 8l3 3m6-6 3 3-3 3",
            "location" => "M8 1c-7 0-7 8 0 14 7-6 7-14 0-14zM8 4a2 2 0 1 0 .01 0",
            "grouping" => "M1 4h6V2h8v12H1z",
            _ => ""
        };
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

using System.Globalization;
using System.Text;
using static UnoUML.PlantUml.LibraryMacro;

namespace UnoUML.PlantUml;

/// <summary>Semantic implementation of the pinned C4-PlantUML context macros. Unknown customization delegates intact.</summary>
internal static class C4Renderer
{
    private enum Library { Context, Container, Component, Deployment }

    internal sealed record Card(string Id, string Label, string[] Description, string Type, string Shape,
        string Technology, bool External, int Line, string? Parent, bool Group, bool Boundary)
    {
        internal int EndLine { get; set; } = -1;
        internal bool Person => Type == "person";
        internal string Stereotype => Boundary ? (Type.Length == 0 ? "" : "[" + Type + "]") :
            "«" + (External ? "external_" : "") + Type + "»";
        internal string Fill => Type switch
        {
            "person" => External ? "#686868" : "#08427B", "system" => External ? "#999999" : "#1168BD",
            "container" => External ? "#B3B3B3" : "#438DD5", "component" => External ? "#CCCCCC" : "#85BBF0",
            _ => "#FFFFFF"
        };
        internal string Stroke => Type switch
        {
            "person" => External ? "#8A8A8A" : "#073B6F", "system" => External ? "#8A8A8A" : "#3C7FC0",
            "container" => External ? "#A6A6A6" : "#3C7FC0", "component" => External ? "#BFBFBF" : "#78A8D8",
            _ => "#A2A2A2"
        };
        internal string Font => Type is "component" or "node" ? "#000000" : "#FFFFFF";
        internal int Width => Math.Max(Math.Max(SvgText.Measure(Label, 16) + 28, SvgText.Measure(Stereotype, 12) + 24),
            Math.Max(SvgText.Measure(Technology,12)+36, Description.Select(t => SvgText.Measure(t) + 24).DefaultIfEmpty(0).Max()));
        internal int Height => 53 + (Person ? 52 : 0) + (Technology.Length == 0 ? 0 : 16) +
            (Description.Length == 0 ? 0 : 16 + 16 * Description.Length) + (Shape == "database" ? 9 : 0) + (Group ? 8 : 0);
    }

    internal sealed record Relation(string From, string To, string Label, string Technology, string[] Description, string Arrow, string Direction, int Line)
    {
        internal int LabelWidth => Math.Max(Math.Max(SvgText.Measure(Label, 12), SvgText.Measure(Technology, 11) + 10),
            Description.Select(s => SvgText.Measure(s, 12)).DefaultIfEmpty(0).Max()) + 12;
        internal int LabelHeight => 18 + (Technology.Length == 0 ? 0 : 14) + Description.Length * 14;
    }
    internal static string? TryRender(string source)
    {
        if (source.Length > 512_000 || !source.Contains("!include <C4/C4_", StringComparison.OrdinalIgnoreCase)) return null;
        var scopes = new Stack<Card>();
        Library library = Library.Context;
        var nodes = new Dictionary<string, Card>(StringComparer.Ordinal);
        var edges = new List<Relation>();
        string title = "";
        int titleLine = -1, index = -1;
        bool opened = false, included = false, closed = false, horizontal = false;
        foreach (var row in source.AsSpan().EnumerateLines())
        {
            index++;
            var line = row.Trim();
            if (line.IsEmpty || line[0] == '\'') continue;
            if (line.Length > 4096 || closed) return null;
            if (!opened) { if (!line.SequenceEqual("@startuml")) return null; opened = true; continue; }
            if (line.SequenceEqual("@enduml")) { closed = true; continue; }
            if (line.StartsWith("!include ", StringComparison.OrdinalIgnoreCase))
            {
                if (included) return null;
                string module = line[8..].Trim().ToString().ToLowerInvariant();
                if (module is not ("<c4/c4_context>" or "<c4/c4_container>" or "<c4/c4_component>" or "<c4/c4_deployment>")) return null;
                library = module switch { "<c4/c4_container>" => Library.Container, "<c4/c4_component>" => Library.Component,
                    "<c4/c4_deployment>" => Library.Deployment, _ => Library.Context };
                included = true; continue;
            }
            if (line.StartsWith("title ", StringComparison.OrdinalIgnoreCase))
            {
                if (titleLine >= 0 || !SvgText.IsPlain(title = line[6..].Trim().ToString())) return null;
                titleLine = index; continue;
            }
            if (!included) return null;
            if (line.SequenceEqual("LAYOUT_LEFT_RIGHT()")) { horizontal = true; continue; }
            if (line.SequenceEqual("LAYOUT_TOP_DOWN()")) { horizontal = false; continue; }
            if (line.SequenceEqual("}"))
            {
                if (!scopes.TryPop(out var closedScope)) return null;
                closedScope.EndLine = index; continue;
            }
            bool opens = line[^1] == '{';
            if (opens) line = line[..^1].TrimEnd();
            int open = line.IndexOf('(');
            if (open < 1 || line[^1] != ')' || !Arguments(line[(open + 1)..^1], out var args)) return null;
            string name = line[..open].TrimEnd().ToString();
            if (!name.StartsWith("Rel",StringComparison.Ordinal) && !name.StartsWith("BiRel",StringComparison.Ordinal))
            {
                if (nodes.Count >= 1000 || scopes.Count >= 48 || args.Count < 2 || !Alias(args[0]) ||
                    !Literal(args[1], out var label) || label.Length == 0) return null;
                if (!ParseCard(name,library,args,label,scopes.TryPeek(out var parent)?parent.Id:null,opens,index,out var c)) return null;
                if (!nodes.TryAdd(c!.Id, c)) return null;
                if (opens) scopes.Push(c);
                continue;
            }
            if (opens) return null;
            string direction;
            bool bidirectional = name.StartsWith("BiRel", StringComparison.Ordinal);
            string suffix;
            if (bidirectional) suffix = name[5..]; else if (name.StartsWith("Rel", StringComparison.Ordinal)) suffix = name[3..]; else return null;
            switch (suffix)
            {
                case "": case "_Back": direction = ""; break;
                case "_Back_Neighbor": case "_Neighbor": case "_R": case "_Right": direction = "right"; break;
                case "_L": case "_Left": direction = "left"; break;
                case "_D": case "_Down": direction = "down"; break;
                case "_U": case "_Up": direction = "up"; break;
                default: return null;
            }
            if (bidirectional && suffix.StartsWith("_Back", StringComparison.Ordinal)) return null;
            if (args.Count is < 3 or > 5 || !Alias(args[0]) || !Alias(args[1]) || !Literal(args[2], out var caption) || edges.Count >= 3000) return null;
            string technology = "", descr = "";
            if (args.Count > 3 && !Literal(args[3], out technology) || args.Count > 4 && !Literal(args[4], out descr)) return null;
            edges.Add(new(args[0], args[1], caption, technology, Wrap(descr, 32),
                bidirectional ? "<-->" : suffix.StartsWith("_Back",StringComparison.Ordinal) ? "<--" : "-->", direction, index));
        }
        if (!closed || !included || scopes.Count != 0 || nodes.Count == 0 || edges.Any(e => !nodes.ContainsKey(e.From) || !nodes.ContainsKey(e.To))) return null;
        return GraphRenderer.RenderC4(nodes, edges, title, titleLine, horizontal);
    }

    private static bool ParseCard(string name, Library library, List<string> args, string label, string? parent,
        bool opens, int line, out Card? card)
    {
        card = null;
        bool hasContainer = library != Library.Context;
        bool external = name.EndsWith("_Ext", StringComparison.Ordinal);
        string baseName = external ? name[..^4] : name;
        string type, shape = "system", tech = "", desc = "";
        bool boundary = false, node = false;
        switch (baseName)
        {
            case "Person": type = "person"; shape = "person"; break;
            case "System": case "SystemDb": case "SystemQueue": type = "system"; break;
            case "Container" or "ContainerDb" or "ContainerQueue" when hasContainer: type = "container"; break;
            case "Component" or "ComponentDb" or "ComponentQueue" when library == Library.Component: type = "component"; break;
            case "Node" or "Deployment_Node" when library == Library.Deployment && !external:
                type = "node"; shape = "node"; node = true; break;
            case "Boundary" or "Enterprise_Boundary" or "System_Boundary" when !external:
            case "Container_Boundary" when hasContainer && !external:
                boundary = true; shape = "boundary";
                type = baseName == "Boundary" ? "" : baseName[..baseName.IndexOf('_')].ToLowerInvariant();
                break;
            default: return false;
        }
        if (opens && !boundary && !node) return false;
        if (boundary)
        {
            // Optional tags and links can change rendering and hit behavior. A scope
            // description is safe when those positional slots are explicitly empty.
            int firstOption = baseName == "Boundary" ? 3 : 2;
            if (baseName == "Boundary" && args.Count>2 && !Literal(args[2],out type)) return false;
            if (args.Count>firstOption && args[firstOption] != "\"\"" ||
                args.Count>firstOption+1 && args[firstOption+1] != "\"\"" ||
                args.Count>firstOption+2 && !Literal(args[firstOption+2],out desc) || args.Count>firstOption+3) return false;
        }
        else
        {
            if (baseName.EndsWith("Db",StringComparison.Ordinal)) shape="database";
            else if (baseName.EndsWith("Queue",StringComparison.Ordinal)) shape="queue";
            else if (type is "container" or "component") shape=type;
            bool technology = type is "component" or "container" or "node";
            int descIndex = technology ? 3 : 2;
            if (technology && args.Count>2 && !Literal(args[2],out tech) ||
                args.Count>descIndex && !Literal(args[descIndex],out desc) || args.Count>descIndex+1) return false;
        }
        card = new(args[0],label,Wrap(desc,40),type,shape,tech,external,line,parent,opens,boundary);
        return true;
    }

    private static string[] Wrap(string text, int width)
    {
        if (text.Length == 0) return [];
        var lines = new List<string>();
        var row = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (row.Length != 0 && row.Length + word.Length + 1 > width) { lines.Add(row.ToString()); row.Clear(); }
            if (row.Length != 0) row.Append(' ');
            row.Append(word);
        }
        if (row.Length > 0) lines.Add(row.ToString());
        return lines.ToArray();
    }
}

internal static partial class GraphRenderer
{
    internal static string? RenderC4(Dictionary<string, C4Renderer.Card> cards, List<C4Renderer.Relation> relations, string title, int titleLine, bool horizontal)
    {
        var graph = ArrangeCards(cards.Values.Select(c => new SizedCard(c.Id, c.Label, c.Line, c.Width, c.Height, c.Parent, c.Group)),
            relations.Select(r => new SizedLink(r.From, r.To, r.Label, r.Line, r.Arrow, r.Direction, r.LabelWidth, r.LabelHeight)).ToList(), title, horizontal);
        if (graph == null) return null;
        var relByLine = relations.ToDictionary(r => r.Line);
        var s = new StringBuilder(graph.Nodes.Count * 310 + relations.Count * 300);
        int left = 0, top = 0, right = Math.Max(graph.Root.Width + 64, SvgText.Measure(title, 19) + 40), bottom = graph.Root.Height + (titleLine < 0 ? 54 : 94);
        foreach (var n in graph.Nodes)
        {
            var c = cards[n.Alias];
            if (c.Group || c.Boundary) DrawC4Group(s,n,c);
        }
        foreach (var route in RouteEdges(graph))
        {
            var rel = relByLine[route.Edge.Line];
            var p = route.Points;
            foreach (var v in p) { left = Math.Min(left, v.X - 15); right = Math.Max(right, v.X + 15); top = Math.Min(top, v.Y - 15); bottom = Math.Max(bottom, v.Y + 15); }
            string points = string.Join(" ", p.Select(v => FormattableString.Invariant($"{v.X},{v.Y}")));
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"link\" data-source-line=\"{rel.Line}\" data-entity-1=\"{SvgText.Escape(route.Edge.From.Qualified)}\" data-entity-2=\"{SvgText.Escape(route.Edge.To.Qualified)}\"><title>{SvgText.Escape(rel.Label)}</title><polyline points=\"{points}\" fill=\"none\" stroke=\"transparent\" stroke-width=\"9\"/><polyline points=\"{points}\" fill=\"none\" stroke=\"#666666\"/>");
            if (p.Length > 1)
            {
                if (rel.Arrow[0] == '<') Arrow(p[0], p.FirstOrDefault(q => q != p[0], p[0]));
                if (rel.Arrow[^1] == '>') Arrow(p[^1], p.LastOrDefault(q => q != p[^1], p[^1]));
            }
            var center = Center(p);
            bool vertical = false;
            for (int i = 1; i < p.Length; i++)
            {
                if (p[i - 1].X != center.X || p[i].X != center.X || p[i].Y == p[i - 1].Y) continue;
                if (center.Y >= Math.Min(p[i - 1].Y, p[i].Y) && center.Y <= Math.Max(p[i - 1].Y, p[i].Y)) { vertical = true; break; }
            }
            int x = center.X + (vertical ? rel.LabelWidth / 2 + 4 : 0);
            int labelTop = center.Y - (vertical ? rel.LabelHeight / 2 : rel.LabelHeight + 3);
            int y = labelTop + 11;
            SemanticLabel(s, x, y, rel.Label, 12, "#666666", bold: true);
            if (rel.Technology.Length != 0) SemanticLabel(s, x, y += 14, "[" + rel.Technology + "]", 11, "#666666", italic: true);
            foreach (string row in rel.Description) SemanticLabel(s, x, y += 14, row, 12, "#666666");
            s.Append("</g>");
            left = Math.Min(left, x - rel.LabelWidth / 2 - 10); right = Math.Max(right, x + rel.LabelWidth / 2 + 10);
            top = Math.Min(top, labelTop - 6); bottom = Math.Max(bottom, y + 8);

            void Arrow(Point tip, Point source)
            {
                int dx = Math.Sign(source.X - tip.X), dy = Math.Sign(source.Y - tip.Y);
                s.Append(CultureInfo.InvariantCulture, $"<polygon points=\"{tip.X},{tip.Y} {tip.X+dx*9-dy*4},{tip.Y+dy*9+dx*4} {tip.X+dx*6},{tip.Y+dy*6} {tip.X+dx*9+dy*4},{tip.Y+dy*9-dx*4}\" fill=\"#666666\"/>");
            }
        }
        foreach (var n in graph.Nodes)
        {
            var c = cards[n.Alias];
            if (c.Group || c.Boundary) continue;
            int x = n.X, y = n.Y, w = n.Width, h = n.Height, cx = x+w/2;
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"entity c4-{c.Shape}\" data-qualified-name=\"{SvgText.Escape(n.Qualified)}\" data-c4-alias=\"{SvgText.Escape(c.Id)}\" data-source-line=\"{c.Line}\"><title>{SvgText.Escape(c.Label)}</title>");
            if (c.Shape == "database")
                s.Append(CultureInfo.InvariantCulture, $"<path d=\"M{x} {y+8}c0 -10 {w} -10 {w} 0v{h-16}c0 10 {-w} 10 {-w} 0zM{x} {y+8}c0 9 {w} 9 {w} 0\" fill=\"{c.Fill}\" stroke=\"{c.Stroke}\"/>");
            else if (c.Shape == "queue")
                s.Append(CultureInfo.InvariantCulture, $"<path d=\"M{x+7} {y}h{w-14}c10 0 10 {h} 0 {h}h{-w+14}c-10 0-10 {-h} 0 {-h}zM{x+w-7} {y}c-9 0-9 {h} 0 {h}\" fill=\"{c.Fill}\" stroke=\"{c.Stroke}\"/>");
            else s.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" fill=\"{c.Fill}\" stroke=\"{c.Stroke}\" stroke-width=\"0.5\"/>");
            int textY = y + (c.Shape == "database" ? 30 : 21);
            SemanticLabel(s, cx, textY, c.Stereotype, 12, c.Font, italic: c.Type != "node");
            if (c.Person)
            {
                int iconY = textY + 4;
                s.Append(CultureInfo.InvariantCulture, $"<g class=\"person-icon\" transform=\"translate({cx} {iconY})\" fill=\"white\"><circle cy=\"12\" r=\"11\"/><path d=\"M-19 44v-3c0-8 4-15 10-17q9 6 18 0c6 2 10 9 10 17v3q0 3-4 3h-30q-4 0-4-3z\"/></g>");
                textY += 52;
            }
            SemanticLabel(s, cx, textY += 18, c.Label, 16, c.Font, bold: true);
            if (c.Technology.Length != 0) SemanticLabel(s,cx,textY += 15,"["+c.Technology+"]",12,c.Font,italic:true);
            if (c.Description.Length != 0) textY += 16;
            foreach (var row in c.Description) SemanticLabel(s, cx, textY += 16, row, 14, c.Font);
            s.Append("</g>");
        }
        if (titleLine >= 0)
        {
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"title\" data-source-line=\"{titleLine}\">");
            SemanticLabel(s,(left+right)/2,30,title,19,Ink,bold:true); s.Append("</g>");
        }
        int width = right - left, height = bottom - top;
        return width is <= 0 or > 32768 || height is <= 0 or > 32768 ? null : FormattableString.Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"{left} {top} {width} {height}\" font-family=\"sans-serif\">{s}</svg>");
    }

    private static void DrawC4Group(StringBuilder svg, Node n, C4Renderer.Card card)
    {
        int cx=n.X+n.Width/2;
        svg.Append(CultureInfo.InvariantCulture,$"<g class=\"cluster c4-{card.Shape}\" data-qualified-name=\"{SvgText.Escape(n.Qualified)}\" data-c4-alias=\"{SvgText.Escape(card.Id)}\" data-source-line=\"{card.Line}\"");
        if(card.EndLine>=0) svg.Append(CultureInfo.InvariantCulture,$" data-source-end-line=\"{card.EndLine}\"");
        svg.Append('>').Append("<title>").Append(SvgText.Escape(card.Label)).Append("</title>");
        string fill = card.Boundary ? "none" : card.Fill;
        string stroke = card.Boundary ? "#444444" : card.Stroke;
        svg.Append(CultureInfo.InvariantCulture,$"<rect x=\"{n.X}\" y=\"{n.Y}\" width=\"{n.Width}\" height=\"{n.Height}\" fill=\"{fill}\" stroke=\"{stroke}\"");
        if(card.Boundary) svg.Append(" stroke-dasharray=\"7 7\"");
        svg.Append("/>");
        int y=n.Y+20;
        if (card.Boundary)
        {
            SemanticLabel(svg,cx,y,card.Label,16,stroke,bold:true);
            if(card.Stereotype.Length!=0) SemanticLabel(svg,cx,y+=16,card.Stereotype,12,stroke,bold:true);
        }
        else
        {
            SemanticLabel(svg,cx,y,card.Stereotype,12,card.Font);
            SemanticLabel(svg,cx,y+=18,card.Label,16,card.Font,bold:true);
            if(card.Technology.Length!=0) SemanticLabel(svg,cx,y+=15,"["+card.Technology+"]",12,card.Font,italic:true);
        }
        if(card.Description.Length!=0) y+=10;
        foreach(var row in card.Description) SemanticLabel(svg,cx,y+=16,row,14,card.Boundary?stroke:card.Font);
        svg.Append("</g>");
    }

}

using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

internal static partial class GraphRenderer
{
    private readonly record struct Point(int X, int Y);
    private sealed record Route(Edge Edge, Point[] Points);
    private const string Ink = "#343544";

    private static string? Draw(Graph graph)
    {
        var body = new StringBuilder(graph.Nodes.Count * 280);
        var routes = RouteEdges(graph);
        int left = 0, top = 0, right = graph.Root.Width + 64, bottom = graph.Root.Height + (graph.Heading == null ? 52 : 73 + graph.Heading.Height);
        right = Math.Max(right, (graph.Heading?.Width ?? 0) + 48);
        foreach (var port in graph.Nodes.Where(n => n.IsStatePort))
        {
            int cx = port.X + 6, half = SvgText.Measure(port.Label) / 2 + 6;
            left = Math.Min(left, cx - half); right = Math.Max(right, cx + half);
            top = Math.Min(top, PortCaptionY(port) - 19); bottom = Math.Max(bottom, PortCaptionY(port) + 8);
        }
        foreach (var n in graph.Nodes.Where(n => n.Children.Count != 0)) DrawNode(body, n, graph);
        foreach (var r in routes)
        {
            foreach (var p in r.Points) { left = Math.Min(left, p.X - 22); right = Math.Max(right, p.X + 22); top = Math.Min(top, p.Y - 22); bottom = Math.Max(bottom, p.Y + 22); }
            var label = DrawEdge(body, r, graph.Skin);
            left = Math.Min(left, label.Min); right = Math.Max(right, label.Max); top = Math.Min(top, label.Top); bottom = Math.Max(bottom, label.Bottom);
        }
        foreach (var n in graph.Nodes.Where(n => n.Children.Count == 0)) DrawNode(body, n, graph);
        if (graph.Heading is { } heading)
            heading.Append(body, graph.Skin, (left + right) / 2, 11 + heading.FontSize - SvgText.Ascent("M", heading.FontSize));
        if (graph.Legend.Count > 0)
        {
            int legendSize = graph.Skin.Size("legendfontsize", 13);
            int legendWidth = graph.Legend.Max(l => SvgText.Measure(l, legendSize)) + 22;
            right = Math.Max(right, left + legendWidth + 28);
            int legendX = graph.LegendAlign switch { "left" => left + 12, "right" => right - legendWidth - 12, _ => (left + right - legendWidth) / 2 };
            int legendHeight = graph.Legend.Count * (legendSize + 6) + 12;
            body.Append(CultureInfo.InvariantCulture, $"<g class=\"legend\" data-style-name=\"legend\" data-source-line=\"{graph.LegendLine}\"><title>Legend</title>");
            Rect(body, legendX, bottom, legendWidth, legendHeight, 2, "#fcfcfc");
            for (int i = 0; i < graph.Legend.Count; i++) Text(body, legendX + 10, bottom + legendSize + 6 + i * (legendSize + 6), graph.Legend[i], legendSize, "start", color: graph.Skin.Get("defaultfontcolor","#232333"));
            body.Append("</g>"); bottom += legendHeight + 12;
        }
        int w = right - left, h = bottom - top;
        if (w > 32768 || h > 32768) return null;
        string background = graph.Skin.Get("backgroundcolor","none");
        if (background != "none") body.Insert(0, FormattableString.Invariant($"<rect x=\"{left}\" y=\"{top}\" width=\"{w}\" height=\"{h}\" fill=\"{background}\"/>"));
        return StyleDrawing.Apply(FormattableString.Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w}\" height=\"{h}\" viewBox=\"{left} {top} {w} {h}\" font-family=\"{graph.Skin.Get("defaultfontname","Arial, sans-serif")}\" font-size=\"14\">{body}</svg>"), graph.Skin);
    }

    private static void DrawNode(StringBuilder s, Node n, Graph graph)
    {
        int x = n.X, y = n.Y, w = n.Width, h = n.Height, cx = x + w / 2;
        string scope = StyleCategory(n);
        string stroke = graph.Skin.For(scope, "bordercolor", n.Stereotype, Ink);
        string defaultFont = graph.Skin.Get("defaultfontcolor","#232333");
        string labelFont = graph.Skin.For(scope, "fontcolor", n.Stereotype, defaultFont);
        string memberFont = graph.Skin.For(scope, "memberfontcolor", n.Stereotype, defaultFont);
        string stereotypeFont = graph.Skin.For(scope, "stereotypefontcolor", n.Stereotype, defaultFont);
        string weight = graph.Skin.For(scope, "borderthickness",n.Stereotype,"1");
        s.Append(CultureInfo.InvariantCulture, $"<g class=\"entity{(n.Kind == "archimate" ? " archimate" : "")}\" data-style-name=\"{scope}\" data-style-stereotype=\"{SvgText.Escape(n.Stereotype)}\" stroke-width=\"{weight}\"");
        if (n.ExplicitFill) s.Append(" data-style-fill=\"explicit\"");
        // Inferred state names have no declaration. Never bind a node edit to its first transition.
        if (!n.Implicit) s.Append(CultureInfo.InvariantCulture, $" data-source-line=\"{n.Line}\"");
        if (n.Alias != "") s.Append(CultureInfo.InvariantCulture, $" data-qualified-name=\"{SvgText.Escape(n.Qualified)}\"");
        s.Append("><title>").Append(SvgText.Escape(n.Label)).Append("</title>");
        if (n.IsStatePort)
        {
            DrawStatePort(s, n,defaultFont); s.Append("</g>"); return;
        }
        var fill = n.ExplicitFill ? n.Fill : graph.Skin.For(scope,"backgroundcolor",n.Stereotype,n.Fill);
        int labelY = y + (n.Children.Count == 0 ? h : n.HeaderHeight) / 2 + 5;
        int labelX = cx;
        string shape = n.IsSpecial ? n.Stereotype switch { "start" => "initial", "end" => "final", _ => n.Stereotype } : n.Kind;
        if (!DrawDeploymentSymbol(s, n, ref labelY, fill, stroke)) switch (shape)
            {
                case "initial":
                case "final":
                    s.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{cx}\" cy=\"{y + 10}\" r=\"9\" fill=\"{(shape == "initial" ? Ink : "white")}\" stroke=\"{Ink}\"/>");
                    if (shape == "final") s.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{cx}\" cy=\"{y + 10}\" r=\"6\" fill=\"{Ink}\"/>");
                    break;
                case "choice":
                    Outline( $"M{cx} {y}L{x + w} {y + h / 2}L{cx} {y + h}L{x} {y + h / 2}Z", fill); break;
                case "fork":
                case "join":
                    Box( x, y, w, h, 2, Ink); break;
                case "history":
                case "history*":
                    s.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{cx}\" cy=\"{y + h / 2}\" r=\"14\" fill=\"{fill}\" stroke=\"{Ink}\"/>");
                    Text(s, cx, y + h / 2 + 5, shape == "history" ? "H" : "H*", 14); break;
                case "actor/":
                case "actor":
                    s.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{cx}\" cy=\"{y + 10}\" r=\"8\" fill=\"{fill}\" stroke=\"{Ink}\"/>");
                    if (n.Kind == "actor/") Outline( $"M{cx - 7} {y + 14}l13 -9");
                    if (graph.Skin.ActorStyle == "stickman") Outline( $"M{cx} {y + 18}v20m-13 -12h26m-13 12l-12 16m12 -16l12 16");
                    else Outline($"M{cx - 8} {y + 20}Q{cx - 16} {y + 20} {cx - 16} {y + 29}V{y + 37}H{cx - 8}V{y + 53}H{cx - 1}V{y + 38}H{cx + 1}V{y + 53}H{cx + 8}V{y + 37}H{cx + 16}V{y + 29}Q{cx + 16} {y + 20} {cx + 8} {y + 20}Z", graph.Skin.ActorStyle == "awesome" ? fill : "none");
                    labelY = y + 70; break;
                case "database":
                    Outline( $"M{x} {y + 8}C{x} {y - 2} {x + w} {y - 2} {x + w} {y + 8}V{y + h - 8}C{x + w} {y + h + 2} {x} {y + h + 2} {x} {y + h - 8}Z", fill);
                    Outline( $"M{x} {y + 8}C{x} {y + 18} {x + w} {y + 18} {x + w} {y + 8}"); labelY += 4; break;
                case "usecase/":
                case "usecase":
                    s.Append(CultureInfo.InvariantCulture, $"<ellipse cx=\"{cx}\" cy=\"{y + h / 2}\" rx=\"{w / 2}\" ry=\"{h / 2}\" fill=\"{fill}\" stroke=\"{Ink}\"/>");
                    if (n.Kind == "usecase/") Outline( $"M{x + w - 20} {y + h - 3}l16 -18");
                    break;
                case "namespace":
                case "package":
                case "folder":
                    Outline( $"M{x} {y + 6}V{y}h{Math.Min(w - 6, Math.Max(28, w / 3))}l7 7H{x + w}V{y + h}H{x}Z", fill);
                    labelY = y + 27; break;
                case "node":
                    Outline( $"M{x} {y + 9}l10 -9H{x + w}V{y + h - 9}l-10 9H{x}Z", fill);
                    Outline( $"M{x} {y + 9}H{x + w - 10}l10 -9m-10 9V{y + h}"); labelY += 3; break;
                case "file":
                case "note":
                    Outline( $"M{x} {y}H{x + w - 12}l12 12V{y + h}H{x}Z", fill);
                    Outline( $"M{x + w - 12} {y}v12h12"); break;
                default:
                    Box( x, y, w, h, n.Kind == "state" ? 10 : 1, fill);
                    if (n.Kind == "component") { Box( x + w - 20, y + 6, 12, 9, 0, fill); Box( x + w - 23, y + 7, 5, 2, 0, Ink); Box( x + w - 23, y + 11, 5, 2, 0, Ink); }
                    break;
            }
        bool headerCard = n.IsType || n.Kind == "state" && n.Members.Count > 0;
        if (headerCard)
        {
            int header = n.HeaderHeight;
            labelY = y + header - n.Padding.Bottom - 2;
            string headerFill = graph.Skin.Style(scope, "backgroundcolor", fill, n.Stereotype, "header");
            if (!n.ExplicitFill && headerFill != fill)
            {
                int round = Math.Min(graph.Skin.Number(scope, "roundcorner", 2, n.Stereotype) / 2, Math.Min(w, header) / 2);
                Path(s, $"M{x + round} {y}H{x + w - round}Q{x + w} {y} {x + w} {y + round}V{y + header}H{x}V{y + round}Q{x} {y} {x + round} {y}Z", headerFill, "none");
            }
            if (n.IsType && !graph.HideCircle && n.Kind is not ("object" or "map"))
            {
                var (glyph, badge) = TypeBadge(n.Kind);
                int spotX = x + n.SpotRadius + 7, spotY = y + header / 2;
                string spot = SpotCategory(n.Kind);
                s.Append(CultureInfo.InvariantCulture, $"<g data-style-name=\"{spot}\" data-style-stereotype=\"{SvgText.Escape(n.Stereotype)}\"><circle cx=\"{spotX}\" cy=\"{spotY}\" r=\"{n.SpotRadius}\" fill=\"{badge}\" stroke=\"{Ink}\"/>");
                Text(s, spotX, spotY + n.SpotSize / 3, glyph, n.SpotSize, bold: true, color:defaultFont);
                s.Append("</g>"); labelX += n.SpotRadius + 1;
            }
            if (n.Members.Count != 0)
            {
                Outline( $"M{x} {y + header}h{w}");
                int keyWidth = n.Kind == "map" ? n.Members.Max(m => SvgText.Measure(m.Text[..m.Text.IndexOf("=>", StringComparison.Ordinal)].Trim(), n.MemberSize)) + 18 : 0;
                if (keyWidth > 0) Outline( $"M{x + keyWidth} {y + header}V{y + h}");
                int memberTop = y + header + 6;
                for (int i = 0; i < n.Members.Count; i++)
                {
                    var m = n.Members[i];
                    s.Append(CultureInfo.InvariantCulture, $"<g class=\"member\" data-source-line=\"{m.Line}\"><title>{SvgText.Escape(m.Text)}</title>");
                    int baseline = memberTop + n.MemberSize;
                    if (keyWidth > 0)
                    {
                        string[] pair = m.Text.Split("=>", StringSplitOptions.TrimEntries);
                        Text(s, x + 8, baseline, pair[0], n.MemberSize, "start", color:memberFont); Text(s, x + keyWidth + 8, baseline, pair[1], n.MemberSize, "start", color:memberFont);
                    }
                    else
                    {
                        int rowTop = memberTop + n.MemberSize - SvgText.Ascent("M", n.MemberSize), memberX = x + 10;
                        if (graph.Skin.VisibilityIconSize > 0 && m.Text.Length > 0 && m.Text[0] is '+' or '-' or '#' or '~')
                        {
                            DrawVisibility(s, memberX, memberTop + (n.MemberSize - graph.Skin.VisibilityIconSize) / 2, graph.Skin.VisibilityIconSize, m.Text[0]);
                            memberX += graph.Skin.VisibilityIconSize + 6;
                        }
                        foreach (string row in n.MemberRows[i])
                        {
                            string rowAnchor = SvgText.Alignment(row, "start");
                            int rowX = rowAnchor == "end" ? x + w - 10 : rowAnchor == "middle" ? (memberX + x + w - 10) / 2 : memberX;
                            Text(s, rowX, rowTop + SvgText.Ascent(row, n.MemberSize), row, n.MemberSize, rowAnchor, color:memberFont);
                            rowTop += SvgText.Height(row, n.MemberSize) + 4;
                        }
                    }
                    memberTop += n.MemberHeights[i];
                    s.Append("</g>");
                }
            }
        }
        if (n.Label.Length != 0 && !n.IsSpecial)
        {
            int ascent = SvgText.Ascent("M", n.LabelSize);
            bool fixedTop = n.Kind is "actor" or "actor/" or "package" or "namespace" or "folder" or "boundary" or "control" or "circle" || n.DeploymentGlyph;
            labelY += headerCard ? ascent - n.LabelSize + 2 : fixedTop ?
                n.LabelHeight - n.LabelSize + ascent - 12 : (n.LabelHeight - n.LabelSize) / 2 + ascent - n.LabelSize / 2 - 5;
            if (n.Stereotype.Length != 0 && graph.Skin.Style(scope, "showstereotype", "true", n.Stereotype) != "false")
            {
                if (!headerCard) labelY += fixedTop ? n.StereotypeSize + 6 : (n.StereotypeSize + 6) / 2;
                string stereoAlignment = graph.Skin.Style(scope, "horizontalalignment", "center", n.Stereotype, "stereotype");
                string stereoAnchor = stereoAlignment == "left" ? "start" : stereoAlignment == "right" ? "end" : "middle";
                int stereoX = stereoAnchor == "start" ? x + n.Padding.Left : stereoAnchor == "end" ? x + w - n.Padding.Right : labelX;
                int stereoY = labelY - n.LabelHeight + n.LabelSize - ascent - 6 - n.StereotypeSize + SvgText.Ascent("M", n.StereotypeSize);
                Text(s, stereoX, stereoY, ManagedSkin.StereotypeLabel(n.Stereotype), n.StereotypeSize, stereoAnchor, color:stereotypeFont, part:"stereotype");
            }
            string alignment = graph.Skin.Style(scope, "horizontalalignment", "center", n.Stereotype);
            string anchor = alignment == "left" ? "start" : alignment == "right" ? "end" : "middle";
            if (alignment == "left") labelX = x + n.Padding.Left + (n.IsType && !graph.HideCircle ? n.SpotRadius * 2 + 4 : 0);
            else if (alignment == "right") labelX = x + w - n.Padding.Right;
            int rowTop = labelY - n.LabelHeight + n.LabelSize - SvgText.Ascent("M", n.LabelSize);
            foreach (string row in n.LabelRows)
            {
                string rowAnchor = SvgText.Alignment(row, anchor);
                int rowX = rowAnchor == "start" ? x + n.Padding.Left + (n.IsType && !graph.HideCircle ? n.SpotRadius * 2 + 4 : 0) : rowAnchor == "end" ? x + w - n.Padding.Right : labelX;
                Text(s, rowX, rowTop + SvgText.Ascent(row, n.LabelSize), row, n.LabelSize, rowAnchor, color:labelFont,
                    italic: n.Kind is "abstract class" or "abstract", underline: n.Kind == "object", part:n.IsType ? "header" : "");
                rowTop += SvgText.Height(row, n.LabelSize) + 4;
            }
        }
        DrawArchimateGlyph(s, n, labelFont);
        s.Append("</g>");

        void Outline(FormattableString path, string shade = "none") => Path(s, path, shade, stroke);
        void Box(int bx, int by, int bw, int bh, int radius, string shade) => Rect(s, bx, by, bw, bh, radius, shade, stroke);
    }

    private static void DrawVisibility(StringBuilder svg, int x, int y, int size, char marker)
    {
        string kind = marker switch { '+' => "public", '-' => "private", '#' => "protected", _ => "package" };
        string color = marker switch { '+' => "#038048", '-' => "#c82930", '#' => "#b38d22", _ => "#1963a0" };
        svg.Append($"<g data-style-name=\"visibilityicon {kind}\">");
        if (marker == '+') svg.Append(CultureInfo.InvariantCulture, $"<ellipse cx=\"{x + size / 2.0:0.###}\" cy=\"{y + size / 2.0:0.###}\" rx=\"{size / 2.0:0.###}\" ry=\"{size / 2.0:0.###}\" fill=\"{color}\" stroke=\"{color}\"/>");
        else if (marker == '-') Rect(svg, x, y, size, size, 0, color, color);
        else if (marker == '#') Path(svg, $"M{x + size / 2} {y}L{x + size} {y + size / 2}L{x + size / 2} {y + size}L{x} {y + size / 2}Z", color, color);
        else Path(svg, $"M{x + size / 2} {y}L{x + size} {y + size}H{x}Z", color, color);
        svg.Append("</g>");
    }

    private static void Text(StringBuilder s, int x, int y, string text, int size, string anchor = "middle", bool bold = false, bool italic = false, bool underline = false, string color = "#232333", string part = "") =>
        SvgText.Append(s, x, y, text, size, anchor, bold, italic, underline, color, part);

    private static void Path(StringBuilder s, FormattableString d, string fill = "none", string stroke = Ink) => s.Append(CultureInfo.InvariantCulture, $"<path d=\"{d.ToString(CultureInfo.InvariantCulture)}\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-linejoin=\"round\"/>");
    private static void Rect(StringBuilder s, int x, int y, int w, int h, int round, string fill, string stroke = Ink) =>
        s.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" rx=\"{round}\" fill=\"{fill}\" stroke=\"{stroke}\"/>");

    private static List<Route> RouteEdges(Graph graph)
    {
        var result = new List<Route>();
        var counts = new Dictionary<(Node, Node), int>();
        var occupied = new ObstacleMap(graph.Nodes);
        foreach (var e in graph.Edges)
        {
            if (e.Hidden) continue;
            Node a = e.From, b = e.To;
            var pair = a.Line <= b.Line ? (a, b) : (b, a);
            int lane = counts.GetValueOrDefault(pair); counts[pair] = lane + 1;
            int offset = lane == 0 ? 0 : (lane % 2 == 0 ? -1 : 1) * ((lane + 1) / 2) * 8;
            Point[] points;
            if (a == b)
            {
                int right = a.X + a.Width + 24 + lane * 10, mid = a.Y + a.Height / 2;
                points = [new(a.X + a.Width, mid + 6), new(right, mid + 6), new(right, a.Y - 12), new(a.X + a.Width / 2, a.Y - 12), new(a.X + a.Width / 2, a.Y)];
            }
            else
            {
                // Route through the free channel between boxes, then test rectangular detours if a rank intervenes.
                int midAx = a.X + a.Width / 2, midAy = a.Y + a.Height / 2, midBx = b.X + b.Width / 2, midBy = b.Y + b.Height / 2;
                bool apartY = a.Y + a.Height <= b.Y || b.Y + b.Height <= a.Y, apartX = a.X + a.Width <= b.X || b.X + b.Width <= a.X;
                bool across = apartX && (!apartY || graph.Horizontal);
                int side = across ? Math.Sign(midBx - midAx) : Math.Sign(midBy - midAy);
                Point start = across ? new(side > 0 ? a.X + a.Width : a.X, midAy + Math.Clamp(offset, -a.Height / 3, a.Height / 3)) : new(midAx + Math.Clamp(offset, -a.Width / 3, a.Width / 3), side > 0 ? a.Y + a.Height : a.Y);
                Point end = across ? new(side > 0 ? b.X : b.X + b.Width, midBy + Math.Clamp(offset, -b.Height / 3, b.Height / 3)) : new(midBx + Math.Clamp(offset, -b.Width / 3, b.Width / 3), side > 0 ? b.Y : b.Y + b.Height);
                int mid = across ? (start.X + end.X) / 2 + offset : (start.Y + end.Y) / 2 + offset;
                points = across ? [start, new(mid, start.Y), new(mid, end.Y), end] : [start, new(start.X, mid), new(end.X, mid), end];
                if (occupied.Blocked(points, a, b))
                {
                    // Use ports facing an outside corridor to avoid crossing intermediate entities.
                    int[] corridors = across
                        ? [Math.Min(a.Y, b.Y) - 22 - Math.Abs(offset), Math.Max(a.Y + a.Height, b.Y + b.Height) + 22 + Math.Abs(offset), 6 - Math.Abs(offset), graph.Root.Height + 92 + Math.Abs(offset)]
                        : [Math.Min(a.X, b.X) - 22 - Math.Abs(offset), Math.Max(a.X + a.Width, b.X + b.Width) + 22 + Math.Abs(offset), 8 - Math.Abs(offset), graph.Root.Width + 84 + Math.Abs(offset)];
                    foreach (int corridor in corridors)
                    {
                        var candidate = across ? new Point[] { new(midAx, corridor < a.Y ? a.Y : a.Y + a.Height), new(midAx, corridor), new(midBx, corridor), new(midBx, corridor < b.Y ? b.Y : b.Y + b.Height) }
                            : [new(corridor < a.X ? a.X : a.X + a.Width, midAy), new(corridor, midAy), new(corridor, midBy), new(corridor < b.X ? b.X : b.X + b.Width, midBy)];
                        if (!occupied.Blocked(candidate, a, b)) { points = candidate; break; }
                    }
                }
            }
            result.Add(new(e, points.Where((p, i) => i == 0 || p != points[i - 1]).ToArray()));
        }
        return result;
    }

    // The index answers axis-aligned segment queries without scanning all nodes per edge.
    private sealed class ObstacleMap
    {
        private readonly Dictionary<int, List<Node>> rows = [], cols = [];
        internal ObstacleMap(List<Node> nodes)
        {
            foreach (var n in nodes.Where(n => n.Children.Count == 0))
            {
                Add(rows, n.Y - 4, n.Y + n.Height + 4, n); Add(cols, n.X - 4, n.X + n.Width + 4, n);
            }
        }
        private static void Add(Dictionary<int, List<Node>> bins, int lo, int hi, Node node)
        {
            for (int i = lo / 64; i <= hi / 64; i++)
            {
                if (!bins.TryGetValue(i, out var list)) bins[i] = list = [];
                list.Add(node);
            }
        }
        internal bool Blocked(Point[] path, Node from, Node to, bool includeEndpoints = false)
        {
            for (int i = 1; i < path.Length; i++)
            {
                Point a = path[i - 1], b = path[i];
                bool horizontal = a.Y == b.Y;
                var bins = horizontal ? rows : cols;
                if (!bins.TryGetValue((horizontal ? a.Y : a.X) / 64, out var nodes)) continue;
                foreach (var n in nodes)
                {
                    if (!includeEndpoints && (n == from || n == to)) continue;
                    if (Math.Min(a.X, b.X) < n.X + n.Width + 4 && Math.Max(a.X, b.X) > n.X - 4 &&
                        Math.Min(a.Y, b.Y) < n.Y + n.Height + 4 && Math.Max(a.Y, b.Y) > n.Y - 4) return true;
                }
            }
            return false;
        }
    }

    private static (int Min, int Max, int Top, int Bottom) DrawEdge(StringBuilder s, Route route, ManagedSkin skin)
    {
        var e = route.Edge; var p = route.Points;
        string stroke = e.Paint?.Stroke ?? skin.Get("arrowcolor",Ink);
        string weight = e.Paint?.Thickness ?? skin.Get("arrowthickness","1.4");
        string dashes = e.Paint?.Dashes ?? (e.Arrow.Contains('.') ? "5 4" : "");
        string font = skin.Get("arrowfontcolor",skin.Get("defaultfontcolor","#232333"));
        s.Append(CultureInfo.InvariantCulture, $"<g class=\"link\" data-style-name=\"arrow\" data-source-line=\"{e.Line}\"{(e.Paint?.Stroke == null ? "" : " data-style-linecolor=\"explicit\"")}{(e.Paint?.Thickness == null ? "" : " data-style-linethickness=\"explicit\"")}{(e.Paint?.Dashes == null ? "" : " data-style-linestyle=\"explicit\"")}><title>{SvgText.Escape(e.Caption.Length == 0 ? e.From.Label + " to " + e.To.Label : e.Caption)}</title>");
        string points = string.Join(" ", p.Select(v => FormattableString.Invariant($"{v.X},{v.Y}")));
        s.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{points}\" fill=\"none\" stroke=\"transparent\" stroke-width=\"9\"/>");
        if (skin.LineType == "curve") s.Append(CultureInfo.InvariantCulture, $"<path d=\"{RoundedRoute(p)}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"{weight}\"");
        else s.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{points}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"{weight}\"");
        if (dashes.Length != 0) s.Append(" stroke-dasharray=\"").Append(dashes).Append('"');
        s.Append("/>");
        if (p.Length < 2) { s.Append("</g>"); return (0, 0, 0, 0); }
        Mark(s, p[0], p[1], e.Arrow, true,stroke); Mark(s, p[^1], p[^2], e.Arrow, false,stroke);
        int left = 0, right = 0, top = 0, bottom = 0;
        void Caption(string label, int x, int y, int size, bool back)
        {
            if (label.Length == 0) return;
            var padding = StyleSpacing.Parse(skin.Style("arrow", "padding", "0"));
            var margin = StyleSpacing.Parse(skin.Style("arrow", "margin", "0"));
            int maximum = skin.Number("arrow", "maximumwidth", 0);
            var rows = StyleText.Wrap(label, size, maximum == 0 ? 0 : Math.Max(size, maximum - padding.Horizontal));
            int width = rows.Max(row => SvgText.Measure(row, size)) + 6 + padding.Horizontal;
            int height = StyleText.Height(rows, size);
            y -= margin.Bottom;
            if (back) s.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x - width / 2}\" y=\"{y - height - padding.Top}\" width=\"{width}\" height=\"{height + 3 + padding.Vertical}\" fill=\"{skin.Get("backgroundcolor","white")}\" fill-opacity=\"0.9\"/>");
            int rowTop = y - height + size - SvgText.Ascent("M", size);
            foreach (string row in rows)
            {
                string anchor = SvgText.Alignment(row, "middle");
                int rowX = anchor == "start" ? x - width / 2 + padding.Left + 3 : anchor == "end" ? x + width / 2 - padding.Right - 3 : x;
                Text(s, rowX, rowTop + SvgText.Ascent(row, size), row, size, anchor, color: font);
                rowTop += SvgText.Height(row, size) + 4;
            }
            left = Math.Min(left, x - width / 2 - 4 - margin.Left); right = Math.Max(right, x + width / 2 + 4 + margin.Right);
            top = Math.Min(top, y - height - padding.Top - margin.Top); bottom = Math.Max(bottom, y + 3 + padding.Bottom + margin.Bottom);
        }
        var (labelX, labelY) = Center(p);
        Caption(e.Caption, labelX, labelY - 4, skin.Size("arrowfontsize",13), true);
        Caption(e.Tail, p[0].X + Math.Sign(p[1].X - p[0].X) * 18 + (p[0].X == p[1].X ? 14 : 0), p[0].Y + Math.Sign(p[1].Y - p[0].Y) * 14 - 3, 11, false);
        Caption(e.Head, p[^1].X + Math.Sign(p[^2].X - p[^1].X) * 18 + (p[^1].X == p[^2].X ? 14 : 0), p[^1].Y + Math.Sign(p[^2].Y - p[^1].Y) * 14 - 3, 11, false);
        s.Append("</g>"); return (left, right, top, bottom);
    }

    private static string RoundedRoute(Point[] points)
    {
        if (points.Length == 0) return "";
        var path = new StringBuilder();
        path.Append(CultureInfo.InvariantCulture, $"M{points[0].X} {points[0].Y}");
        for (int i = 1; i + 1 < points.Length; i++)
        {
            Point previous = points[i - 1], corner = points[i], next = points[i + 1];
            int before = Math.Min(10, (Math.Abs(corner.X - previous.X) + Math.Abs(corner.Y - previous.Y)) / 2);
            int after = Math.Min(10, (Math.Abs(next.X - corner.X) + Math.Abs(next.Y - corner.Y)) / 2);
            path.Append(CultureInfo.InvariantCulture, $"L{corner.X - Math.Sign(corner.X - previous.X) * before} {corner.Y - Math.Sign(corner.Y - previous.Y) * before}Q{corner.X} {corner.Y} {corner.X + Math.Sign(next.X - corner.X) * after} {corner.Y + Math.Sign(next.Y - corner.Y) * after}");
        }
        if (points.Length > 1) path.Append(CultureInfo.InvariantCulture, $"L{points[^1].X} {points[^1].Y}");
        return path.ToString();
    }

    private static Point Center(Point[] points)
    {
        static int Distance(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
        int rest = 0;
        for (int i = 1; i < points.Length; i++) rest += Distance(points[i - 1], points[i]);
        rest /= 2;
        for (int i = 1; i < points.Length; i++)
        {
            var a = points[i - 1]; var b = points[i]; int length = Distance(a, b);
            if (rest <= length) return new(a.X + Math.Sign(b.X - a.X) * rest, a.Y + Math.Sign(b.Y - a.Y) * rest);
            rest -= length;
        }
        return points[^1];
    }

    private static void Mark(StringBuilder s, Point tip, Point away, string arrow, bool start, string stroke)
    {
        char token = start ? arrow[0] : arrow[^1]; bool triangle = start ? arrow.StartsWith("<|") : arrow.EndsWith("|>");
        if (token is not ('*' or 'o' or '<' or '>')) return;
        int dx = Math.Sign(away.X - tip.X), dy = Math.Sign(away.Y - tip.Y);
        string P(int along, int side) => FormattableString.Invariant($"{tip.X + along * dx - side * dy} {tip.Y + along * dy + side * dx}");
        bool hollow = token == 'o' || triangle;
        if (hollow) s.Append("<g data-style-name=\"arrow\" data-style-fill=\"explicit\">");
        if (token is '*' or 'o') Path(s, $"M{P(0, 0)}L{P(7, 4)}L{P(14, 0)}L{P(7, -4)}Z", token == '*' ? stroke : "white", stroke);
        else if (triangle) Path(s, $"M{P(0, 0)}L{P(12, 6)}L{P(12, -6)}Z", "white", stroke);
        else if (arrow.StartsWith("<<", StringComparison.Ordinal) || arrow.EndsWith(">>", StringComparison.Ordinal)) Path(s, $"M{P(9, 4)}L{P(0, 0)}L{P(9, -4)}", "none", stroke);
        else Path(s, $"M{P(9, 4)}L{P(0, 0)}L{P(9, -4)}Z", stroke,stroke);
        if (hollow) s.Append("</g>");
    }
}

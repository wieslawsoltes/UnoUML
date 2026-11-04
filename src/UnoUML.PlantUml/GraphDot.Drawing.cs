using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

internal static partial class GraphRenderer
{
    private sealed class DotBounds
    {
        internal int Left = int.MaxValue, Top = int.MaxValue, Right = int.MinValue, Bottom = int.MinValue;
        internal void Add(int x, int y, int width, int height)
        {
            Left = Math.Min(Left, x); Top = Math.Min(Top, y); Right = Math.Max(Right, x + width); Bottom = Math.Max(Bottom, y + height);
        }
        internal void Add(DotLabel? label)
        {
            if (label == null) return;
            if (label.Width != 0 || label.Height != 0)
                Add(label.X + Math.Min(0, (label.ActualWidth - label.Width) / 2), label.Y + Math.Min(0, (label.ActualHeight - label.Height) / 2), Math.Max(label.Width, label.ActualWidth), Math.Max(label.Height, label.ActualHeight));
            if (label is DotCellLabel cell) Add(cell.Content);
            else if (label is DotRecordLabel record) foreach (var child in record.Children) Add(child);
            else if (label is DotTableLabel table) foreach (var child in table.Cells) Add(child);
        }
    }

    private static string? DrawDot(DotDocument dot)
    {
        var body = new StringBuilder(dot.Nodes.Count * 320);
        var bounds = new DotBounds();
        int paintId = 0;
        foreach (var scope in dot.Scopes.Where(scope => scope.IsCluster && !scope.Visual!.Invisible))
        {
            var box = scope.Box!; var visual = scope.Visual!;
            DotGroupStart(body, "cluster", "cluster:" + scope.Id, scope.Id, scope.Span, scope.Insert, scope.LabelToken, visual, true, canvasParent: DotClusterParent(scope.Parent)?.Id);
            bool link = DotLinkStart(body, visual.Link, visual.Target, visual.Tooltip);
            DrawDotShape(body, visual, box.X, box.Y, box.Width, box.Height, ref paintId);
            visual.Label.Draw(body);
            if (link) body.Append("</a>");
            body.Append("</g>");
            int strokeMargin = (int)Math.Ceiling(visual.PenWidth / 2) + 1;
            bounds.Add(box.X - strokeMargin, box.Y - strokeMargin, box.Width + strokeMargin * 2, box.Height + strokeMargin * 2); bounds.Add(visual.Label);
        }
        if (dot.Splines is not ("none" or ""))
        {
            var obstacles = new ObstacleMap(dot.Graph.Nodes);
            foreach (var route in RouteEdges(dot.Graph))
            {
                var edge = dot.EdgeMap[route.Edge];
                var points = DotRoute(dot, edge, route.Points, obstacles);
                DrawDotEdge(body, dot, edge, points, bounds, ref paintId);
            }
        }
        foreach (var node in dot.Nodes.Values)
        {
            var visual = node.Visual;
            if (visual.Invisible) continue;
            var box = DotShapeBox(node);
            DotGroupStart(body, "node", "node:" + node.Id, node.Id, node.Span, node.Insert, node.LabelToken, visual, node.Explicit, nameToken: node.NameToken,
                canvasParent: dot.Scopes.FirstOrDefault(scope => scope.IsCluster && ReferenceEquals(scope.Box, node.Box.Parent))?.Id);
            bool link = DotLinkStart(body, visual.Link, visual.Target, visual.Tooltip);
            DrawDotShape(body, visual, box.X, box.Y, box.Width, box.Height, ref paintId);
            visual.Image?.Draw(body);
            if (visual.Shape != "point") visual.Label.Draw(body);
            visual.External?.Draw(body);
            if (link) body.Append("</a>");
            body.Append("</g>");
            int strokeMargin = (int)Math.Ceiling(visual.PenWidth / 2) + 1;
            bounds.Add(box.X - strokeMargin, box.Y - strokeMargin, box.Width + strokeMargin * 2, box.Height + strokeMargin * 2);
            if (visual.Shape != "point") bounds.Add(visual.Label);
            bounds.Add(visual.Image);
            bounds.Add(visual.External);
        }
        if (bounds.Left == int.MaxValue) bounds.Add(0, 0, 64, 48);
        var rootVisual = dot.Root.Visual!;
        var heading = rootVisual.Label;
        if (heading.Width != 0 || heading.Height != 0)
        {
            int center = (bounds.Left + bounds.Right) / 2;
            int x = rootVisual.Justify == "l" ? bounds.Left : rootVisual.Justify == "r" ? bounds.Right - heading.Width : center - heading.Width / 2;
            int y = dot.Root.Attributes.Get("labelloc", "b") == "t" ? bounds.Top - heading.Height - 8 : bounds.Bottom + 8;
            heading.Place(x, y, heading.Width, heading.Height);
            DotGroupStart(body, "graph", "graph:" + dot.Root.Id, dot.Root.Id, dot.Root.Span, dot.Root.Insert, dot.Root.LabelToken, rootVisual, true);
            heading.Draw(body); body.Append("</g>"); bounds.Add(heading);
        }
        int pad = dot.Padding;
        bounds.Add(bounds.Left - pad, bounds.Top - pad, bounds.Right - bounds.Left + pad * 2, bounds.Bottom - bounds.Top + pad * 2);
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        if (width > 32768 || height > 32768) return null;
        var svg = new StringBuilder(body.Length + 300);
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width * dot.Scale:0.###}\" height=\"{height * dot.Scale:0.###}\" viewBox=\"{bounds.Left} {bounds.Top} {width} {height}\" data-dot-directed=\"{(dot.Directed ? "true" : "false")}\">");
        string background = DotFill(dot.Root.Attributes.Get("bgcolor", "white"), dot.Root.Attributes.Get("colorscheme"));
        background = DotPaint(svg, background, rootVisual.GradientAngle, rootVisual.Style.Contains("radial", StringComparison.Ordinal), ref paintId);
        if (background != "none") svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{bounds.Left}\" y=\"{bounds.Top}\" width=\"{width}\" height=\"{height}\" fill=\"{background}\"/>");
        return svg.Append(body).Append("</svg>").ToString();
    }

    private static void DotGroupStart(StringBuilder svg, string kind, string id, string name, DotSpan span, int insert, DotToken? label, DotVisual visual, bool explicitNode, DotEdge? edge = null, DotToken? nameToken = null, string? canvasParent = null)
    {
        string cssClass = kind == "node" ? "entity" : kind == "edge" ? "link" : kind;
        if (visual.Class.Length != 0) cssClass += " " + visual.Class;
        svg.Append(CultureInfo.InvariantCulture, $"<g class=\"{SvgText.Escape(cssClass)}\" data-dot-kind=\"{kind}\" data-dot-id=\"{DotAttribute(id)}\" data-qualified-name=\"{DotAttribute(name)}\" data-source-line=\"{span.Line}\" data-source-end-line=\"{span.EndLine}\" data-dot-start=\"{span.Start}\" data-dot-length=\"{span.End - span.Start}\" data-dot-explicit=\"{(explicitNode ? "true" : "false")}\" data-dot-label-text=\"{DotAttribute(visual.Label.Plain)}\" data-dot-label-format=\"{visual.LabelFormat}\"");
        if (kind != "graph") svg.Append(" data-canvas-id=\"").Append(DotAttribute(id)).Append('"');
        if (canvasParent != null) svg.Append(" data-canvas-parent=\"cluster:").Append(DotAttribute(canvasParent)).Append('"');
        if (nameToken is { } identifier) svg.Append(CultureInfo.InvariantCulture, $" data-dot-name-start=\"{identifier.Start}\" data-dot-name-length=\"{identifier.Length}\"");
        if (visual.LabelShared) svg.Append(" data-dot-label-shared=\"true\"");
        if (visual.SvgId.Length != 0) svg.Append(" id=\"").Append(SvgText.Escape(visual.SvgId)).Append('"');
        if (label is { Start: >= 0 } token)
            svg.Append(CultureInfo.InvariantCulture, $" data-dot-label-start=\"{token.Start}\" data-dot-label-length=\"{token.Length}\" data-dot-label-kind=\"{(token.Kind == DotTokenKind.Html ? "html" : token.Kind == DotTokenKind.Quoted ? "quoted" : "id")}\"");
        else svg.Append(CultureInfo.InvariantCulture, $" data-dot-label-insert=\"{insert}\"");
        if (edge != null)
        {
            if (edge.Merged) svg.Append(" data-dot-merged=\"true\"");
            svg.Append(CultureInfo.InvariantCulture, $" data-dot-from-id=\"{DotAttribute(edge.From.Node.Id)}\" data-dot-to-id=\"{DotAttribute(edge.To.Node.Id)}\"");
            if (edge.From.Literal) svg.Append(CultureInfo.InvariantCulture, $" data-dot-from-start=\"{edge.From.Span.Start}\" data-dot-from-length=\"{edge.From.Span.End - edge.From.Span.Start}\"");
            if (edge.To.Literal) svg.Append(CultureInfo.InvariantCulture, $" data-dot-to-start=\"{edge.To.Span.Start}\" data-dot-to-length=\"{edge.To.Span.End - edge.To.Span.Start}\"");
            svg.Append(CultureInfo.InvariantCulture, $" data-dot-arrow-start=\"{edge.Arrow.Start}\" data-dot-arrow-length=\"{edge.Arrow.Length}\"");
        }
        svg.Append('>').Append("<title>").Append(SvgText.Escape(visual.Tooltip.Length == 0 ? name : visual.Tooltip)).Append("</title>");
    }

    private static string DotAttribute(string value) => SvgText.Escape(value).Replace("\r", "&#13;", StringComparison.Ordinal).Replace("\n", "&#10;", StringComparison.Ordinal).Replace("\t", "&#9;", StringComparison.Ordinal);

    private static bool DotLinkStart(StringBuilder svg, string link, string target, string tooltip)
    {
        if (link.Length == 0) return false;
        string normalized = link.Trim();
        if (normalized.Any(char.IsControl) || normalized.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) throw InvalidDot();
        svg.Append("<a href=\"").Append(SvgText.Escape(normalized)).Append('"');
        if (target.Length != 0) svg.Append(" target=\"").Append(SvgText.Escape(target)).Append('"');
        svg.Append('>');
        if (tooltip.Length != 0) svg.Append("<title>").Append(SvgText.Escape(tooltip)).Append("</title>");
        return true;
    }

    private static DotRect DotShapeBox(DotNode node)
    {
        var visual = node.Visual;
        int contentHeight = node.Box.Height - (visual.External?.Height + 4 ?? 0);
        return new(node.Box.X + (node.Box.Width - visual.ShapeWidth) / 2, node.Box.Y + (contentHeight - visual.ShapeHeight) / 2, visual.ShapeWidth, visual.ShapeHeight);
    }

    private static string DotPaint(StringBuilder svg, string fill, double angle, bool radial, ref int serial)
    {
        int separator = fill.IndexOf(':');
        if (separator < 0) return fill;
        string id = "dot-gradient-" + serial++;
        svg.Append("<defs>");
        if (radial)
        {
            svg.Append(CultureInfo.InvariantCulture, $"<radialGradient id=\"{id}\" cx=\"50%\" cy=\"50%\" r=\"70%\"");
            if (angle != 0) svg.Append(CultureInfo.InvariantCulture, $" fx=\"{50 + Math.Cos(angle * Math.PI / 180) * 35:0.###}%\" fy=\"{50 - Math.Sin(angle * Math.PI / 180) * 35:0.###}%\"");
            svg.Append('>');
        }
        else
        {
            double dx = Math.Cos(angle * Math.PI / 180) * 50, dy = -Math.Sin(angle * Math.PI / 180) * 50;
            svg.Append(CultureInfo.InvariantCulture, $"<linearGradient id=\"{id}\" x1=\"{50 - dx:0.###}%\" y1=\"{50 - dy:0.###}%\" x2=\"{50 + dx:0.###}%\" y2=\"{50 + dy:0.###}%\">");
        }
        svg.Append("<stop offset=\"0\" stop-color=\"").Append(fill[..separator]).Append("\"/><stop offset=\"1\" stop-color=\"").Append(fill[(separator + 1)..]).Append("\"/>");
        svg.Append(radial ? "</radialGradient>" : "</linearGradient>").Append("</defs>");
        return "url(#" + id + ")";
    }

    private static void DrawDotShape(StringBuilder svg, DotVisual visual, int x, int y, int width, int height, ref int paintId)
    {
        if (visual.Shape is "none" or "plain" or "plaintext") return;
        string fill = DotPaint(svg, visual.Fill, visual.GradientAngle, visual.Style.Contains("radial", StringComparison.Ordinal), ref paintId);
        string stroke = visual.Peripheries == 0 ? "none" : visual.Stroke;
        svg.Append(CultureInfo.InvariantCulture, $"<g fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"{visual.PenWidth:0.###}\" stroke-linejoin=\"round\"");
        if (visual.Dashes.Length != 0) svg.Append(" stroke-dasharray=\"").Append(visual.Dashes).Append('"');
        svg.Append('>');
        int rings = Math.Max(1, visual.Peripheries);
        for (int ring = 0; ring < rings; ring++)
        {
            int inset = ring * 4;
            int left = x + inset, top = y + inset, w = Math.Max(1, width - inset * 2), h = Math.Max(1, height - inset * 2);
            if (ring != 0) svg.Append("<g fill=\"none\">");
            switch (visual.Shape)
            {
                case "ellipse": case "oval": case "circle": case "doublecircle": case "Mcircle": case "point":
                    svg.Append(CultureInfo.InvariantCulture, $"<ellipse cx=\"{left + w / 2.0:0.###}\" cy=\"{top + h / 2.0:0.###}\" rx=\"{w / 2.0:0.###}\" ry=\"{h / 2.0:0.###}\"/>");
                    break;
                case "egg":
                    svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{left + w / 2.0:0.###} {top}C{left - w * .12:0.###} {top} {left - w * .05:0.###} {top + h:0.###} {left + w / 2.0:0.###} {top + h}C{left + w * 1.05:0.###} {top + h:0.###} {left + w * 1.12:0.###} {top} {left + w / 2.0:0.###} {top}Z\"/>");
                    break;
                case "box": case "rect": case "rectangle": case "square": case "Msquare": case "record": case "Mrecord":
                    svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{left}\" y=\"{top}\" width=\"{w}\" height=\"{h}\" rx=\"{(visual.Rounded ? Math.Min(12, Math.Min(w, h) / 4) : 0)}\"/>");
                    break;
                case "underline":
                    svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{left} {top + h}h{w}\" fill=\"none\"/>");
                    break;
                case "note":
                    svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{left} {top}H{left + w - 10}L{left + w} {top + 10}V{top + h}H{left}Z M{left + w - 10} {top}V{top + 10}H{left + w}\"/>");
                    break;
                case "tab": case "folder":
                    svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{left} {top + 8}V{top}H{left + w * .45:0.###}L{left + w * .45 + 8:0.###} {top + 8}H{left + w}V{top + h}H{left}Z\"/>");
                    break;
                case "box3d":
                    svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{left} {top + 7}L{left + 7} {top}H{left + w}V{top + h - 7}L{left + w - 7} {top + h}H{left}Z M{left} {top + 7}H{left + w - 7}L{left + w} {top} M{left + w - 7} {top + 7}V{top + h}\"/>");
                    break;
                case "component":
                    svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{left + 5}\" y=\"{top}\" width=\"{Math.Max(1, w - 5)}\" height=\"{h}\"/><rect x=\"{left}\" y=\"{top + h / 4}\" width=\"10\" height=\"{Math.Max(4, h / 6)}\"/><rect x=\"{left}\" y=\"{top + h * 2 / 3}\" width=\"10\" height=\"{Math.Max(4, h / 6)}\"/>");
                    break;
                case "cylinder":
                    int cap = Math.Min(12, h / 4);
                    svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{left} {top + cap}C{left} {top - cap / 2} {left + w} {top - cap / 2} {left + w} {top + cap}V{top + h - cap}C{left + w} {top + h + cap / 2} {left} {top + h + cap / 2} {left} {top + h - cap}Z M{left} {top + cap}C{left} {top + cap * 5 / 2} {left + w} {top + cap * 5 / 2} {left + w} {top + cap}\"/>");
                    break;
                default:
                    var polygon = DotPolygon(visual, left, top, w, h);
                    if (visual.Rounded) svg.Append("<path d=\"").Append(DotRoundedPolygon(polygon)).Append("\"/>");
                    else svg.Append("<polygon points=\"").Append(string.Join(' ', polygon.Select(point => FormattableString.Invariant($"{point.X:0.###},{point.Y:0.###}")))).Append("\"/>");
                    break;
            }
            if (ring != 0) svg.Append("</g>");
        }
        if (visual.Diagonals)
        {
            if (visual.Shape is "ellipse" or "oval" or "circle" or "doublecircle" or "Mcircle")
            {
                double left = x + width * .143, length = width * .714;
                svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{left:0.###} {y + height * .15:0.###}h{length:0.###} M{left:0.###} {y + height * .85:0.###}h{length:0.###}\" fill=\"none\"/>");
            }
            else
            {
                var vertices = visual.Shape is "box" or "rect" or "rectangle" or "square" or "Msquare" or "record" or "Mrecord"
                    ? new (double X, double Y)[] { (x, y), (x + width, y), (x + width, y + height), (x, y + height) }
                    : DotPolygon(visual, x, y, width, height);
                for (int i = 0; i < vertices.Length; i++)
                {
                    var point = vertices[i]; var before = vertices[(i + vertices.Length - 1) % vertices.Length]; var after = vertices[(i + 1) % vertices.Length];
                    svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{point.X + (before.X - point.X) * .18:0.###} {point.Y + (before.Y - point.Y) * .18:0.###}L{point.X + (after.X - point.X) * .18:0.###} {point.Y + (after.Y - point.Y) * .18:0.###}\" fill=\"none\"/>");
                }
            }
        }
        svg.Append("</g>");
    }

    private static (double X, double Y)[] DotPolygon(DotVisual visual, int x, int y, int width, int height)
    {
        (double X, double Y)[] points = visual.Shape switch
        {
            "diamond" or "Mdiamond" => [(0, -.5), (.5, 0), (0, .5), (-.5, 0)],
            "trapezium" => [(-.3, -.5), (.3, -.5), (.5, .5), (-.5, .5)],
            "invtrapezium" => [(-.5, -.5), (.5, -.5), (.3, .5), (-.3, .5)],
            "parallelogram" => [(-.3, -.5), (.5, -.5), (.3, .5), (-.5, .5)],
            "house" => [(0, -.5), (.5, -.2), (.5, .5), (-.5, .5), (-.5, -.2)],
            "invhouse" => [(-.5, -.5), (.5, -.5), (.5, .2), (0, .5), (-.5, .2)],
            _ => []
        };
        if (points.Length == 0)
        {
            int sides = visual.Shape switch { "triangle" or "invtriangle" => 3, "pentagon" => 5, "hexagon" => 6, "septagon" => 7, "octagon" or "doubleoctagon" or "tripleoctagon" => 8, "star" => 10, _ => visual.Sides };
            points = new (double X, double Y)[sides];
            for (int i = 0; i < sides; i++)
            {
                double angle = i * Math.PI * 2 / sides - Math.PI / 2 + (visual.Shape == "invtriangle" ? Math.PI : sides % 2 == 0 && visual.Shape != "star" ? Math.PI / sides : 0);
                double radius = visual.Shape == "star" && i % 2 == 1 ? .21 : .5;
                points[i] = (Math.Cos(angle) * radius, Math.Sin(angle) * radius);
            }
        }
        double rotation = visual.Rotation * Math.PI / 180;
        for (int i = 0; i < points.Length; i++)
        {
            var (px, py) = points[i];
            double skewed = px * (1 + visual.Distortion * py) + visual.Skew * py;
            points[i] = (skewed * Math.Cos(rotation) - py * Math.Sin(rotation), skewed * Math.Sin(rotation) + py * Math.Cos(rotation));
        }
        double minX = points.Min(point => point.X), maxX = points.Max(point => point.X), minY = points.Min(point => point.Y), maxY = points.Max(point => point.Y);
        return points.Select(point => (x + (point.X - minX) / Math.Max(.001, maxX - minX) * width,
            y + (point.Y - minY) / Math.Max(.001, maxY - minY) * height)).ToArray();
    }

    private static string DotRoundedPolygon((double X, double Y)[] vertices)
    {
        var path = new StringBuilder();
        for (int i = 0; i < vertices.Length; i++)
        {
            var point = vertices[i]; var before = vertices[(i + vertices.Length - 1) % vertices.Length]; var after = vertices[(i + 1) % vertices.Length];
            double beforeLength = Math.Sqrt(Math.Pow(before.X - point.X, 2) + Math.Pow(before.Y - point.Y, 2));
            double afterLength = Math.Sqrt(Math.Pow(after.X - point.X, 2) + Math.Pow(after.Y - point.Y, 2));
            double first = Math.Min(.2, 10 / Math.Max(1, beforeLength)), second = Math.Min(.2, 10 / Math.Max(1, afterLength));
            path.Append(CultureInfo.InvariantCulture, $"{(i == 0 ? 'M' : 'L')}{point.X + (before.X - point.X) * first:0.###} {point.Y + (before.Y - point.Y) * first:0.###}Q{point.X:0.###} {point.Y:0.###} {point.X + (after.X - point.X) * second:0.###} {point.Y + (after.Y - point.Y) * second:0.###}");
        }
        return path.Append('Z').ToString();
    }

    private static Point[] DotRoute(DotDocument dot, DotEdge edge, Point[] original, ObstacleMap obstacles)
    {
        if (original.Length < 2) return original;
        var from = DotAttachment(edge.From, edge.Attributes.Get("tailport"), original[1], !edge.Attributes.ContainsKey("tailclip") || DotBool(edge.Attributes.Get("tailclip")));
        var to = DotAttachment(edge.To, edge.Attributes.Get("headport"), original[^2], !edge.Attributes.ContainsKey("headclip") || DotBool(edge.Attributes.Get("headclip")));
        if (DotBool(dot.Root.Attributes.Get("compound")))
        {
            from = DotCompoundPoint(dot, edge.Attributes.Get("ltail"), from, to);
            to = DotCompoundPoint(dot, edge.Attributes.Get("lhead"), to, from);
        }
        if (dot.Splines is "line" or "false") return [from, to];
        bool ports = edge.From.Port.Length != 0 || edge.To.Port.Length != 0 || edge.Attributes.ContainsKey("tailport") || edge.Attributes.ContainsKey("headport");
        if (ports || original.Length <= 4 && edge.From.Node != edge.To.Node)
        {
            Point start = DotPortExit(edge.From, edge.Attributes.Get("tailport"), from), end = DotPortExit(edge.To, edge.Attributes.Get("headport"), to);
            int middle = dot.Graph.Horizontal ? (start.X + end.X) / 2 : (start.Y + end.Y) / 2;
            Point[] inner = dot.Graph.Horizontal ? [start, new(middle, start.Y), new(middle, end.Y), end] : [start, new(start.X, middle), new(end.X, middle), end];
            if (obstacles.Blocked(inner, edge.From.Node.Box, edge.To.Node.Box, true))
            {
                var a = DotShapeBox(edge.From.Node); var b = DotShapeBox(edge.To.Node);
                int[] columns = [Math.Min(a.X, b.X) - 22, Math.Max(a.X + a.Width, b.X + b.Width) + 22, 8, dot.Graph.Root.Width + 56];
                int[] rows = [Math.Min(a.Y, b.Y) - 22, Math.Max(a.Y + a.Height, b.Y + b.Height) + 22, 0, dot.Graph.Root.Height + 48];
                foreach (int column in columns)
                {
                    Point[] candidate = [start, new(column, start.Y), new(column, end.Y), end];
                    if (!obstacles.Blocked(candidate, edge.From.Node.Box, edge.To.Node.Box, true)) { inner = candidate; break; }
                }
                if (obstacles.Blocked(inner, edge.From.Node.Box, edge.To.Node.Box, true))
                    foreach (int row in rows)
                    {
                        Point[] candidate = [start, new(start.X, row), new(end.X, row), end];
                        if (!obstacles.Blocked(candidate, edge.From.Node.Box, edge.To.Node.Box, true)) { inner = candidate; break; }
                    }
            }
            var direct = new[] { from }.Concat(inner).Append(to).ToArray();
            return direct.Where((point, i) => i == 0 || point != direct[i - 1]).ToArray();
        }
        var points = original.ToArray();
        if (points[0].X == points[1].X) points[1] = points[1] with { X = from.X };
        else points[1] = points[1] with { Y = from.Y };
        if (points[^1].X == points[^2].X) points[^2] = points[^2] with { X = to.X };
        else points[^2] = points[^2] with { Y = to.Y };
        points[0] = from; points[^1] = to;
        return points.Where((point, i) => i == 0 || point != points[i - 1]).ToArray();
    }

    private static Point DotPortExit(DotEndpoint endpoint, string attribute, Point point)
    {
        string port = endpoint.Port, compass = endpoint.Compass;
        if (attribute.Length != 0)
        {
            var pieces = attribute.Split(':'); port = pieces[0]; compass = pieces.Length == 2 ? pieces[1] : "";
        }
        var outer = DotShapeBox(endpoint.Node);
        var box = endpoint.Node.Visual.Ports.TryGetValue(port, out var field) ? field : outer;
        if (compass.Length == 0 && DotCompass(port)) compass = port;
        if (compass.Length == 0 || compass is "_" or "c")
        {
            int dx = point.X - box.X - box.Width / 2, dy = point.Y - box.Y - box.Height / 2;
            compass = Math.Abs(dx) * box.Height > Math.Abs(dy) * box.Width ? dx < 0 ? "w" : "e" : dy < 0 ? "n" : "s";
        }
        int x = compass.Contains('w') ? Math.Min(point.X, outer.X) - 12 : compass.Contains('e') ? Math.Max(point.X, outer.X + outer.Width) + 12 : point.X;
        int y = compass.Contains('n') ? Math.Min(point.Y, outer.Y) - 12 : compass.Contains('s') ? Math.Max(point.Y, outer.Y + outer.Height) + 12 : point.Y;
        return new(x, y);
    }

    private static Point DotCompoundPoint(DotDocument dot, string name, Point source, Point target)
    {
        if (name.Length == 0) return source;
        var scope = dot.Scopes.FirstOrDefault(scope => scope.Id == name && scope.IsCluster);
        if (scope?.Box is not { } box) throw InvalidDot();
        if (source.X < box.X || source.X > box.X + box.Width || source.Y < box.Y || source.Y > box.Y + box.Height) return source;
        return DotRectanglePoint(new(box.X, box.Y, box.Width, box.Height), target, "");
    }

    private static Point DotAttachment(DotEndpoint endpoint, string attribute, Point target, bool clip)
    {
        string port = endpoint.Port, compass = endpoint.Compass;
        if (attribute.Length != 0)
        {
            var parts = attribute.Split(':');
            if (parts.Length > 2) throw InvalidDot();
            port = parts[0]; compass = parts.Length == 2 ? parts[1] : "";
        }
        var box = DotShapeBox(endpoint.Node);
        bool field = endpoint.Node.Visual.Ports.TryGetValue(port, out var portBox);
        if (field) box = portBox;
        else if (compass.Length == 0 && DotCompass(port)) compass = port;
        if (compass.Length != 0 && !DotCompass(compass)) throw InvalidDot();
        if (!clip || compass == "c") return new(box.X + box.Width / 2, box.Y + box.Height / 2);
        var point = DotRectanglePoint(box, target, compass);
        string shape = endpoint.Node.Visual.Shape;
        if (!field && shape is "ellipse" or "oval" or "circle" or "doublecircle" or "Mcircle" or "point" or "egg")
        {
            double cx = box.X + box.Width / 2.0, cy = box.Y + box.Height / 2.0, dx = point.X - cx, dy = point.Y - cy;
            double scale = 1 / Math.Sqrt(dx * dx / Math.Pow(box.Width / 2.0, 2) + dy * dy / Math.Pow(box.Height / 2.0, 2));
            return new((int)Math.Round(cx + dx * scale), (int)Math.Round(cy + dy * scale));
        }
        if (!field && shape is "diamond" or "Mdiamond" or "triangle" or "invtriangle" or "pentagon" or "hexagon" or "septagon" or "octagon" or "doubleoctagon" or "tripleoctagon" or "star" or "polygon" or "house" or "invhouse" or "trapezium" or "invtrapezium" or "parallelogram")
        {
            var polygon = DotPolygon(endpoint.Node.Visual, box.X, box.Y, box.Width, box.Height);
            double cx = box.X + box.Width / 2.0, cy = box.Y + box.Height / 2.0, dx = point.X - cx, dy = point.Y - cy, best = double.PositiveInfinity;
            for (int i = 0; i < polygon.Length; i++)
            {
                var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
                double ex = b.X - a.X, ey = b.Y - a.Y, determinant = dx * ey - dy * ex;
                if (Math.Abs(determinant) < .00001) continue;
                double t = ((a.X - cx) * ey - (a.Y - cy) * ex) / determinant;
                double u = ((a.X - cx) * dy - (a.Y - cy) * dx) / determinant;
                if (t >= 0 && u is >= 0 and <= 1) best = Math.Min(best, t);
            }
            if (double.IsFinite(best)) return new((int)Math.Round(cx + dx * best), (int)Math.Round(cy + dy * best));
        }
        return point;
    }

    private static Point DotRectanglePoint(DotRect box, Point target, string compass)
    {
        int cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2;
        if (compass.Length == 0 || compass == "_")
        {
            int dx = target.X - cx, dy = target.Y - cy;
            compass = Math.Abs(dx) * box.Height > Math.Abs(dy) * box.Width ? dx < 0 ? "w" : "e" : dy < 0 ? "n" : "s";
        }
        int x = compass.Contains('w') ? box.X : compass.Contains('e') ? box.X + box.Width : cx;
        int y = compass.Contains('n') ? box.Y : compass.Contains('s') ? box.Y + box.Height : cy;
        return new(x, y);
    }

    private static void DrawDotEdge(StringBuilder svg, DotDocument dot, DotEdge edge, Point[] points, DotBounds bounds, ref int serial)
    {
        if (points.Length < 2) return;
        var visual = edge.Visual;
        string name = edge.From.Node.Id + (dot.Directed ? "->" : "--") + edge.To.Node.Id;
        DotGroupStart(svg, "edge", "edge:" + edge.Index, name, edge.Span, edge.Insert, edge.LabelToken, visual, true, edge);
        bool link = DotLinkStart(svg, edge.Attributes.Get("edgehref", edge.Attributes.Get("edgeURL", visual.Link)), edge.Attributes.Get("edgetarget", visual.Target), edge.Attributes.Get("edgetooltip", visual.Tooltip));
        string direction = edge.Attributes.Get("dir", dot.Directed ? "forward" : "none");
        if (direction is not ("forward" or "back" or "both" or "none")) throw InvalidDot();
        double arrowSize = DotNumber(edge.Attributes.Get("arrowsize"), 1, 0, 10);
        var head = DotArrow(edge.Attributes.Get("arrowhead", "normal"));
        var tail = DotArrow(edge.Attributes.Get("arrowtail", "normal"));
        double tailLength = direction is "back" or "both" ? tail.Sum(part => part.Length) * arrowSize : 0;
        double headLength = direction is "forward" or "both" ? head.Sum(part => part.Length) * arrowSize : 0;
        var trimmed = points.ToArray();
        trimmed[0] = DotInset(points[0], points[1], tailLength);
        trimmed[^1] = DotInset(points[^1], points[^2], headLength);
        string route = string.Join(' ', points.Select(point => FormattableString.Invariant($"{point.X},{point.Y}")));
        svg.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{route}\" fill=\"none\" stroke=\"transparent\" stroke-width=\"9\"/>");
        if (dot.Splines is "spline" or "splines" or "true" or "curved")
            svg.Append(CultureInfo.InvariantCulture, $"<path d=\"{RoundedRoute(trimmed)}\" fill=\"none\" stroke=\"{visual.Stroke}\" stroke-width=\"{visual.PenWidth:0.###}\"");
        else svg.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{string.Join(' ', trimmed.Select(point => FormattableString.Invariant($"{point.X},{point.Y}")))}\" fill=\"none\" stroke=\"{visual.Stroke}\" stroke-width=\"{visual.PenWidth:0.###}\"");
        if (visual.Dashes.Length != 0) svg.Append(" stroke-dasharray=\"").Append(visual.Dashes).Append('"');
        svg.Append("/>");
        if (direction is "back" or "both") DrawDotArrow(svg, points[0], points[1], tail, arrowSize, visual, ref serial);
        if (direction is "forward" or "both") DrawDotArrow(svg, points[^1], points[^2], head, arrowSize, visual, ref serial);
        DotArrowBounds(bounds, points[0], points[1], tailLength, arrowSize * 6 + visual.PenWidth);
        DotArrowBounds(bounds, points[^1], points[^2], headLength, arrowSize * 6 + visual.PenWidth);
        if (link) svg.Append("</a>");
        var center = DotRouteCenter(points);
        var fromBox = DotShapeBox(edge.From.Node); var toBox = DotShapeBox(edge.To.Node);
        if (fromBox.X + fromBox.Width <= toBox.X || toBox.X + toBox.Width <= fromBox.X)
        {
            int left = Math.Min(fromBox.X + fromBox.Width, toBox.X + toBox.Width), right = Math.Max(fromBox.X, toBox.X);
            center = new((left + right) / 2, (points[0].Y + points[^1].Y) / 2);
        }
        else if (fromBox.Y + fromBox.Height <= toBox.Y || toBox.Y + toBox.Height <= fromBox.Y)
        {
            int top = Math.Min(fromBox.Y + fromBox.Height, toBox.Y + toBox.Height), bottom = Math.Max(fromBox.Y, toBox.Y);
            center = new((points[0].X + points[^1].X) / 2, (top + bottom + visual.Label.Height) / 2);
        }
        PlaceDotEdgeLabel(svg, visual.Label, center.X, center.Y - 5 - visual.Label.Height, edge.Attributes, "label", bounds);
        if (edge.ExternalLabel != null) PlaceDotEdgeLabel(svg, edge.ExternalLabel, center.X, center.Y + 5, edge.Attributes, "label", bounds);
        double distance = DotNumber(edge.Attributes.Get("labeldistance"), 1, 0, 100) * 10;
        double angle = DotNumber(edge.Attributes.Get("labelangle"), -25, -3600, 3600) * Math.PI / 180;
        void EndpointLabel(DotLabel? label, Point endpoint, Point away, string prefix, bool atHead)
        {
            if (label == null) return;
            double heading = Math.Atan2(away.Y - endpoint.Y, away.X - endpoint.X) + (atHead ? -angle : angle);
            int x = endpoint.X + (int)Math.Round(Math.Cos(heading) * distance);
            int y = endpoint.Y + (int)Math.Round(Math.Sin(heading) * distance) - label.Height / 2;
            PlaceDotEdgeLabel(svg, label, x, y, edge.Attributes, prefix, bounds);
        }
        EndpointLabel(edge.TailLabel, points[0], points[1], "tail", false);
        EndpointLabel(edge.HeadLabel, points[^1], points[^2], "head", true);
        svg.Append("</g>");
        int expansion = (int)Math.Ceiling(Math.Max(arrowSize * 6, visual.PenWidth / 2)) + 2;
        foreach (var point in points) bounds.Add(point.X - expansion, point.Y - expansion, expansion * 2, expansion * 2);
    }

    private static void PlaceDotEdgeLabel(StringBuilder svg, DotLabel label, int centerX, int top, DotAttributes attributes, string prefix, DotBounds bounds)
    {
        if (label.Width == 0 && label.Height == 0) return;
        label.Place(centerX - label.Width / 2, top, label.Width, label.Height);
        bool link = DotLinkStart(svg, attributes.Get(prefix + "href", attributes.Get(prefix + "URL")), attributes.Get(prefix + "target"), attributes.Get(prefix + "tooltip"));
        label.Draw(svg);
        if (link) svg.Append("</a>");
        bounds.Add(label);
    }

    private static Point DotInset(Point endpoint, Point away, double amount)
    {
        double dx = away.X - endpoint.X, dy = away.Y - endpoint.Y, length = Math.Sqrt(dx * dx + dy * dy);
        if (length == 0) return endpoint;
        amount = Math.Min(amount, length * .8);
        return new((int)Math.Round(endpoint.X + dx * amount / length), (int)Math.Round(endpoint.Y + dy * amount / length));
    }

    private static void DotArrowBounds(DotBounds bounds, Point tip, Point away, double length, double halfWidth)
    {
        if (length == 0) return;
        double dx = away.X - tip.X, dy = away.Y - tip.Y, distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance == 0) return;
        dx /= distance; dy /= distance;
        foreach (double along in new[] { 0d, length })
            foreach (int side in new[] { -1, 1 })
            {
                int x = (int)Math.Floor(tip.X + dx * along - dy * side * halfWidth), y = (int)Math.Floor(tip.Y + dy * along + dx * side * halfWidth);
                bounds.Add(x - 1, y - 1, 3, 3);
            }
    }

    private static Point DotRouteCenter(Point[] points)
    {
        static double Length(Point a, Point b) => Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
        double remaining = 0;
        for (int i = 1; i < points.Length; i++) remaining += Length(points[i - 1], points[i]);
        remaining /= 2;
        for (int i = 1; i < points.Length; i++)
        {
            var a = points[i - 1]; var b = points[i]; double length = Length(a, b);
            if (length != 0 && remaining <= length) return new((int)Math.Round(a.X + (b.X - a.X) * remaining / length), (int)Math.Round(a.Y + (b.Y - a.Y) * remaining / length));
            remaining -= length;
        }
        return points[^1];
    }

    private sealed record DotArrowPart(string Shape, bool Open, int Half)
    {
        internal int Length => Shape == "none" ? 0 : Shape is "tee" or "curve" or "icurve" ? 5 : 10;
    }
    private static List<DotArrowPart> DotArrow(string value)
    {
        value = value switch { "empty" => "onormal", "invempty" => "oinv", "open" => "vee", "halfopen" => "lvee", "ediamond" => "odiamond", _ => value };
        var parts = new List<DotArrowPart>();
        int at = 0;
        while (at < value.Length)
        {
            if (parts.Count >= 4) throw InvalidDot();
            bool open = false; int half = 0;
            while (at < value.Length && value[at] is 'o' or 'l' or 'r')
            {
                char modifier = value[at++];
                if (modifier == 'o') { if (open) throw InvalidDot(); open = true; }
                else { if (half != 0) throw InvalidDot(); half = modifier == 'l' ? -1 : 1; }
            }
            string? shape = new[] { "normal", "diamond", "icurve", "curve", "none", "crow", "box", "dot", "inv", "tee", "vee" }.FirstOrDefault(shape => value.AsSpan(at).StartsWith(shape));
            if (shape == null) throw InvalidDot();
            at += shape.Length;
            parts.Add(new(shape, open, half));
        }
        return parts;
    }

    private static void DrawDotArrow(StringBuilder svg, Point tip, Point away, List<DotArrowPart> parts, double size, DotVisual visual, ref int serial)
    {
        if (size == 0 || parts.Count == 0) return;
        double angle = Math.Atan2(away.Y - tip.Y, away.X - tip.X) * 180 / Math.PI;
        svg.Append(CultureInfo.InvariantCulture, $"<g transform=\"translate({tip.X} {tip.Y}) rotate({angle:0.###}) scale({size:0.###})\" stroke=\"{visual.Stroke}\" stroke-width=\"{visual.PenWidth / size:0.###}\" stroke-linejoin=\"round\">");
        int offset = 0;
        foreach (var part in parts)
        {
            if (part.Shape == "none") continue;
            svg.Append(CultureInfo.InvariantCulture, $"<g transform=\"translate({offset} 0)\" fill=\"{(part.Open ? "none" : visual.Stroke)}\"");
            if (part.Half != 0)
            {
                string id = "dot-half-" + serial++;
                svg.Append(" clip-path=\"url(#").Append(id).Append(")\">");
                svg.Append(CultureInfo.InvariantCulture, $"<defs><clipPath id=\"{id}\"><rect x=\"-2\" y=\"{(part.Half < 0 ? -12 : 0)}\" width=\"18\" height=\"12\"/></clipPath></defs>");
            }
            else svg.Append('>');
            switch (part.Shape)
            {
                case "normal": svg.Append("<path d=\"M0 0L10 -4L10 4Z\"/>"); break;
                case "inv": svg.Append("<path d=\"M10 0L0 -4L0 4Z\"/>"); break;
                case "diamond": svg.Append("<path d=\"M0 0L5 -4L10 0L5 4Z\"/>"); break;
                case "dot": svg.Append("<circle cx=\"5\" cy=\"0\" r=\"4\"/>"); break;
                case "box": svg.Append("<rect x=\"0\" y=\"-4\" width=\"8\" height=\"8\"/>"); break;
                case "tee": svg.Append("<path d=\"M2 -5V5\" fill=\"none\"/>"); break;
                case "vee": svg.Append("<path d=\"M0 0L10 -5L7 0L10 5Z\"/>"); break;
                case "crow": svg.Append("<path d=\"M0 -5L10 0L0 5M10 0H0\" fill=\"none\"/>"); break;
                case "curve": svg.Append("<path d=\"M5 -5Q-3 0 5 5\" fill=\"none\"/>"); break;
                case "icurve": svg.Append("<path d=\"M0 -5Q8 0 0 5\" fill=\"none\"/>"); break;
            }
            svg.Append("</g>"); offset += part.Length;
        }
        svg.Append("</g>");
    }
}

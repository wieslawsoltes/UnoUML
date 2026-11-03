using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

internal static partial class GraphRenderer
{
    private sealed class DotVisual
    {
        internal DotLabel Label = null!;
        internal DotFont Font = new(14, "Times New Roman, Times, serif", "black");
        internal DotLabel? External;
        internal DotImageLabel? Image;
        internal string ImagePosition = "mc";
        internal string LabelFormat = "text";
        internal bool LabelShared;
        internal string Shape = "ellipse", Stroke = "black", Fill = "none", Dashes = "", Style = "", Link = "", Target = "", Tooltip = "";
        internal string Location = "c", Justify = "c", Class = "", SvgId = "";
        internal int ShapeWidth, ShapeHeight, Peripheries = 1, Sides = 4, MarginX = 8, MarginY = 4;
        internal double PenWidth = 1, Rotation, Skew, Distortion, GradientAngle;
        internal bool Invisible, Rounded, Diagonals, Fixed;
        internal readonly Dictionary<string, DotRect> Ports = new(StringComparer.Ordinal);
    }

    private static readonly HashSet<string> DotShapes = new(("box rect rectangle square ellipse oval circle point egg triangle plaintext plain none " +
        "diamond trapezium parallelogram house pentagon hexagon septagon octagon doublecircle doubleoctagon tripleoctagon invtriangle invtrapezium invhouse " +
        "Mdiamond Msquare Mcircle note tab folder box3d component cylinder record Mrecord underline star polygon").Split(' '), StringComparer.Ordinal);

    private static bool ArrangeDot(DotDocument dot)
    {
        var graph = dot.Graph;
        var attributes = dot.Root.Attributes;
        dot.RankDirection = attributes.Get("rankdir", "TB");
        if (dot.RankDirection is not ("TB" or "BT" or "LR" or "RL")) throw InvalidDot();
        dot.Splines = attributes.Get("splines", "spline");
        if (dot.Splines is not ("spline" or "splines" or "true" or "polyline" or "ortho" or "line" or "false" or "curved" or "none" or "")) throw InvalidDot();
        if (attributes.Get("layout", "dot") != "dot" || attributes.Get("ratio").Length != 0 || attributes.Get("size").Length != 0 ||
            attributes.Get("page").Length != 0 || attributes.Get("viewport").Length != 0 || attributes.Get("rotate", "0") != "0" ||
            DotBool(attributes.Get("landscape")) || DotBool(attributes.Get("concentrate"))) throw InvalidDot();
        graph.Horizontal = dot.RankDirection is "LR" or "RL";
        graph.CenterRanks = true;
        graph.NodeGap = Math.Max(2, DotInches(attributes, "nodesep", .25));
        string ranksep = attributes.Get("ranksep", ".5");
        if (ranksep.EndsWith(" equally", StringComparison.Ordinal)) { graph.EqualRanks = true; ranksep = ranksep[..^8].TrimEnd(); }
        graph.RankGap = Math.Max(2, (int)Math.Ceiling(DotNumber(ranksep, .5, .02, 100) * 72));
        dot.Scale = DotNumber(attributes.Get("dpi", attributes.Get("resolution")), 96, 1, 600) / 96;
        dot.Padding = DotInches(attributes, "pad", 4.0 / 72);
        dot.Root.Visual = DotAppearance(attributes, DotToken.Literal(""), dot.Root.Id, "", true);
        dot.Root.Visual.Label = DotMakeLabel(attributes.Token("label"), dot.Root.Visual.Font, dot.Root.Id);
        var nodeLabelOwners = dot.Nodes.Values.Where(node => node.LabelToken != null).GroupBy(node => node.LabelToken!.Value.Start).ToDictionary(group => group.Key, group => group.Count());
        var nodeStatements = dot.Nodes.Values.Where(node => node.Explicit).GroupBy(node => node.Span.Start).ToDictionary(group => group.Key, group => group.Count());
        var edgeStatements = dot.Edges.GroupBy(edge => edge.Span.Start).ToDictionary(group => group.Key, group => group.Count());
        foreach (var scope in dot.Scopes.Where(scope => scope.IsCluster))
        {
            var parent = DotClusterParent(scope.Parent)?.Box ?? graph.Root;
            scope.Visual = DotAppearance(scope.Attributes, DotToken.Literal(""), dot.Root.Id, "", true);
            scope.Visual.Label = DotMakeLabel(scope.Attributes.Token("label"), scope.Visual.Font, dot.Root.Id);
            if (scope.LabelToken == null) SuppressDotLabelEditing(scope.Visual.Label);
            var visual = scope.Visual;
            int margin = (int)Math.Ceiling(DotNumber(scope.Attributes.Get("margin"), 8, 0, 1024));
            int labelHeight = visual.Label.Plain.Length == 0 ? 0 : visual.Label.Height + 8;
            bool bottomLabel = scope.Attributes.Get("labelloc", "t") == "b";
            scope.Box = new("measured-group", scope.Id, visual.Label.Plain, scope.Span.Line, parent)
            {
                Width = Math.Max(20, visual.Label.Width + margin * 2), Height = margin + (bottomLabel ? 0 : labelHeight),
                ChildPadding = new(0, margin, margin + (bottomLabel ? labelHeight : 0), margin)
            };
            graph.Nodes.Add(scope.Box);
            parent.Children.Add(scope.Box);
        }
        foreach (var node in dot.Nodes.Values)
        {
            // DOT node identities are global. Membership can be introduced after
            // its first use, so select the deepest compatible cluster now.
            DotScope? cluster = null;
            foreach (var scope in dot.Scopes.Where(scope => scope.IsCluster && scope.Nodes.Contains(node.Id)))
            {
                if (cluster == null || DotContains(cluster, scope)) cluster = scope;
                else if (!DotContains(scope, cluster)) throw InvalidDot();
            }
            node.Visual = DotAppearance(node.Attributes, DotToken.Literal("\\N"), dot.Root.Id, node.Id, recordHorizontal: !graph.Horizontal);
            var visual = node.Visual;
            visual.LabelShared = node.Explicit && nodeStatements[node.Span.Start] > 1 || node.LabelToken is { } owned && nodeLabelOwners[owned.Start] > 1;
            if (node.LabelToken == null || visual.LabelShared) SuppressDotLabelEditing(visual.Label);
            if (visual.External != null && (visual.LabelShared || node.Attributes.Token("xlabel").Start < node.Span.Start))
                SuppressDotLabelEditing(visual.External);
            var parent = cluster?.Box ?? graph.Root;
            node.Box = new("measured-card", node.Id, visual.Label.Plain, node.Span.Line, parent)
            {
                Width = Math.Max(visual.ShapeWidth, visual.Label.Width + 2 * visual.MarginX) + (int)Math.Ceiling(visual.PenWidth),
                Height = Math.Max(visual.ShapeHeight, visual.Label.Height + 2 * visual.MarginY) + (int)Math.Ceiling(visual.PenWidth)
            };
            if (visual.External != null)
            {
                node.Box.Width = Math.Max(node.Box.Width, visual.External.Width);
                node.Box.Height += visual.External.Height + 4;
            }
            graph.Nodes.Add(node.Box);
            parent.Children.Add(node.Box);
            if (!node.Explicit) node.Insert = node.Scope.Insert;
        }
        foreach (var edge in dot.Edges)
        {
            edge.Visual = DotAppearance(edge.Attributes, DotToken.Literal(""), dot.Root.Id, "", edge: true,
                tail: edge.From.Node.Id, head: edge.To.Node.Id, directed: dot.Directed);
            edge.Visual.LabelShared = edgeStatements[edge.Span.Start] > 1;
            if (edge.LabelToken == null || edge.Visual.LabelShared) SuppressDotLabelEditing(edge.Visual.Label);
            string label = edge.Attributes.Get("label");
            var font = edge.Visual.Font with
            {
                Size = (int)Math.Ceiling(DotNumber(edge.Attributes.Get("labelfontsize"), 14, 1, 256)),
                Family = DotFamily(edge.Attributes.Get("labelfontname", edge.Visual.Font.Family)),
                Color = DotColor(edge.Attributes.Get("labelfontcolor", edge.Visual.Font.Color), edge.Attributes.Get("colorscheme"))
            };
            edge.HeadLabel = DotMakeLabel(edge.Attributes.Token("headlabel"), font, dot.Root.Id, "", edge.From.Node.Id, edge.To.Node.Id, dot.Directed);
            edge.TailLabel = DotMakeLabel(edge.Attributes.Token("taillabel"), font, dot.Root.Id, "", edge.From.Node.Id, edge.To.Node.Id, dot.Directed);
            edge.ExternalLabel = edge.Visual.External;
            if (edge.Visual.LabelShared || edge.Attributes.Token("headlabel").Start < edge.Span.Start) SuppressDotLabelEditing(edge.HeadLabel);
            if (edge.Visual.LabelShared || edge.Attributes.Token("taillabel").Start < edge.Span.Start) SuppressDotLabelEditing(edge.TailLabel);
            if (edge.ExternalLabel != null && (edge.Visual.LabelShared || edge.Attributes.Token("xlabel").Start < edge.Span.Start))
                SuppressDotLabelEditing(edge.ExternalLabel);
            int minimumLength = (int)DotNumber(edge.Attributes.Get("minlen"), 1, 0, 64);
            bool constrain = !edge.Attributes.TryGetValue("constraint", out var constraint) || DotBool(constraint.Value);
            edge.Layout = new(edge.From.Node.Box, edge.To.Node.Box, dot.Directed ? "-->" : "--", "", "", "", edge.Span.Line, "down", edge.Visual.Invisible)
            {
                ConstrainsRank = constrain, MinimumLength = minimumLength,
                LayoutLabelWidth = edge.Visual.Label.Width + (label.Length == 0 ? 0 : 8),
                LayoutLabelHeight = edge.Visual.Label.Height + (label.Length == 0 ? 0 : 8)
            };
            graph.Edges.Add(edge.Layout);
            dot.EdgeMap.Add(edge.Layout, edge);
        }
        foreach (var scope in dot.Scopes)
        {
            string rank = scope.Attributes.Get("rank");
            if (rank.Length == 0) continue;
            if (rank is not ("same" or "min" or "max" or "source" or "sink")) throw InvalidDot();
            var nodes = scope.Nodes.Select(id => dot.Nodes[id].Box).ToArray();
            if (nodes.Length == 0) continue;
            Node container = nodes[0].Parent!;
            if (nodes.Any(node => node.Parent != container)) throw InvalidDot();
            var slots = container.Children.Select((node, index) => (node, index)).ToDictionary(pair => pair.node, pair => pair.index);
            int anchor = slots[nodes[0]];
            for (int i = 1; i < nodes.Length; i++) container.Arcs.Add(new(anchor, slots[nodes[i]], "right", true));
            if (rank == "same") continue;
            var members = nodes.ToHashSet();
            for (int i = 0; i < container.Children.Count; i++)
                if (!members.Contains(container.Children[i]))
                    container.Arcs.Add(new(anchor, i, rank is "max" or "sink" ? "up" : "down", true, MinimumLength: rank is "source" or "sink" ? 1 : 0));
        }
        if (!Arrange(graph)) return false;
        if (dot.RankDirection is "BT" or "RL")
        {
            foreach (var node in graph.Nodes)
                if (graph.Horizontal) node.X = graph.Root.Width + 64 - node.X - node.Width;
                else node.Y = graph.Root.Height + 48 - node.Y - node.Height;
        }
        foreach (var node in dot.Nodes.Values)
        {
            var visual = node.Visual;
            int contentHeight = node.Box.Height - (visual.External?.Height + 4 ?? 0);
            int x = node.Box.X + (node.Box.Width - visual.ShapeWidth) / 2;
            int y = node.Box.Y + (contentHeight - visual.ShapeHeight) / 2;
            if (visual.Shape is "record" or "Mrecord") visual.Label.Place(x, y, visual.ShapeWidth, visual.ShapeHeight);
            else
            {
                int labelX = node.Box.X + (node.Box.Width - visual.Label.Width) / 2;
                int labelY = visual.Location == "t" ? y + visual.MarginY : visual.Location == "b" ? y + visual.ShapeHeight - visual.MarginY - visual.Label.Height : node.Box.Y + (contentHeight - visual.Label.Height) / 2;
                visual.Label.Place(labelX, labelY, visual.Label.Width, visual.Label.Height);
            }
            visual.Label.Ports(visual.Ports);
            if (visual.Image != null)
            {
                var dimensions = visual.Image.Fit(Math.Max(1, visual.ShapeWidth - visual.MarginX * 2), Math.Max(1, visual.ShapeHeight - visual.MarginY * 2));
                int imageX = visual.ImagePosition[1] == 'l' ? x + visual.MarginX : visual.ImagePosition[1] == 'r' ? x + visual.ShapeWidth - visual.MarginX - dimensions.Width : x + (visual.ShapeWidth - dimensions.Width) / 2;
                int imageY = visual.ImagePosition[0] == 't' ? y + visual.MarginY : visual.ImagePosition[0] == 'b' ? y + visual.ShapeHeight - visual.MarginY - dimensions.Height : y + (visual.ShapeHeight - dimensions.Height) / 2;
                visual.Image.Place(imageX, imageY, dimensions.Width, dimensions.Height);
            }
            visual.External?.Place(node.Box.X + (node.Box.Width - visual.External.Width) / 2, node.Box.Y + contentHeight + 4, visual.External.Width, visual.External.Height);
        }
        foreach (var scope in dot.Scopes.Where(scope => scope.IsCluster))
        {
            var box = scope.Box!; var visual = scope.Visual!;
            int x = visual.Justify == "l" ? box.X + 8 : visual.Justify == "r" ? box.X + box.Width - visual.Label.Width - 8 : box.X + (box.Width - visual.Label.Width) / 2;
            int y = scope.Attributes.Get("labelloc", "t") == "b" ? box.Y + box.Height - visual.Label.Height - 4 : box.Y + 4;
            visual.Label.Place(x, y, visual.Label.Width, visual.Label.Height);
        }
        return true;
    }

    private static DotScope? DotClusterParent(DotScope? scope)
    {
        while (scope != null && !scope.IsCluster) scope = scope.Parent;
        return scope;
    }
    private static bool DotContains(DotScope ancestor, DotScope child)
    {
        for (var parent = child.Parent; parent != null; parent = parent.Parent) if (parent == ancestor) return true;
        return false;
    }

    private static DotVisual DotAppearance(DotAttributes attributes, DotToken defaultLabel, string graph, string node, bool group = false,
        bool edge = false, string tail = "", string head = "", bool directed = true, bool recordHorizontal = true)
    {
        var visual = new DotVisual();
        string scheme = attributes.Get("colorscheme");
        visual.Font = DotNamedFont(new((int)Math.Ceiling(DotNumber(attributes.Get("fontsize"), 14, 1, 256)),
            "", DotColor(attributes.Get("fontcolor", "black"), scheme)), attributes.Get("fontname", "Times-Roman"));
        visual.Stroke = DotColor(attributes.Get("color", "black"), scheme);
        visual.Shape = group ? "box" : edge ? "none" : attributes.Get("shape", "ellipse");
        if (!DotShapes.Contains(visual.Shape)) throw InvalidDot();
        visual.Style = attributes.Get("style");
        var styles = visual.Style.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (string style in styles)
        {
            switch (style)
            {
                case "solid": visual.Dashes = ""; break;
                case "dashed": visual.Dashes = "5,2"; break;
                case "dotted": visual.Dashes = "1,5"; break;
                case "bold": visual.PenWidth = 2; break;
                case "invis": case "invisible": visual.Invisible = true; break;
                case "rounded": visual.Rounded = true; break;
                case "diagonals": visual.Diagonals = true; break;
                case "filled": break;
                case "radial": break;
                default: throw InvalidDot();
            }
        }
        visual.PenWidth = DotNumber(attributes.Get("penwidth"), visual.PenWidth, 0, 64);
        visual.Fill = styles.Contains("filled") || styles.Contains("radial") ? DotFill(attributes.Get("fillcolor", attributes.Get("color", group ? attributes.Get("bgcolor", "lightgrey") : "lightgrey")), scheme) : "none";
        if (group && attributes.ContainsKey("bgcolor") && !attributes.ContainsKey("fillcolor")) visual.Fill = DotFill(attributes.Get("bgcolor"), scheme);
        if (visual.Shape == "point") visual.Fill = attributes.ContainsKey("fillcolor") ? DotFill(attributes.Get("fillcolor"), scheme) : visual.Stroke;
        visual.Peripheries = (int)DotNumber(attributes.Get("peripheries"), visual.Shape is "doublecircle" or "doubleoctagon" ? 2 : visual.Shape == "tripleoctagon" ? 3 : 1, 0, 8);
        visual.Rounded |= visual.Shape == "Mrecord";
        visual.Diagonals |= visual.Shape is "Mdiamond" or "Msquare" or "Mcircle";
        visual.GradientAngle = DotNumber(attributes.Get("gradientangle"), 0, -3600, 3600);
        visual.Rotation = DotNumber(attributes.Get("orientation"), 0, -3600, 3600);
        visual.Sides = (int)DotNumber(attributes.Get("sides"), 4, 3, 128);
        visual.Skew = DotNumber(attributes.Get("skew"), 0, -10, 10);
        visual.Distortion = DotNumber(attributes.Get("distortion"), 0, -10, 10);
        visual.Link = DotSubstitute(attributes.Get("href", attributes.Get("URL")), graph, node, tail, head, tail + (directed ? "->" : "--") + head);
        visual.Target = attributes.Get("target"); visual.Tooltip = attributes.Get("tooltip");
        visual.Class = attributes.Get("class"); visual.SvgId = attributes.Get("id");
        visual.Location = attributes.Get("labelloc", group ? "t" : "c"); visual.Justify = attributes.Get("labeljust", "c");
        if (visual.Location is not ("t" or "c" or "b") || visual.Justify is not ("l" or "c" or "r")) throw InvalidDot();
        if (attributes.Get("shapefile").Length != 0 || DotBool(attributes.Get("decorate"))) throw InvalidDot();
        if (attributes.TryGetValue("image", out var imageToken) && imageToken.Value.Length != 0)
        {
            var image = SpriteCatalog.ResolveImage(imageToken.Value, imageToken.Line);
            if (image == null) throw InvalidDot();
            visual.Image = new(image, attributes.Get("imagescale", "false")) { Width = (int)Math.Ceiling(image.Width), Height = (int)Math.Ceiling(image.Height) };
            visual.ImagePosition = attributes.Get("imagepos", "mc");
            if (visual.ImagePosition.Length != 2 || !"tmb".Contains(visual.ImagePosition[0]) || !"lcr".Contains(visual.ImagePosition[1])) throw InvalidDot();
        }
        if (attributes.Get("style").Contains("setlinewidth", StringComparison.Ordinal)) throw InvalidDot();
        string margin = attributes.Get("margin");
        if (!group && margin.Length != 0)
        {
            string[] values = margin.Split(',', StringSplitOptions.TrimEntries);
            if (values.Length is < 1 or > 2) throw InvalidDot();
            visual.MarginX = (int)Math.Ceiling(DotNumber(values[0], .11, 0, 50) * 72);
            visual.MarginY = (int)Math.Ceiling(DotNumber(values[^1], .055, 0, 50) * 72);
        }
        var label = attributes.Token("label", defaultLabel.Value);
        visual.LabelFormat = visual.Shape is "record" or "Mrecord" ? "record" : label.Kind == DotTokenKind.Html ? "html" : "text";
        visual.Label = visual.Shape is "record" or "Mrecord"
            ? DotRecord(DotSubstitute(label.Value, graph, node), label, visual.Font, recordHorizontal, visual.Stroke, visual.PenWidth)
            : DotMakeLabel(label, visual.Font, graph, node, tail, head, directed);
        if (visual.Shape == "point") { visual.Label = DotPlainLabel("", visual.Font); visual.MarginX = visual.MarginY = 0; }
        if (attributes.Get("xlabel").Length != 0) visual.External = DotMakeLabel(attributes.Token("xlabel"), visual.Font, graph, node, tail, head, directed);
        bool plain = visual.Shape is "plain" or "none" or "plaintext";
        if (visual.Shape == "plain") visual.MarginX = visual.MarginY = 0;
        int width = Math.Max(1, DotInches(attributes, "width", plain ? 0 : visual.Shape == "point" ? .05 : .75));
        int height = Math.Max(1, DotInches(attributes, "height", plain ? 0 : visual.Shape == "point" ? .05 : .5));
        string fixedSize = attributes.Get("fixedsize", "false");
        if (fixedSize is not ("true" or "false" or "1" or "0" or "shape")) throw InvalidDot();
        visual.Fixed = DotBool(fixedSize) || fixedSize == "shape";
        int labelWidth = visual.Label.Width + visual.MarginX * 2, labelHeight = visual.Label.Height + visual.MarginY * 2;
        if (visual.Image != null) { labelWidth = Math.Max(labelWidth, visual.Image.Width + visual.MarginX * 2); labelHeight = Math.Max(labelHeight, visual.Image.Height + visual.MarginY * 2); }
        if (!visual.Fixed)
        {
            if (visual.Shape is "ellipse" or "oval" or "circle" or "doublecircle" or "Mcircle" or "egg")
            { labelWidth = (int)Math.Ceiling(labelWidth * 1.42); labelHeight = (int)Math.Ceiling(labelHeight * 1.42); }
            else if (visual.Shape is "diamond" or "Mdiamond" or "triangle" or "invtriangle")
            { labelWidth *= 2; labelHeight *= 2; }
            else if (visual.Shape == "star") { labelWidth *= 3; labelHeight *= 3; }
            width = Math.Max(width, labelWidth); height = Math.Max(height, labelHeight);
        }
        if (visual.Shape is "circle" or "doublecircle" or "Mcircle" or "square" or "Msquare" || DotBool(attributes.Get("regular"))) width = height = Math.Max(width, height);
        visual.ShapeWidth = width; visual.ShapeHeight = height;
        return visual;
    }

    private static DotLabel DotMakeLabel(DotToken value, DotFont font, string graph, string node = "", string tail = "", string head = "", bool directed = true)
    {
        string text = DotSubstitute(value.Value, graph, node, tail, head, tail + (directed ? "->" : "--") + head);
        return value.Kind == DotTokenKind.Html ? DotHtml(text, value, font) : DotPlainLabel(text, font);
    }

    private static string DotFamily(string value) => value switch
    {
        "Times-Roman" or "Times" => "Times New Roman, Times, serif",
        "Helvetica" => "Arial, Helvetica, sans-serif",
        "Courier" => "Courier New, Courier, monospace",
        _ => value
    };

    private static DotFont DotNamedFont(DotFont inherited, string name)
    {
        string family = name;
        foreach (string suffix in new[] { "-BoldItalic", "-BoldOblique", "-Italic", "-Oblique", "-Bold" })
        {
            if (!name.EndsWith(suffix, StringComparison.Ordinal)) continue;
            family = name[..^suffix.Length];
            inherited = inherited with { Bold = inherited.Bold || suffix.Contains("Bold", StringComparison.Ordinal), Italic = inherited.Italic || suffix.Contains("Italic", StringComparison.Ordinal) || suffix.Contains("Oblique", StringComparison.Ordinal) };
            break;
        }
        return inherited with { Family = DotFamily(family) };
    }

    private static double DotNumber(string value, double fallback, double minimum, double maximum)
    {
        if (value.Length == 0) return fallback;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number) || number < minimum || number > maximum) throw InvalidDot();
        return number;
    }
    private static int DotInches(DotAttributes attributes, string name, double fallback) => (int)Math.Ceiling(DotNumber(attributes.Get(name), fallback, 0, 200) * 72);

    private static string DotFill(string value, string scheme = "")
    {
        var colors = value.Split(':');
        if (colors.Length > 2) throw InvalidDot();
        return string.Join(':', colors.Select(color => DotColor(color, scheme)));
    }

    private static string DotColor(string value, string scheme = "")
    {
        if (value.Length == 0) return "none";
        if (value.StartsWith('/'))
        {
            string[] qualified = value.Split('/');
            if (qualified.Length != 3) throw InvalidDot();
            scheme = qualified[1]; value = qualified[2];
        }
        if (scheme is not ("" or "x11" or "svg")) throw InvalidDot();
        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase) || value.Equals("none", StringComparison.OrdinalIgnoreCase)) return "none";
        string[] hsv = value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (hsv.Length == 3 && double.TryParse(hsv[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double hue))
        {
            hue = DotNumber(hsv[0], 0, 0, 1) * 6;
            double saturation = DotNumber(hsv[1], 0, 0, 1), brightness = DotNumber(hsv[2], 0, 0, 1);
            double chroma = brightness * saturation, middle = chroma * (1 - Math.Abs(hue % 2 - 1)), match = brightness - chroma;
            var (r, g, b) = ((int)hue % 6) switch { 0 => (chroma, middle, 0d), 1 => (middle, chroma, 0d), 2 => (0d, chroma, middle), 3 => (0d, middle, chroma), 4 => (middle, 0d, chroma), _ => (chroma, 0d, middle) };
            return FormattableString.Invariant($"#{(int)Math.Round((r + match) * 255):x2}{(int)Math.Round((g + match) * 255):x2}{(int)Math.Round((b + match) * 255):x2}");
        }
        string name = value.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        if (scheme != "svg" && DotX11Colors.TryGetValue(name, out string? x11)) return x11;
        if (ManagedSkin.Color(value) is { } color && !ManagedSkin.TryGradient(color, out _, out _, out _)) return color;
        throw InvalidDot();
    }
}

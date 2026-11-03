using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace UnoUML.PlantUml;

internal readonly record struct StyleSpacing(int Top, int Right, int Bottom, int Left)
{
    internal int Horizontal => Left + Right;
    internal int Vertical => Top + Bottom;
    internal static StyleSpacing Parse(string value)
    {
        var numbers = value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => (int)Math.Ceiling(double.Parse(v, CultureInfo.InvariantCulture))).ToArray();
        return numbers.Length switch
        {
            1 => new(numbers[0], numbers[0], numbers[0], numbers[0]),
            2 => new(numbers[0], numbers[1], numbers[0], numbers[1]),
            _ => new(numbers[0], numbers[1], numbers[2], numbers[3])
        };
    }
}

internal static class StyleText
{
    // Font ink can extend beyond the nominal em used by the shared text metrics.
    internal static StyleSpacing InkPadding(int size) => new((size * 15 + 99) / 100, 0, (size * 5 + 99) / 100, 0);

    internal static string[] Wrap(string text, int size, int maximumWidth) => maximumWidth <= 0 && SvgText.IsPlain(text) ? [text] : SvgText.Wrap(text, size, maximumWidth);

    internal static int Height(IReadOnlyList<string> rows, int size) => rows.Sum(row => SvgText.Height(row, size)) + Math.Max(0, rows.Count - 1) * 4;

    internal static int Measure(string value, int size, string fontStyle)
    {
        int width = SvgText.Measure(value, size);
        return fontStyle.Contains("bold", StringComparison.OrdinalIgnoreCase) ? (int)Math.Ceiling(width * 1.08) : width;
    }
}

// Shape painters emit semantic scopes, and this final pass applies paint-only
// properties after layout. Font sizes and spacing are consumed by the painters.
internal static class StyleDrawing
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    internal static string Apply(string svg, ManagedSkin skin, IReadOnlyDictionary<string, Dictionary<string, StyleSheet.Value>>? resolved = null)
    {
        if (skin.Sheet.IsEmpty && skin.Dpi == 96 && !skin.Handwritten && skin.MonospacedFont == "monospace") return svg;
        return Apply(XElement.Parse(svg, LoadOptions.PreserveWhitespace), skin, resolved);
    }

    internal static string Apply(XElement root, ManagedSkin skin, IReadOnlyDictionary<string, Dictionary<string, StyleSheet.Value>>? resolved = null)
    {
        string prefix = "uml-" + ((string?)root.Attribute("data-sequence-page") ?? "0") + "-";
        var shadows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.AncestorsAndSelf().Any(e => (string?)e.Attribute("data-rich-paint") == "true")) continue;
            var scope = element.AncestorsAndSelf().FirstOrDefault(e => e.Attribute("data-style-name") != null);
            if (scope == null) continue;
            string category = (string?)scope.Attribute("data-style-name") ?? "";
            string stereotype = (string?)scope.Attribute("data-style-stereotype") ?? "";
            string part = (string?)element.Attribute("data-style-part") ?? "";
            if (part.Length == 0 && element.Ancestors().TakeWhile(e => e != scope).Any(e => (string?)e.Attribute("class") == "member")) part = "body";
            bool custom = resolved != null && resolved.TryGetValue((string?)scope.Attribute("data-style-id") ?? "", out var specific);
            var style = custom ? resolved![(string)scope.Attribute("data-style-id")!] : skin.Query(category, stereotype, part);
            if (element.Name.LocalName == "g")
            {
                if (element == scope) Set("linethickness", "stroke-width");
                if (element.Attribute("stroke") is { Value: not ("none" or "transparent") }) Set("linecolor", "stroke");
            }
            else if (element.Name.LocalName == "tspan")
            {
                if ((part == "monospaced" || (string?)element.Attribute("data-rich-monospaced") == "true") && element.Attribute("data-rich-font") == null)
                    element.SetAttributeValue("font-family", skin.MonospacedFont);
                if (part == "hyperlink")
                {
                    if (element.Attribute("data-rich-color") == null) Set("hyperlinkcolor", "fill");
                    if (style.TryGetValue("hyperlinkunderlinethickness", out var underline) && underline.Text == "0")
                        element.SetAttributeValue("text-decoration", "none");
                }
            }
            else if (element.Name.LocalName == "text")
            {
                Set("fontname", "font-family");
                Set("fontcolor", "fill");
                if (style.TryGetValue("fontstyle", out var fontStyle))
                {
                    element.SetAttributeValue("font-style", fontStyle.Text.Contains("italic", StringComparison.Ordinal) ? "italic" : "normal");
                    element.SetAttributeValue("font-weight", fontStyle.Text.Contains("bold", StringComparison.Ordinal) ? "bold" : "normal");
                }
                Set("fontweight", "font-weight");
                if (style.TryGetValue("showstereotype", out var visible) && visible.Text == "false" && part == "stereotype") element.SetAttributeValue("display", "none");
            }
            else if (element.Name.LocalName == "circle" && element.Attribute("data-chart-marker") != null &&
                (style.ContainsKey("markershape") || style.ContainsKey("markercolor")))
            {
                Marker(element, style);
            }
            else if (element.Name.LocalName is "rect" or "path" or "polyline" or "polygon" or "circle" or "ellipse" or "line")
            {
                string? stroke = (string?)element.Attribute("stroke"), fill = (string?)element.Attribute("fill");
                bool hasStroke = stroke is not (null or "none" or "transparent");
                if (hasStroke) { Set("linecolor", "stroke"); Set("linethickness", "stroke-width"); Set("linestyle", "stroke-dasharray"); }
                if (fill is not (null or "none" or "transparent") && (string?)scope.Attribute("data-style-fill") != "explicit" &&
                    (category == "arrow" || element == scope.Elements().FirstOrDefault(e => e.Name.LocalName is "rect" or "path" or "polygon" or "circle" or "ellipse")))
                {
                    if (category == "arrow" && element.Name.LocalName != "rect") { Set("linecolor", "fill"); Set("headcolor", "fill"); }
                    else if (category != "arrow") Set("backgroundcolor", "fill");
                }
                if (element.Name.LocalName == "rect" && style.TryGetValue("roundcorner", out var radius))
                {
                    double round = double.Parse(radius.Text, CultureInfo.InvariantCulture) / 2;
                    element.SetAttributeValue("rx", round.ToString("0.###", CultureInfo.InvariantCulture));
                }
                if (element.Name.LocalName == "rect" && style.TryGetValue("diagonalcorner", out var cut) && double.Parse(cut.Text, CultureInfo.InvariantCulture) > 0)
                    Diagonal(element, double.Parse(cut.Text, CultureInfo.InvariantCulture));
                if (fill is not (null or "none" or "transparent") && element == scope.Elements().FirstOrDefault(e => e.Name.LocalName is "rect" or "path" or "polygon" or "circle" or "ellipse") && style.TryGetValue("shadowing", out var shadow) && shadow.Text != "0")
                {
                    if (!shadows.TryGetValue(shadow.Text, out string? id)) shadows[shadow.Text] = id = prefix + "shadow-" + shadows.Count;
                    element.SetAttributeValue("filter", "url(#" + id + ")");
                }
            }
            void Set(string property, string attribute)
            {
                if ((string?)scope.Attribute("data-style-" + property) != "explicit" && style.TryGetValue(property, out var value)) element.SetAttributeValue(attribute, custom ? value.Text : skin.Style(category, property, value.Text, stereotype, part));
            }
        }
        if (skin.Sheet.Rules.Any(rule => rule.Property == "fontcolor" && rule.Value == "automatic"))
            foreach (var attribute in root.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName is "fill" or "stroke" && a.Value == "automatic"))
            {
                string color = AutomaticFontColor(attribute.Parent!, skin);
                attribute.Value = color;
                if (attribute.Parent!.Attribute("style") is { } css) css.Value = css.Value.Replace("color:automatic", "color:" + color, StringComparison.Ordinal);
            }
        var definitions = new XElement(Svg + "defs");
        var gradients = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in root.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName is "fill" or "stroke"))
        {
            if (!ManagedSkin.TryGradient(attribute.Value, out string first, out string second, out char direction)) continue;
            if (!gradients.TryGetValue(attribute.Value, out string? id))
            {
                gradients[attribute.Value] = id = prefix + "gradient-" + gradients.Count;
                var (x1, y1, x2, y2) = direction switch { '|' => (0, 50, 100, 50), '/' => (0, 100, 100, 0), '\\' => (0, 0, 100, 100), _ => (50, 0, 50, 100) };
                definitions.Add(new XElement(Svg + "linearGradient", new XAttribute("id", id), new XAttribute("x1", x1 + "%"), new XAttribute("y1", y1 + "%"), new XAttribute("x2", x2 + "%"), new XAttribute("y2", y2 + "%"),
                    new XElement(Svg + "stop", new XAttribute("offset", "0%"), new XAttribute("stop-color", first)),
                    new XElement(Svg + "stop", new XAttribute("offset", "100%"), new XAttribute("stop-color", second))));
            }
            attribute.Value = "url(#" + id + ")";
        }
        if (shadows.Count != 0)
        {
            foreach (var (distance, id) in shadows)
                definitions.Add(new XElement(Svg + "filter", new XAttribute("id", id), new XAttribute("x", "-25%"), new XAttribute("y", "-25%"), new XAttribute("width", "150%"), new XAttribute("height", "150%"),
                    new XElement(Svg + "feDropShadow", new XAttribute("dx", distance), new XAttribute("dy", distance), new XAttribute("stdDeviation", "0"), new XAttribute("flood-color", "#000000"), new XAttribute("flood-opacity", "0.35"))));
        }
        if (definitions.HasElements) root.AddFirst(definitions);
        if (skin.Handwritten) SketchDrawing.Apply(root);
        if (skin.Dpi != 96)
        {
            double scale = skin.Dpi / 96.0;
            double[] box = ((string?)root.Attribute("viewBox") ?? "0 0 0 0").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            root.SetAttributeValue("width", Math.Ceiling(box[2] * scale));
            root.SetAttributeValue("height", Math.Ceiling(box[3] * scale));
            root.SetAttributeValue("viewBox", string.Join(" ", box.Select(v => (v * scale).ToString("0.###", CultureInfo.InvariantCulture))));
            var contents = root.Nodes().ToArray();
            root.RemoveNodes();
            root.Add(new XElement(Svg + "g", new XAttribute("transform", "scale(" + scale.ToString("0.######", CultureInfo.InvariantCulture) + ")"), contents));
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static string AutomaticFontColor(XElement element, ManagedSkin skin)
    {
        var scope = element.AncestorsAndSelf().FirstOrDefault(e => e.Attribute("data-style-name") != null);
        string category = (string?)scope?.Attribute("data-style-name") ?? "";
        var shape = scope?.Elements().FirstOrDefault(e => category == "arrow" ? e.Name.LocalName == "rect" :
            e.Name.LocalName is "rect" or "path" or "polygon" or "circle" or "ellipse");
        string background = (string?)shape?.Attribute("fill") ?? "none";
        bool member = element.Ancestors().TakeWhile(e => e != scope).Any(e => (string?)e.Attribute("class") == "member");
        if (category is "class" or "object" && !member && (string?)scope?.Attribute("data-style-fill") != "explicit")
            background = skin.Style(category, "backgroundcolor", background, (string?)scope?.Attribute("data-style-stereotype") ?? "", "header");
        if (Transparent(background)) background = skin.Get("backgroundcolor", "white");
        if (ManagedSkin.TryGradient(background, out string first, out _, out _)) background = first;
        if (Transparent(background) || !ManagedSkin.TryRgb(background, out int rgb)) rgb = 0xffffff;
        int gray = ((rgb >> 16) * 299 + ((rgb >> 8) & 255) * 587 + (rgb & 255) * 114) / 1000;
        return gray < 128 ? "#ffffff" : "#000000";

        static bool Transparent(string value) => value is "none" or "transparent" ||
            value.Length == 9 && value[0] == '#' && value.EndsWith("00", StringComparison.Ordinal) ||
            value.Length == 5 && value[0] == '#' && value[^1] == '0';
    }

    private static void Marker(XElement marker, Dictionary<string, StyleSheet.Value> style)
    {
        string color = style.TryGetValue("markercolor", out var paint) ? paint.Text : (string?)marker.Attribute("fill") ?? "none";
        marker.SetAttributeValue("fill", color);
        marker.SetAttributeValue("stroke", color);
        marker.SetAttributeValue("stroke-width", "1");
        string shape = style.TryGetValue("markershape", out var selected) ? selected.Text : "circle";
        if (shape is not ("square" or "triangle")) return;
        double x = (double)marker.Attribute("cx")!, y = (double)marker.Attribute("cy")!, radius = (double)marker.Attribute("r")!;
        marker.Attribute("cx")!.Remove(); marker.Attribute("cy")!.Remove(); marker.Attribute("r")!.Remove();
        if (shape == "square")
        {
            marker.Name = Svg + "rect";
            marker.SetAttributeValue("x", x - radius); marker.SetAttributeValue("y", y - radius);
            marker.SetAttributeValue("width", radius * 2); marker.SetAttributeValue("height", radius * 2);
        }
        else
        {
            marker.Name = Svg + "polygon";
            marker.SetAttributeValue("points", FormattableString.Invariant($"{x},{y - radius} {x - radius},{y + radius} {x + radius},{y + radius}"));
        }
    }

    private static void Diagonal(XElement rectangle, double requested)
    {
        double Read(string name) => double.TryParse((string?)rectangle.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;
        double x = Read("x"), y = Read("y"), w = Read("width"), h = Read("height"), corner = Math.Min(requested, Math.Min(Read("width"), Read("height")) / 2);
        rectangle.Name = Svg + "path";
        rectangle.SetAttributeValue("d", FormattableString.Invariant($"M{x + corner} {y}H{x + w - corner}L{x + w} {y + corner}V{y + h - corner}L{x + w - corner} {y + h}H{x + corner}L{x} {y + h - corner}V{y + corner}Z"));
        foreach (string name in new[] { "x", "y", "width", "height", "rx", "ry" }) rectangle.Attribute(name)?.Remove();
    }
}

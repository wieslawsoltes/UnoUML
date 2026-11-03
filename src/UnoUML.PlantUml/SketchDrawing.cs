using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace UnoUML.PlantUml;

internal static partial class SketchDrawing
{
    internal static void Apply(XElement root)
    {
        int seed = 1;
        foreach (var element in root.Descendants().ToArray())
        {
            if (element.AncestorsAndSelf().Any(e => e.Name.LocalName is "defs" or "filter" || (string?)e.Attribute("data-rich-paint") == "true")) continue;
            string? stroke = element.AncestorsAndSelf().Attributes("stroke").FirstOrDefault()?.Value;
            if (stroke is null or "transparent" or "none") continue;
            double Read(string name) => double.TryParse((string?)element.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;
            string? path = null;
            switch (element.Name.LocalName)
            {
                case "circle":
                case "ellipse":
                    double cx = Read("cx"), cy = Read("cy"), rx = element.Name.LocalName == "circle" ? Read("r") : Read("rx"), ry = element.Name.LocalName == "circle" ? Read("r") : Read("ry");
                    double kx = rx * 0.55228475, ky = ry * 0.55228475;
                    path = FormattableString.Invariant($"M{cx + rx} {cy}C{cx + rx} {cy + ky} {cx + kx} {cy + ry} {cx} {cy + ry}C{cx - kx} {cy + ry} {cx - rx} {cy + ky} {cx - rx} {cy}C{cx - rx} {cy - ky} {cx - kx} {cy - ry} {cx} {cy - ry}C{cx + kx} {cy - ry} {cx + rx} {cy - ky} {cx + rx} {cy}Z");
                    break;
                case "line": path = FormattableString.Invariant($"M{Read("x1")} {Read("y1")}L{Read("x2")} {Read("y2")}"); break;
                case "rect":
                    double x = Read("x"), y = Read("y"), w = Read("width"), h = Read("height"), r = Math.Min(Read("rx"), Math.Min(w, h) / 2);
                    if (r == 0) path = FormattableString.Invariant($"M{x} {y}h{w}v{h}h{-w}z");
                    else path = FormattableString.Invariant($"M{x + r} {y}H{x + w - r}Q{x + w} {y} {x + w} {y + r}V{y + h - r}Q{x + w} {y + h} {x + w - r} {y + h}H{x + r}Q{x} {y + h} {x} {y + h - r}V{y + r}Q{x} {y} {x + r} {y}Z");
                    break;
                case "polyline":
                case "polygon":
                    path = "M" + ((string?)element.Attribute("points") ?? "").Trim() + (element.Name.LocalName == "polygon" ? "Z" : "");
                    break;
                case "path": path = (string?)element.Attribute("d"); break;
            }
            if (path == null || !TrySketch(path, ref seed, out string drawn)) continue;
            if (element.Name.LocalName is "line" or "polyline" && element.Attribute("fill") == null) element.SetAttributeValue("fill", "none");
            element.Name = element.Name.Namespace + "path";
            element.SetAttributeValue("d", drawn);
            element.SetAttributeValue("stroke-linecap", "round");
            element.SetAttributeValue("stroke-linejoin", "round");
            foreach (string name in new[] { "x", "y", "x1", "y1", "x2", "y2", "width", "height", "cx", "cy", "r", "rx", "ry", "points" }) element.Attribute(name)?.Remove();
        }
        root.SetAttributeValue("data-handwritten", "true");
    }

    private static bool TrySketch(string path, ref int seed, out string drawn)
    {
        drawn = path;
        var tokens = Token().Matches(path);
        if (tokens.Count is 0 or > 16384) return false;
        var result = new StringBuilder(path.Length + tokens.Count * 5);
        double x = 0, y = 0, startX = 0, startY = 0;
        int position = 0;
        char command = '\0';
        Span<double> coordinates = stackalloc double[7];
        while (position < tokens.Count)
        {
            string token = tokens[position].Value;
            if (char.IsLetter(token[0])) { command = token[0]; position++; }
            bool relative = char.IsLower(command);
            char kind = char.ToUpperInvariant(command);
            int count = kind switch { 'M' or 'L' or 'T' => 2, 'H' or 'V' => 1, 'C' => 6, 'S' or 'Q' => 4, 'A' => 7, 'Z' => 0, _ => -1 };
            if (count < 0 || position + count > tokens.Count) return false;
            for (int i = 0; i < count; i++)
                if (!double.TryParse(tokens[position + i].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out coordinates[i])) return false;
            position += count;
            if (kind == 'M')
            {
                x = coordinates[0] + (relative ? x : 0); y = coordinates[1] + (relative ? y : 0);
                startX = x; startY = y;
                result.Append(CultureInfo.InvariantCulture, $"M{x:0.###} {y:0.###}");
                command = relative ? 'l' : 'L';
                continue;
            }
            if (kind is 'L' or 'H' or 'V' or 'Z')
            {
                double nx = kind == 'Z' ? startX : kind == 'V' ? x : coordinates[0] + (relative ? x : 0);
                double ny = kind == 'Z' ? startY : kind == 'H' ? y : coordinates[kind == 'V' ? 0 : 1] + (relative ? y : 0);
                double dx = nx - x, dy = ny - y, length = Math.Sqrt(dx * dx + dy * dy);
                double wobble = Math.Min(1.6, length * 0.025) * Noise(ref seed);
                double ox = length == 0 ? 0 : -dy / length * wobble, oy = length == 0 ? 0 : dx / length * wobble;
                result.Append(CultureInfo.InvariantCulture, $"C{x + dx / 3 + ox:0.###} {y + dy / 3 + oy:0.###} {x + 2 * dx / 3 - ox:0.###} {y + 2 * dy / 3 - oy:0.###} {nx:0.###} {ny:0.###}");
                x = nx; y = ny;
                if (kind == 'Z') { result.Append('Z'); command = '\0'; }
                continue;
            }
            result.Append(command);
            for (int i = 0; i < count; i++)
            {
                double value = coordinates[i];
                if (kind is 'C' or 'Q' && i < count - 2) value += Noise(ref seed) * 0.7;
                if (i > 0) result.Append(' ');
                result.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
            }
            x = coordinates[count - 2] + (relative ? x : 0);
            y = coordinates[count - 1] + (relative ? y : 0);
        }
        drawn = result.ToString();
        return true;
    }

    private static double Noise(ref int seed)
    {
        seed = unchecked(seed * 1664525 + 1013904223);
        return ((uint)seed >> 8) / 8388607.5 - 1;
    }

    [GeneratedRegex(@"[AaCcHhLlMmQqSsTtVvZz]|[-+]?(?:\d*\.)?\d+(?:[Ee][-+]?\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
}

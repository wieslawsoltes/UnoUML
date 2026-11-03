using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

internal static partial class MathRenderer
{
    private const string Serif = "STIX Two Math, Cambria Math, Times New Roman, serif";
    private readonly record struct Face(string Family, bool Bold = false, bool Roman = false, bool Italic = false, bool Double = false);
    private readonly record struct Placed(Box Box, double X, double Y);

    // Coordinates are in CSS pixels relative to this box's mathematical baseline.
    private sealed class Box(Expr node, double size, Face face)
    {
        internal readonly Expr Node = node;
        internal readonly double Size = size;
        internal Face Face = face;
        internal string? Text;
        internal double Width, Up, Down;
        internal readonly List<Placed> Parts = [];
        internal readonly List<string> Paths = [];
        internal void Place(Box b, double x, double baseline)
        {
            Parts.Add(new(b, x, baseline));
            Width = Math.Max(Width, x + b.Width); Up = Math.Max(Up, b.Up - baseline); Down = Math.Max(Down, b.Down + baseline);
        }
    }

    private static string? MathSvg(Expr e, string formula, int line, int column, string color, string background, double scale)
    {
        var b = Typeset(e, 21, new(Serif));
        const double margin = 5;
        double w = Math.Ceiling(b.Width + 2 * margin), h = Math.Ceiling(b.Up + b.Down + 2 * margin);
        if (w * scale > 32768 || h * scale > 32768 || w < 1 || !double.IsFinite(w + h)) return null;
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Math.Ceiling(w * scale)}\" height=\"{Math.Ceiling(h * scale)}\" viewBox=\"0 0 {w} {h}\"><title>");
        svg.Append(SvgText.Escape(formula)).Append("</title>");
        svg.Append(CultureInfo.InvariantCulture, $"<rect width=\"{w}\" height=\"{h}\" fill=\"{background}\"/><g class=\"math-expression\" data-source-line=\"{line}\" fill=\"{color}\">");
        Draw(svg, b, margin, margin + b.Up, column, color);
        return svg.Append("</g></svg>").ToString();
    }

    private static void Draw(StringBuilder svg, Box b, double x, double y, int column, string color)
    {
        var n = b.Node;
        svg.Append(CultureInfo.InvariantCulture, $"<g class=\"math-{n.Form.ToString().ToLowerInvariant()}\" data-math-column=\"{column + n.At}\" data-math-length=\"{n.Length}\" transform=\"translate({x:0.###} {y:0.###})\">");
        if (b.Text is { Length: > 0 } text)
        {
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"0\" y=\"0\" font-family=\"{b.Face.Family}\" font-size=\"{b.Size:0.###}\" textLength=\"{b.Width:0.###}\" lengthAdjust=\"spacingAndGlyphs\" xml:space=\"preserve\"");
            if (b.Face.Bold) svg.Append(" font-weight=\"bold\"");
            if (b.Face.Italic) svg.Append(" font-style=\"italic\"");
            svg.Append('>').Append(SvgText.Escape(text)).Append("</text>");
        }
        foreach (var d in b.Paths)
            svg.Append(CultureInfo.InvariantCulture, $"<path d=\"{d}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{Math.Max(.65, b.Size * .052):0.###}\" stroke-linecap=\"square\" stroke-linejoin=\"round\"/>");
        foreach (var c in b.Parts) Draw(svg, c.Box, c.X, c.Y, column, color);
        svg.Append("</g>");
    }

    private static Box Typeset(Expr e, double s, Face face)
    {
        var box = new Box(e, s, face);
        Box Child(int i, double ratio = 1) => Typeset(e.Children[i], Math.Max(5, s * ratio), face);
        switch (e.Form)
        {
            case Form.Glyph:
                bool large = e.Atom is Atom.Large or Atom.Integral && e.Value.Length == 1;
                double fontSize = large ? 1.48 * s : s;
                if (face.Double && e.Value.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new InvalidFormula();
                string text = face.Double ? DoubleStruck(e.Value) : e.Value;
                var glyph = new Box(e, fontSize, face with { Italic = face.Italic || (e.Atom == Atom.Variable && !face.Roman && !face.Double) })
                {
                    Text = text,
                    Width = Math.Max(0, fontSize * SvgText.Measure(text, 100) / 100d),
                    Up = fontSize * .81,
                    Down = fontSize * .24
                };
                if (large) glyph.Width = Math.Max(glyph.Width, fontSize * .68);
                return glyph;
            case Form.Space:
                box.Width = s * (e.Value switch { "thin" => .167, "medium" => .222, "thick" => .278, "quad" => 1d, "qquad" => 2d, "negative" => -.167, _ => .32 }); break;
            case Form.Row:
                double x = 0;
                Atom previous = Atom.Open;
                for (int i = 0; i < e.Children.Length; i++)
                {
                    var expr = e.Children[i]; var b = Child(i);
                    Atom atom = expr.Atom;
                    if (atom == Atom.Binary && (previous is Atom.Open or Atom.Relation or Atom.Binary or Atom.Punctuation || i == e.Children.Length - 1)) atom = Atom.Ordinary;
                    if (i != 0) x += Spacing(previous, atom) * s;
                    box.Place(b, x, 0); x += b.Width; previous = atom;
                }
                box.Width = Math.Max(0, x); break;
            case Form.Font:
                var styled = e.Value switch
                {
                    "bold" => face with { Bold = true, Roman = true, Italic = false },
                    "roman" => face with { Roman = true, Italic = false },
                    "italic" => face with { Italic = true },
                    "sans" => face with { Family = "sans-serif", Roman = true, Italic = false },
                    "mono" => face with { Family = "monospace", Roman = true, Italic = false },
                    "double" => face with { Double = true, Roman = true, Italic = false },
                    _ => throw new InvalidFormula()
                };
                box.Place(Typeset(e.Children[0], s, styled), 0, 0); break;
            case Form.Fraction:
                double ratio = e.Value == "dfrac" ? 1 : e.Value == "tfrac" ? .72 : .88;
                var numerator = Child(0, ratio); var denominator = Child(1, ratio);
                double w = Math.Max(numerator.Width, denominator.Width) + .36 * s, axis = -.24 * s, gap = .15 * s;
                box.Place(numerator, (w - numerator.Width) / 2, axis - gap - numerator.Down);
                box.Place(denominator, (w - denominator.Width) / 2, axis + gap + denominator.Up);
                if (e.Value != "nobar") box.Paths.Add(Path($"M{.04 * s} {axis}H{w - .04 * s}"));
                box.Width = w; break;
            case Form.Radical:
                var inner = Child(0); var degree = e.Children.Length > 1 ? Child(1, .52) : null;
                double prefix = Math.Max(0, (degree?.Width ?? 0) - .24 * s), head = .78 * s, y = -inner.Up - .13 * s;
                box.Place(inner, prefix + head + .03 * s, 0);
                double bottom = Math.Max(inner.Down, .14 * s);
                box.Width += .08 * s;
                box.Paths.Add(Path($"M{prefix} {-.02 * s}l{.15 * s} {-.1 * s}L{prefix + .36 * s} {bottom}L{prefix + head} {y}H{box.Width}"));
                if (degree != null) box.Place(degree, 0, y * .54);
                box.Up = Math.Max(box.Up, -y + .04 * s); box.Down = Math.Max(box.Down, bottom); break;
            case Form.Scripts:
                var body = Child(0); var below = Child(1, .69); var above = Child(2, .69);
                if (e.Limits)
                {
                    double extent = Math.Max(body.Width, Math.Max(below.Width, above.Width));
                    box.Place(body, (extent - body.Width) / 2, 0);
                    if (below.Width > 0) box.Place(below, (extent - below.Width) / 2, body.Down + .13 * s + below.Up);
                    if (above.Width > 0) box.Place(above, (extent - above.Width) / 2, -body.Up - .12 * s - above.Down);
                }
                else
                {
                    box.Place(body, 0, 0);
                    double hi = -Math.Max(.51 * s, body.Up * .70) - above.Down * .45;
                    double lo = Math.Max(.22 * s, body.Down * .83) + below.Up * .43;
                    if (above.Width > 0 && below.Width > 0) lo = Math.Max(lo, hi + above.Down + below.Up + .13 * s);
                    if (above.Width > 0) box.Place(above, body.Width + .045 * s, hi);
                    if (below.Width > 0) box.Place(below, body.Width + .025 * s, lo);
                }
                break;
            case Form.Fence:
                var fenced = Child(0); box.Up = Math.Max(fenced.Up + .065 * s, .79 * s); box.Down = Math.Max(fenced.Down + .05 * s, .23 * s);
                double dw = Math.Max(.30 * s, Math.Min(.64 * s, (box.Up + box.Down) * .16));
                double before = e.Value[0] == '.' ? 0 : dw + .09 * s, after = e.Value[1] == '.' ? 0 : dw + .09 * s;
                box.Place(fenced, before, 0);
                FencePath(box, e.Value[0], 0, dw);
                FencePath(box, e.Value[1], before + fenced.Width + .09 * s, dw);
                box.Width = before + fenced.Width + after; break;
            case Form.Matrix: Matrix(box, e, s, face); break;
            case Form.Accent:
                var core = Child(0); box.Place(core, 0, 0); double right = core.Width, top = -core.Up - .13 * s;
                switch (e.Value)
                {
                    case "underline": box.Down += .16 * s; box.Paths.Add(Path($"M0 {box.Down}H{right}")); break;
                    case "bar": box.Up = -top; box.Paths.Add(Path($"M0 {top}H{right}")); break;
                    case "hat": box.Up = -top + .19 * s; box.Paths.Add(Path($"M0 {top}L{right / 2} {-box.Up}L{right} {top}")); break;
                    case "tilde": box.Up = -top + .13 * s; box.Paths.Add(Path($"M0 {top}Q{right * .25} {top - .22 * s} {right * .5} {top}T{right} {top}")); break;
                    case "vec": box.Up = -top + .13 * s; box.Paths.Add(Path($"M0 {top}H{right}m{-.22 * s} {-.12 * s}l{.22 * s} {.12 * s}l{-.22 * s} {.12 * s}")); break;
                    case "dot":
                    case "ddot":
                        box.Up = -top + .08 * s;
                        if (e.Value == "dot") box.Paths.Add(Path($"M{right * .50 - .03 * s} {top}h{.06 * s}"));
                        else { box.Paths.Add(Path($"M{right * .35 - .03 * s} {top}h{.06 * s}")); box.Paths.Add(Path($"M{right * .65 - .03 * s} {top}h{.06 * s}")); }
                        break;
                    default: throw new InvalidFormula();
                }
                break;
        }
        return box;
    }
    private static string Path(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    private static double Spacing(Atom a, Atom b)
    {
        if (a == Atom.Open || b is Atom.Close or Atom.Punctuation) return 0;
        if (a == Atom.Relation || b == Atom.Relation) return .28;
        if (a == Atom.Binary || b == Atom.Binary) return .20;
        if (a is Atom.Punctuation or Atom.Function or Atom.Large or Atom.Integral || b is Atom.Function or Atom.Large or Atom.Integral) return .13;
        return 0;
    }
    private static void Matrix(Box box, Expr expr, double size, Face face)
    {
        int cols = expr.Columns, rows = expr.Children.Length / cols;
        var cells = new Box[expr.Children.Length]; var width = new double[cols]; var up = new double[rows]; var down = new double[rows];
        for (int i = 0; i < cells.Length; i++)
        {
            var c = cells[i] = Typeset(expr.Children[i], size, face);
            int col = i % cols, row = i / cols;
            width[col] = Math.Max(width[col], c.Width); up[row] = Math.Max(up[row], c.Up); down[row] = Math.Max(down[row], c.Down);
        }
        double h = up.Sum() + down.Sum() + Math.Max(0, rows - 1) * .38 * size;
        box.Up = h / 2 + .24 * size; box.Down = h - box.Up;
        double y = -box.Up;
        for (int row = 0; row < rows; row++)
        {
            double x = 0; y += up[row];
            for (int col = 0; col < cols; col++)
            {
                var c = cells[row * cols + col];
                box.Place(c, x + (expr.Value == "cases" ? 0 : (width[col] - c.Width) / 2), y);
                x += width[col] + .85 * size;
            }
            box.Width = Math.Max(box.Width, x - .85 * size); y += down[row] + .38 * size;
        }
    }
    private static void FencePath(Box b, char c, double x, double w)
    {
        if (c == '.') return;
        double y = -b.Up, z = b.Down, mid = (y + z) / 2;
        bool right = c is ')' or ']' or '}' or '⟩' or '⌋' or '⌉';
        double outer = right ? x + w : x, inner = right ? x : x + w;
        string p = c switch
        {
            '(' or ')' => Path($"M{inner} {y}Q{outer} {y + (z - y) * .17} {outer} {mid}T{inner} {z}"),
            '[' or ']' => Path($"M{inner} {y}H{outer}V{z}H{inner}"),
            '⌊' or '⌋' => Path($"M{outer} {y}V{z}H{inner}"),
            '⌈' or '⌉' => Path($"M{inner} {y}H{outer}V{z}"),
            '⟨' or '⟩' => Path($"M{inner} {y}L{outer} {mid}L{inner} {z}"),
            '{' or '}' => Path($"M{inner} {y}Q{x + w / 2} {y} {x + w / 2} {mid - (z - y) * .18}Q{x + w / 2} {mid} {outer} {mid}Q{x + w / 2} {mid} {x + w / 2} {mid + (z - y) * .18}Q{x + w / 2} {z} {inner} {z}"),
            '|' => Path($"M{x + w * .5} {y}V{z}"),
            '‖' => Path($"M{x + w * .3} {y}V{z}M{x + w * .7} {y}V{z}"),
            _ => throw new InvalidFormula()
        };
        b.Paths.Add(p);
    }
    private static string DoubleStruck(string value)
    {
        var sb = new StringBuilder();
        foreach (char c in value)
        {
            if (c is 'C' or 'H' or 'N' or 'P' or 'Q' or 'R' or 'Z')
            {
                sb.Append(c switch { 'C' => 'ℂ', 'H' => 'ℍ', 'N' => 'ℕ', 'P' => 'ℙ', 'Q' => 'ℚ', 'R' => 'ℝ', _ => 'ℤ' }); continue;
            }
            int code = char.IsAsciiLetterUpper(c) ? 0x1d538 + c - 'A' : char.IsAsciiLetterLower(c) ? 0x1d552 + c - 'a' : 0x1d7d8 + c - '0';
            sb.Append(char.ConvertFromUtf32(code));
        }
        return sb.ToString();
    }
}

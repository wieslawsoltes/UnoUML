using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

internal static partial class RailroadRenderer
{
    private const int Track = 18, Radius = 7;
    private const string RailInk = "#495065";

    private static string? Draw(List<Rule> rules, string title)
    {
        int width = Math.Max(1, SvgText.Measure(title, 18)), height = title.Length > 0 ? 44 : 16;
        foreach (var rule in rules)
        {
            if (!Measure(rule.Root)) return null;
            width = Math.Max(width, Math.Max(rule.Root.Width + Track * 2, SvgText.Measure(rule.Name, 14)));
            height += rule.Root.Above + rule.Root.Below + (rule.Name.Length > 0 ? 26 : 0) + 22;
            if (height > 32000 || width > 32000) return null;
        }
        width += 32;
        var s = new StringBuilder();
        s.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"Arial, sans-serif\">");
        int top = 16;
        if (title.Length != 0) { Label(s, width / 2, 29, title, 18, bold: true); top = 44; }
        foreach (var rule in rules)
        {
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"production\" data-source-line=\"{rule.Line}\">");
            if (rule.Name.Length > 0)
            {
                s.Append("<title>").Append(SvgText.Escape(rule.Name)).Append("</title>");
                Label(s, 16, top + 14, rule.Name, 14, "start", true); top += 26;
            }
            var rail = rule.Root;
            int y = top + rail.Above, x = 16 + Track, end = x + rail.Width;
            Path(s, $"M16 {y}H{x}M{end} {y}h{Track}"); Arrow(s, end + Track - 1, y, 1);
            s.Append(CultureInfo.InvariantCulture, $"<circle cx=\"16\" cy=\"{y}\" r=\"2.5\" fill=\"{RailInk}\"/>");
            DrawRail(s, rail, x, y); s.Append("</g>");
            top += rail.Above + rail.Below + 22;
        }
        return s.Append("</svg>").ToString();
    }

    private static bool Measure(Rail node)
    {
        foreach (var child in node.Children)
            if (!Measure(child)) return false;
        switch (node.Kind)
        {
            case Kind.Sequence:
                node.Width = node.Children.Sum(c => c.Width) + (node.Children.Length - 1) * Track;
                node.Above = node.Children.Max(c => c.Above); node.Below = node.Children.Max(c => c.Below);
                break;
            case Kind.Choice:
                node.Width = node.Children.Max(c => c.Width) + 4 * Track;
                node.Above = node.Children[0].Above;
                node.Below = node.Children.Sum(c => c.Above + c.Below) + Track * (node.Children.Length - 1) - node.Above;
                break;
            case Kind.Set:
                node.Width = Math.Max(34, node.Children.Max(c => SvgText.Measure(c.Text, 13)) + 22);
                node.Above = 14;
                node.Below = 14 + (node.Children.Length - 1) * 18 + (node.Text == "^" ? 18 : 0);
                break;
            case Kind.Option:
            case Kind.Loop:
            case Kind.Capture:
                var inner = node.Children[0];
                node.Width = Math.Max(inner.Width, SvgText.Measure(node.Text, 11) + 20) + Track * 2;
                node.Above = inner.Above + (node.Kind == Kind.Option ? Track : node.Kind == Kind.Capture ? 6 : 0);
                node.Below = inner.Below + (node.Kind == Kind.Loop ? (node.Text.Length > 0 ? 36 : Track) : node.Kind == Kind.Capture ? 6 : 0);
                break;
            default:
                node.Width = Math.Max(28, SvgText.Measure(node.Text, 14) + 20); node.Above = node.Below = 14;
                break;
        }
        return node.Width <= 31968 && node.Above + node.Below <= 31968;
    }

    private static void DrawRail(StringBuilder s, Rail node, int x, int y)
    {
        int right = x + node.Width, middle = x + node.Width / 2;
        switch (node.Kind)
        {
            case Kind.Sequence:
                for (int i = 0; i < node.Children.Length; i++)
                {
                    var term = node.Children[i];
                    if (i > 0) Path(s, $"M{x - Track} {y}H{x}");
                    DrawRail(s, term, x, y);
                    x += term.Width + Track;
                }
                return;
            case Kind.Choice:
                int branchY = y;
                for (int i = 0; i < node.Children.Length; i++)
                {
                    var branch = node.Children[i];
                    if (i > 0) branchY += node.Children[i - 1].Below + Track + branch.Above;
                    int bx = middle - branch.Width / 2;
                    if (i == 0) Path(s, $"M{x} {y}H{bx}M{bx + branch.Width} {y}H{right}");
                    else
                    {
                        // Keep every alternative independently connected to both junctions.
                        Path(s, $"M{x} {y}h{Track - Radius}q{Radius} 0 {Radius} {Radius}V{branchY - Radius}q0 {Radius} {Radius} {Radius}H{bx}M{bx + branch.Width} {branchY}H{right - Track - Radius}q{Radius} 0 {Radius} -{Radius}V{y + Radius}q0 -{Radius} {Radius} -{Radius}H{right}");
                        Arrow(s, bx - 2, branchY, 1);
                    }
                    DrawRail(s, branch, bx, branchY);
                }
                return;
            case Kind.Option:
            case Kind.Loop:
            case Kind.Capture:
                var inner = node.Children[0];
                int start = middle - inner.Width / 2, end = start + inner.Width;
                Path(s, $"M{x} {y}H{start}M{end} {y}H{right}");
                if (node.Kind == Kind.Option)
                {
                    int top = y - node.Above + 3;
                    Path(s, $"M{x} {y}q{Radius} 0 {Radius} -{Radius}V{top + Radius}q0 -{Radius} {Radius} -{Radius}H{right - 2 * Radius}q{Radius} 0 {Radius} {Radius}V{y - Radius}q0 {Radius} {Radius} {Radius}");
                    Arrow(s, middle, top, 1);
                }
                if (node.Kind == Kind.Loop)
                {
                    int bottom = y + node.Below - 3;
                    Path(s, $"M{right} {y}q-{Radius} 0 -{Radius} {Radius}V{bottom - Radius}q0 {Radius} -{Radius} {Radius}H{x + 2 * Radius}q-{Radius} 0 -{Radius} -{Radius}V{y + Radius}q0 -{Radius} -{Radius} -{Radius}");
                    Arrow(s, middle, bottom, -1);
                    if (node.Text.Length != 0) Label(s, middle, bottom - 6, node.Text, 11);
                }
                if (node.Kind == Kind.Capture)
                    s.Append(CultureInfo.InvariantCulture, $"<g><title>Capture {node.Text}</title><rect x=\"{start - 5}\" y=\"{y - node.Above + 1}\" width=\"{inner.Width + 10}\" height=\"{node.Above + node.Below - 2}\" rx=\"6\" fill=\"none\" stroke=\"#82879d\" stroke-dasharray=\"4 3\"/></g>");
                DrawRail(s, inner, start, y); return;
            case Kind.Set:
                BeginLeaf(s, node, "character-class");
                Box(s, x, y - 14, node.Width, node.Above + node.Below, false);
                int row = y + 5;
                if (node.Text == "^") { Label(s, middle, row, "not", 12, bold: true); row += 18; }
                foreach (var token in node.Children)
                {
                    Label(s, middle, row, token.Text, 13); row += 18;
                }
                s.Append("</g>"); return;
            default:
                BeginLeaf(s, node, node.Kind == Kind.Reference ? "nonterminal" : "token");
                Box(s, x, y - 14, node.Width, 28, node.Kind is Kind.Reference or Kind.Special);
                Label(s, middle, y + 5, node.Text, 14);
                s.Append("</g>"); return;
        }
    }

    private static void BeginLeaf(StringBuilder s, Rail node, string kind) =>
        s.Append(CultureInfo.InvariantCulture, $"<g class=\"rail-{kind}\" data-source-line=\"{node.Line}\">");
    private static void Box(StringBuilder s, int x, int y, int width, int height, bool round) =>
        s.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{y}\" width=\"{width}\" height=\"{height}\" rx=\"{(round ? 5 : 1)}\" fill=\"{(round ? "#eff0f7" : "white")}\" stroke=\"{RailInk}\"/>");
    private static void Path(StringBuilder s, FormattableString path) =>
        s.Append(CultureInfo.InvariantCulture, $"<path d=\"{path.ToString(CultureInfo.InvariantCulture)}\" stroke=\"{RailInk}\" fill=\"none\" stroke-linejoin=\"round\"/>");
    private static void Arrow(StringBuilder s, int x, int y, int direction) =>
        s.Append(CultureInfo.InvariantCulture, $"<path d=\"M{x} {y}l{-direction * 5} -3v6Z\" fill=\"{RailInk}\"/>");
    private static void Label(StringBuilder s, int x, int y, string text, int size, string anchor = "middle", bool bold = false) =>
        s.Append(CultureInfo.InvariantCulture, $"<text x=\"{x}\" y=\"{y}\" text-anchor=\"{anchor}\" font-size=\"{size}\" fill=\"#292c39\"{(bold ? " font-weight=\"bold\"" : "")}>{SvgText.Escape(text)}</text>");
}

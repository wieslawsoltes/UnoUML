using System.Globalization;

namespace UnoUML.PlantUml;

/// <summary>Standalone PlantUML scientific equations, typeset locally into accessible SVG.</summary>
internal static partial class MathRenderer
{
    private enum Form { Glyph, Row, Fraction, Radical, Scripts, Fence, Matrix, Accent, Font, Space }
    private enum Atom { Ordinary, Variable, Function, Binary, Relation, Punctuation, Large, Integral, Open, Close }
    private sealed record Expr(Form Form, int At, int Length, string Value, Atom Atom, Expr[] Children)
    {
        internal int Columns { get; init; }
        internal bool Limits { get; init; }
    }

    internal static string? TryRender(string source, bool latex)
    {
        string start = latex ? "@startlatex" : "@startmath", end = latex ? "@endlatex" : "@endmath";
        string color = "#000000", background = "#ffffff";
        double scale = 1;
        string? formula = null;
        int line = -1, formulaLine = 0, column = 0, state = 0;
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            line++;
            var s = raw.Trim();
            if (s.IsEmpty || s[0] == '\'') continue;
            if (state == 0 && s.SequenceEqual(start)) { state = 1; continue; }
            if (state == 1 && s.SequenceEqual(end)) { state = 2; continue; }
            if (state != 1) return null;
            int split = s.IndexOfAny(' ', '\t');
            var key = split < 0 ? "" : s[..split].ToString().ToLowerInvariant();
            if (key is "scale" or "dpi")
            {
                if (!double.TryParse(s[split..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return null;
                scale = key == "dpi" ? value / 96 : value;
                if (!double.IsFinite(scale) || scale is < .05 or > 16) return null;
            }
            else if (key is "color" or "backgroundcolor")
            {
                var paint = MathPaint(s[split..].Trim().ToString());
                if (paint == null) return null;
                if (key == "color") color = paint; else background = paint;
            }
            else
            {
                // PSystemMath/PSystemLatex consume one formula line and separate configuration lines.
                if (formula != null || s.Length > 16384 || s[0] is '!' or '@') return null;
                formula = s.ToString(); formulaLine = line; column = raw.Length - raw.TrimStart().Length;
            }
        }
        if (state != 2 || formula == null) return null;
        try
        {
            var expr = new Parser(formula, latex).Parse();
            return MathSvg(expr, formula, formulaLine, column, color, background, scale);
        }
        catch (InvalidFormula) { return null; }
    }

    private static string? MathPaint(string s)
    {
        if (s.Length is 4 or 7 && s[0] == '#' && s.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0) return s;
        if (s.StartsWith('#')) s = s[1..];
        return s.ToLowerInvariant() switch
        {
            "white" or "black" or "blue" or "red" or "green" or "gray" or "grey" or "yellow" or "orange" or "purple" or "navy" or "silver" or "maroon" or "teal" => s.ToLowerInvariant(),
            _ => null
        };
    }

    private sealed class InvalidFormula : Exception { }
    private enum Lex { End, Glyph, Cmd, Left, Right, Sub, Sup, Divide, Comma, Bar, Align, Break, BeginEnv, EndEnv, Text }
    private readonly record struct Token(Lex Kind, string Value, int At, int Length, Atom Class = Atom.Ordinary);
    private static Expr Node(Form form, Token t, string value, params Expr[] children) =>
        new(form, t.At, t.Length, value, t.Class, children);
    private static Expr Row(List<Expr> items, int at) => items.Count == 1 ? items[0] :
        new(Form.Row, at, 0, "", Atom.Ordinary, items.ToArray());

    private sealed class Parser
    {
        private readonly List<Token> tokens;
        private readonly bool latex;
        private int cursor, depth;
        private Token Next => tokens[cursor];
        private Token Take() => tokens[cursor++];
        internal Parser(string source, bool tex) { latex = tex; tokens = Tokenize(source, tex); }
        internal Expr Parse()
        {
            var e = Sequence();
            if (Next.Kind != Lex.End || e.Form == Form.Row && e.Children.Length == 0) throw new InvalidFormula();
            return e;
        }
        private Expr Sequence(bool stopBar = false)
        {
            int at = Next.At;
            var terms = new List<Expr>();
            while (Next.Kind is not (Lex.End or Lex.Right or Lex.Align or Lex.Break or Lex.EndEnv) &&
                !(Next is { Kind: Lex.Cmd, Value: "right" } || stopBar && Next.Kind == Lex.Bar))
            {
                Expr e = Scripted();
                if (Next.Kind == Lex.Divide)
                {
                    var slash = Take();
                    e = Node(Form.Fraction, slash, "", StripFence(e), StripFence(Scripted()));
                    if (Next.Kind == Lex.Divide) throw new InvalidFormula();
                }
                terms.Add(e);
            }
            return Row(terms, at);
        }
        private Expr Scripted()
        {
            var start = Next;
            var b = Primary();
            bool limits = b.Atom == Atom.Large;
            if (Next.Kind == Lex.Cmd && Next.Value is "limits" or "nolimits")
            {
                if (b.Atom is not (Atom.Large or Atom.Integral)) throw new InvalidFormula();
                limits = Take().Value == "limits";
            }
            Expr? sub = null, sup = null;
            while (Next.Kind is Lex.Sub or Lex.Sup)
            {
                var script = Take();
                if (script.Kind == Lex.Sub) { if (sub != null) throw new InvalidFormula(); sub = Argument(); }
                else { if (sup != null) throw new InvalidFormula(); sup = Argument(); }
            }
            if (sub == null && sup == null) return b;
            var scripted = new Expr(Form.Scripts, b.At, b.Length, "", b.Atom, [b, sub ?? Row([], b.At), sup ?? Row([], b.At)]) { Limits = limits };
            if (!latex && IsAsciiFunction(start) && AcceptsArgument(start))
            {
                if (++depth > 64) throw new InvalidFormula();
                try { return Row([scripted, Scripted()], start.At); }
                finally { depth--; }
            }
            return scripted;
        }
        private Expr Argument()
        {
            if (!latex && Next is { Kind: Lex.Glyph, Value: "−" })
            {
                var neg = Take();
                return Row([Node(Form.Glyph, neg, "−"), StripFence(Primary())], neg.At);
            }
            return StripFence(Primary());
        }
        private Expr StripFence(Expr e) => !latex && e.Form == Form.Fence && (e.Value is "()" or "[]" or "{}") ? e.Children[0] : e;
        private Expr Primary()
        {
            if (++depth > 64 || Next.Kind == Lex.End) throw new InvalidFormula();
            try
            {
                var t = Take();
                switch (t.Kind)
                {
                    case Lex.Glyph:
                        var glyph = Node(Form.Glyph, t, t.Value);
                        if (!latex && IsAsciiFunction(t) && AcceptsArgument(t)) return Row([glyph, Primary()], t.At);
                        return glyph;
                    case Lex.Comma:
                    case Lex.Bar:
                    case Lex.Text: return Node(Form.Glyph, t, t.Value);
                    case Lex.Left:
                        var content = Sequence();
                        if (Next.Kind != Lex.Right || Closing(t.Value) != Next.Value) throw new InvalidFormula();
                        Take();
                        if (latex && t.Value == "{") return content;
                        return Fence(t, t.Value, Closing(t.Value), content);
                    case Lex.BeginEnv: return Environment(t);
                    case Lex.Cmd: return Command(t);
                    default: throw new InvalidFormula();
                }
            }
            finally { depth--; }
        }
        private static bool IsAsciiFunction(Token t) => t.Class == Atom.Function && t.Value is not ("or" or "if" or "and" or "dim" or "mod" or "lub" or "glb") || t.Value is "f" or "g";
        private bool AcceptsArgument(Token t) =>
            (Next.Kind is Lex.Left or Lex.Glyph or Lex.Text or Lex.Cmd) &&
            (t.Value is not ("f" or "g") || Next is { Kind: Lex.Left, Value: "(" });

        private Expr Command(Token t)
        {
            switch (t.Value)
            {
                case "frac": case "dfrac": case "tfrac": return Node(Form.Fraction, t, t.Value, Argument(), Argument());
                case "binom": return Node(Form.Fence, t, "()", Node(Form.Fraction, t, "nobar", Argument(), Argument()));
                case "root": { var index = Argument(); return Node(Form.Radical, t, "", Argument(), index); }
                case "sqrt":
                    if (latex && Next is { Kind: Lex.Left, Value: "[" })
                    {
                        Take(); var index = Sequence();
                        if (Next is not { Kind: Lex.Right, Value: "]" }) throw new InvalidFormula();
                        Take(); return Node(Form.Radical, t, "", Argument(), index);
                    }
                    return Node(Form.Radical, t, "", Argument());
                case "left":
                    string left = Delimiter(); var middle = Sequence();
                    if (Next is not { Kind: Lex.Cmd, Value: "right" }) throw new InvalidFormula();
                    Take(); return Node(Form.Fence, t, left + Delimiter(), middle);
                case "abs":
                case "norm":
                case "floor":
                case "ceil":
                    return Node(Form.Fence, t, t.Value switch { "abs" => "||", "norm" => "‖‖", "floor" => "⌊⌋", _ => "⌈⌉" }, Argument());
                case "hat":
                case "tilde":
                case "vec":
                case "bar":
                case "underline":
                case "dot":
                case "ddot":
                    return Node(Form.Accent, t, t.Value, Argument());
                case "bold":
                case "roman":
                case "italic":
                case "sans":
                case "mono":
                case "double":
                    return Node(Form.Font, t, t.Value, Argument());
                case "thin":
                case "medium":
                case "thick":
                case "quad":
                case "qquad":
                case "negative":
                case "space":
                    return Node(Form.Space, t, t.Value);
                default: throw new InvalidFormula();
            }
        }
        private string Delimiter()
        {
            var t = Take();
            if (t.Kind is Lex.Left or Lex.Right or Lex.Bar || t.Kind == Lex.Glyph && ".{}‖⌊⌋⌈⌉⟨⟩|<>".Contains(t.Value, StringComparison.Ordinal))
                return t.Value switch { "<" => "⟨", ">" => "⟩", _ => t.Value };
            throw new InvalidFormula();
        }
        private Expr Fence(Token t, string left, string right, Expr content)
        {
            // ASCIIMath recognises a rectangular sequence of comma-separated bracketed rows.
            if (!latex && content.Form == Form.Row && content.Children.Length >= 3 && content.Children[0].Form == Form.Fence)
            {
                var outer = content.Children;
                bool matrix = outer.Length % 2 == 1 && outer.Where((_, i) => i % 2 == 1).All(n => n.Form == Form.Glyph && n.Value == ",")
                    && outer.Where((_, i) => i % 2 == 0).All(n => n.Form == Form.Fence && n.Value == outer[0].Value);
                if (matrix)
                {
                    int cols = 0;
                    var cells = new List<Expr>();
                    foreach (var row in outer.Where((_, i) => i % 2 == 0))
                    {
                        var units = row.Children[0].Form == Form.Row ? row.Children[0].Children : [row.Children[0]];
                        var cell = new List<Expr>(); int n = 1;
                        foreach (var unit in units)
                        {
                            if (unit.Form != Form.Glyph || unit.Value != ",") { cell.Add(unit); continue; }
                            cells.Add(Row(cell, t.At)); cell.Clear(); n++;
                        }
                        cells.Add(Row(cell, t.At));
                        if (cols != 0 && cols != n) throw new InvalidFormula();
                        cols = n;
                    }
                    if (cols > 32 || cells.Count > 512) throw new InvalidFormula();
                    content = new(Form.Matrix, t.At, t.Length, "", Atom.Ordinary, cells.ToArray()) { Columns = cols };
                }
            }
            return Node(Form.Fence, t, left + right, content);
        }
        private Expr Environment(Token t)
        {
            string fence = t.Value switch { "matrix" => "..", "pmatrix" => "()", "bmatrix" => "[]", "Bmatrix" => "{}", "vmatrix" => "||", "Vmatrix" => "‖‖", "cases" => "{.", _ => throw new InvalidFormula() };
            var cells = new List<Expr>(); int cols = 0, columns = 0;
            while (true)
            {
                cells.Add(Sequence()); columns++;
                if (columns > 32 || cells.Count > 512) throw new InvalidFormula();
                if (Next.Kind == Lex.Align) { Take(); continue; }
                if (cols == 0) cols = columns;
                if (cols != columns) throw new InvalidFormula();
                columns = 0;
                if (Next.Kind == Lex.Break) { Take(); if (Next.Kind != Lex.EndEnv) continue; }
                if (Next.Kind != Lex.EndEnv || Take().Value != t.Value) throw new InvalidFormula();
                break;
            }
            var grid = new Expr(Form.Matrix, t.At, t.Length, t.Value, Atom.Ordinary, cells.ToArray()) { Columns = cols };
            return Node(Form.Fence, t, fence, grid);
        }
    }
    private static string Closing(string s) => s switch { "(" => ")", "[" => "]", "{" => "}", "⟨" => "⟩", "." => ".", _ => throw new InvalidFormula() };
}

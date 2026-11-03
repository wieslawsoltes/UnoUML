using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

/// <summary>Bounded, source-aware Salt parsing with intrinsic nested layout and a separate popup paint pass.</summary>
internal static partial class SaltRenderer
{
    private enum Widget { Label, Input, Button, Radio, Check, Choice, Space, Separator, Group, Tab, Menu, Tree }
    private sealed class Cell
    {
        internal Widget Type;
        internal string Text = "";
        internal StyleLabel? Label;
        internal bool Checked;
        internal int Width, Height, Start, Length, TokenStart, MarkerLength, Line, Column, ColumnIndex, Span = 1, Depth;
        internal char Separator;
        internal Form? Form;
        internal List<Cell>? Options;
        internal string Category => "salt-widget salt-" + Type.ToString().ToLowerInvariant() + " " + Type.ToString().ToLowerInvariant();
    }

    private sealed class Form
    {
        internal readonly List<List<Cell>> Rows = [];
        internal int[] Widths = [], Heights = [];
        internal string Kind = "grid";
        internal char Grid;
        internal Cell? Title;
        internal List<(Cell Target, List<Cell> Items)>? Popups;
        internal int Width, Height, Header, Right, Bottom;
    }

    private readonly record struct Token(string Text, int Start, int Line, int Column, bool NewRow);

    internal static string? TryRender(string source)
    {
        var tokens = Tokenize(source);
        if (tokens == null) return null;
        int position = 0;
        var form = ParseForm(tokens, ref position, 0);
        if (form == null || position != tokens.Count) return null;
        var body = new StringBuilder(128 + 180 * tokens.Count);
        var overlays = new StringBuilder();
        int width = form.Width + 2, height = form.Height + 2;
        DrawForm(body, overlays, form, 1, 1, ref width, ref height);
        if (width > 32768 || height > 32768) return null;
        var svg = new StringBuilder(body.Length + overlays.Length + 180);
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"sans-serif\" font-size=\"12\">");
        return svg.Append(body).Append(overlays).Append("</svg>").ToString();
    }

    private static Cell? ParseCell(Token token, Widget? forced = null)
    {
        string raw = token.Text;
        var cell = new Cell { TokenStart = token.Start, Start = token.Start, Line = token.Line, Column = token.Column };
        if (forced == null && raw is "" or ".") { cell.Type = Widget.Space; cell.Width = 4; cell.Height = 16; return cell; }
        cell.Type = forced ?? Widget.Label;
        int begin = 0, end = raw.Length;
        if (forced == null)
        {
            int marker = raw.Length >= 2 && raw[0] is '[' or '(' ? MarkerLength(raw) : 0;
            if (marker > 0)
            {
                cell.Type = raw[0] == '(' ? Widget.Radio : Widget.Check;
                cell.MarkerLength = marker;
                cell.Checked = marker == 3 && char.ToUpperInvariant(raw[1]) == 'X';
                begin = marker;
                while (begin < end && char.IsWhiteSpace(raw[begin])) begin++;
            }
            else if (raw.Length > 1 && raw[0] == '[' && raw[^1] == ']' && !raw.StartsWith("[[", StringComparison.Ordinal))
            { cell.Type = Widget.Button; begin = 1; end--; }
            else if (raw.Length > 1 && raw[0] == '"' && raw[^1] == '"')
            { cell.Type = Widget.Input; begin = 1; end--; }
            else if (raw.Length > 1 && raw[0] == '^' && raw[^1] == '^')
            { cell.Type = Widget.Choice; begin = 1; end = raw.IndexOf('^', 1); }
            else if (raw.Length >= 2 && raw[0] is '.' or '-' or '=' or '~' && raw.StartsWith(new string(raw[0], 2), StringComparison.Ordinal) && raw.EndsWith(new string(raw[0], 2), StringComparison.Ordinal))
            { cell.Type = Widget.Separator; cell.Separator = raw[0]; cell.Width = 20; cell.Height = 10; return cell; }
        }
        if (cell.Type == Widget.Tree)
        {
            while (begin < end && raw[begin] == '+') begin++;
            cell.Depth = begin;
            while (begin < end && char.IsWhiteSpace(raw[begin])) begin++;
        }
        string text = Decode(raw[begin..end]);
        if (text.Length > 4096 || !SvgText.IsSupported(text) && !(cell.Type == Widget.Input && text.All(c => c == '*'))) return null;
        if (forced == null && cell.Type == Widget.Label && text.StartsWith("<<", StringComparison.Ordinal)) return null;
        if (cell.Type == Widget.Input && text.Contains('"') || cell.Type == Widget.Button && text.Contains('[')) return null;
        cell.Text = text.TrimEnd();
        cell.Start = token.Start + begin;
        cell.Length = raw[begin..end].TrimEnd().Length;
        string display = cell.Type == Widget.Input && text.All(c => c == '*')
            ? string.Concat(Enumerable.Repeat("<U+002A>", text.Length)) : cell.Text;
        cell.Label = StyleLabel.Measure(display, cell.Category, 12);
        cell.Width = cell.Type switch
        {
            Widget.Button or Widget.Input => Math.Max(text.Length * cell.Label.Size * 2 / 3, cell.Label.Width) + 11,
            Widget.Radio or Widget.Check or Widget.Choice => cell.Label.Width + 24,
            Widget.Tree => cell.Label.Width + 8 + 12 * Math.Max(0, cell.Depth),
            Widget.Tab or Widget.Menu => cell.Label.Width + 16,
            _ => cell.Label.Width + 6
        };
        cell.Height = Math.Max(cell.Type switch { Widget.Button or Widget.Tab => 25, Widget.Input or Widget.Choice or Widget.Menu => 19, _ => 16 },
            cell.Label.Height + (cell.Type is Widget.Button or Widget.Tab ? 10 : 4));
        if (cell.Type == Widget.Choice)
        {
            cell.Options = [];
            int cursor = end + 1;
            while (cursor < raw.Length)
            {
                int next = raw.IndexOf('^', cursor);
                if (next < 0) break;
                string item = raw[cursor..next];
                int leading = item.Length - item.TrimStart().Length;
                if (!string.IsNullOrWhiteSpace(item))
                {
                    var option = ParseCell(new(item.Trim(), token.Start + cursor + leading, token.Line, token.Column + cursor + leading, true), Widget.Label);
                    if (option == null || cell.Options.Count == 128) return null;
                    cell.Options.Add(option);
                }
                cursor = next + 1;
            }
        }
        return cell;
    }

    private static int MarkerLength(string token)
    {
        char close = token[0] == '[' ? ']' : ')';
        if (token[1] == close) return 2;
        return token.Length > 2 && token[2] == close && token[1] is ' ' or 'X' or 'x' ? 3 : 0;
    }

    private static string Decode(string text) => text.Replace("\\|", "|", StringComparison.Ordinal)
        .Replace("\\{", "{", StringComparison.Ordinal).Replace("\\}", "}", StringComparison.Ordinal);

    private static void Rule(StringBuilder s, int x1, int y1, int x2, int y2, string extra = "") =>
        s.Append(CultureInfo.InvariantCulture, $"<line x1=\"{x1}\" y1=\"{y1}\" x2=\"{x2}\" y2=\"{y2}\" stroke=\"#000\"{extra}/>");
}

using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

/// <summary>Intrinsic two-pass layout for the basic Salt form grammar. Source columns refer to the original row.</summary>
internal static class SaltRenderer
{
    private enum Widget { Label, Input, Button, Radio, Check, Choice, Space }
    private sealed record Cell(Widget Type, string Text, bool Checked, int Width, int Height, int Offset)
    {
        internal StyleLabel Label = null!;
        internal int Start, Length;
        internal string Category => "salt-widget salt-" + Type.ToString().ToLowerInvariant() + " " + Type.ToString().ToLowerInvariant();
    }
    private sealed record Row(List<Cell> Cells, int Height, int Line);

    internal static string? TryRender(string source)
    {
        var rows = new List<Row>();
        var widths = new List<int>();
        int phase = 0, index = -1, total = 0, nextOffset = 0;
        char grid = ' ';
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            index++;
            int lineOffset = nextOffset;
            nextOffset += raw.Length;
            if (nextOffset < source.Length && source[nextOffset] == '\r') nextOffset++;
            if (nextOffset < source.Length && source[nextOffset] == '\n') nextOffset++;
            var line = raw.Trim();
            if (line.IsEmpty || line[0] == '\'') continue;
            if (phase == 0) { if (!line.SequenceEqual("@startsalt")) return null; phase++; continue; }
            if (phase == 1)
            {
                if (line.Length is < 1 or > 2 || line[0] != '{') return null;
                if (line.Length == 2 && "#!-+".IndexOf(grid = line[1]) < 0) return null;
                phase++; continue;
            }
            if (phase == 3) { if (!line.SequenceEqual("@endsalt")) return null; phase++; continue; }
            if (phase != 2) return null;
            if (line.SequenceEqual("}")) { phase++; continue; }
            if (line.StartsWith("!") || line.StartsWith("@") || line[0] is '{' or '}' || line.Length > 8192 || rows.Count >= 1024) return null;
            foreach (string command in Reserved)
                if (line.StartsWith(command, StringComparison.OrdinalIgnoreCase)) return null;
            var cells = new List<Cell>();
            int height = 0;
            foreach (var part in raw.Split('|'))
            {
                var original = raw[part];
                var value = original.Trim();
                if (cells.Count >= 128 || ++total > 8192) return null;
                int offset = part.Start.GetOffset(raw.Length) + original.Length - original.TrimStart().Length;
                if (Parse(value.ToString(), offset, lineOffset) is not {} cell) return null;
                if (widths.Count == cells.Count) widths.Add(cell.Width);
                else widths[cells.Count] = Math.Max(widths[cells.Count], cell.Width);
                cells.Add(cell);
                height = Math.Max(height, cell.Height);
            }
            rows.Add(new(cells, height, index));
        }
        if (phase != 4 || rows.Count == 0) return null;
        int width = widths.Sum() + 2, fullHeight = rows.Sum(r => r.Height) + 2;
        if (width > 32768 || fullHeight > 32768) return null;
        var s = new StringBuilder(128 + 180 * total);
        s.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{fullHeight}\" viewBox=\"0 0 {width} {fullHeight}\" font-family=\"sans-serif\" font-size=\"12\">");
        int y = 1;
        foreach (var row in rows)
        {
            int x = 1;
            for (int i = 0; i < row.Cells.Count; i++)
            {
                var cell = row.Cells[i];
                if (cell.Type != Widget.Space)
                {
                    s.Append(CultureInfo.InvariantCulture, $"<g class=\"salt-widget salt-{cell.Type.ToString().ToLowerInvariant()}\" data-source-line=\"{row.Line}\" data-source-column=\"{cell.Offset}\" data-column-index=\"{i}\" data-edit-start=\"{cell.Start}\" data-edit-length=\"{cell.Length}\" data-edit-kind=\"salt-text\" data-edit-role=\"label\"");
                    StyleLabel.Scope(s, cell.Category);
                    s.Append('>');
                    Draw(s, cell, x, y + (row.Height - cell.Height) / 2);
                    s.Append("</g>");
                }
                x += widths[i];
            }
            y += row.Height;
            if (grid is '#' or '-') Rule(s, 1, y, width - 1, y);
        }
        if (grid is '#' or '!')
        {
            int x = 1;
            foreach (int column in widths) { x += column; Rule(s, x, 1, x, fullHeight - 1); }
        }
        if (grid is '#' or '+' or '-') { Rule(s, 1, 1, width - 1, 1); if (grid == '+') Rule(s, 1, fullHeight - 1, width - 1, fullHeight - 1); }
        if (grid is '#' or '+' or '!') { Rule(s, 1, 1, 1, fullHeight - 1); if (grid == '+') Rule(s, width - 1, 1, width - 1, fullHeight - 1); }
        return s.Append("</svg>").ToString();
    }

    private static readonly string[] Reserved = ["title ", "header ", "footer ", "caption ", "legend", "skinparam", "sprite ", "scale ", "hide ", "show "];

    private static Cell? Parse(string token, int offset, int lineOffset)
    {
        if (token is "" or ".") return new(Widget.Space, "", false, 4, 16, offset);
        string text = token;
        int textOffset = 0;
        Widget kind;
        bool active = false;
        if (Mark("[]") || Mark("[X]") || Mark("()") || Mark("(X)"))
        {
            kind = token[0] == '(' ? Widget.Radio : Widget.Check;
            active = token.Length > 2 && char.ToUpperInvariant(token[1]) == 'X';
            textOffset = active ? 3 : 2;
            while (textOffset < token.Length && char.IsWhiteSpace(token[textOffset])) textOffset++;
            text = token[textOffset..];
        }
        else if (token.Length > 1 && token[0] == '[' && token[^1] == ']' && !token.StartsWith("[[", StringComparison.Ordinal)) { kind = Widget.Button; text = token[1..^1]; textOffset = 1; }
        else if (token.Length > 1 && token[0] == '"' && token[^1] == '"') { kind = Widget.Input; text = token[1..^1]; textOffset = 1; }
        else if (token.Length > 1 && token[0] == '^' && token[^1] == '^') { kind = Widget.Choice; text = token[1..^1]; textOffset = 1; }
        else kind = Widget.Label;
        if (text.Length > 4096 || !SvgText.IsSupported(text) && !(kind == Widget.Input && text.All(c => c == '*'))) return null;
        if (kind == Widget.Label && (text == "*" || text[0] is '(' or '{' or '}' || text.StartsWith("..") || text.StartsWith("=="))) return null;
        if (kind == Widget.Choice && text.Contains('^') || kind == Widget.Input && text.Contains('"') || kind == Widget.Button && text.Contains('[')) return null;
        string display = text.TrimEnd();
        if (kind == Widget.Input && text.All(c => c == '*')) display = string.Concat(Enumerable.Repeat("<U+002A>", text.Length));
        string category = "salt-widget salt-" + kind.ToString().ToLowerInvariant() + " " + kind.ToString().ToLowerInvariant();
        var label = StyleLabel.Measure(display, category, 12);
        int width = kind switch
        {
            Widget.Button or Widget.Input => Math.Max(text.Length * label.Size * 2 / 3, label.Width) + 11,
            Widget.Radio or Widget.Check or Widget.Choice => label.Width + 24,
            _ => label.Width + 6
        };
        int height = Math.Max(kind switch { Widget.Button => 25, Widget.Input or Widget.Choice => 19, _ => 16 },
            label.Height + (kind == Widget.Button ? 10 : 4));
        return new(kind, text, active, width, height, offset)
        {
            Label = label, Start = lineOffset + offset + textOffset, Length = text.TrimEnd().Length
        };

        bool Mark(string marker) => token.StartsWith(marker, StringComparison.OrdinalIgnoreCase) &&
                                    (token.Length == marker.Length || char.IsWhiteSpace(token[marker.Length]));
    }

    private static void Draw(StringBuilder s, Cell c, int x, int y)
    {
        int labelX = x + 1, labelWidth = c.Width - 2;
        int center = y + c.Height / 2;
        switch (c.Type)
        {
            case Widget.Input:
                Rule(s, x + 1, y + c.Height - 3, x + c.Width - 4, y + c.Height - 3);
                Rule(s, x + 1, y + c.Height - 6, x + 1, y + c.Height - 3);
                Rule(s, x + c.Width - 4, y + c.Height - 6, x + c.Width - 4, y + c.Height - 3);
                labelX = x + 3; labelWidth = c.Width - 7; break;
            case Widget.Button:
                s.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x + 3}\" y=\"{y + 3}\" width=\"{c.Width - 7}\" height=\"{c.Height - 7}\" rx=\"5\" ry=\"5\" stroke=\"#000\" stroke-width=\"2.5\" fill=\"#EEE\"/>");
                labelX = x + 4; labelWidth = c.Width - 9; break;
            case Widget.Check:
                s.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x + 3}\" y=\"{center - 5}\" width=\"10\" height=\"10\" stroke=\"#000\" fill=\"none\"/>");
                if (c.Checked) s.Append(CultureInfo.InvariantCulture, $"<path d=\"M{x + 4} {center - 1}l3 4 7-10-7 7z\"/>");
                labelX = x + 20; labelWidth = c.Width - 22; break;
            case Widget.Radio:
                s.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{x + 8}\" cy=\"{center}\" r=\"5\" stroke=\"#000\" stroke-width=\"1.5\" fill=\"none\"/>");
                if (c.Checked) s.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{x + 8}\" cy=\"{center}\" r=\"2.5\"/>");
                labelX = x + 20; labelWidth = c.Width - 22; break;
            case Widget.Choice:
                s.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x + 1}\" y=\"{y + 1}\" width=\"{c.Width - 4}\" height=\"{c.Height - 3}\" fill=\"#EEE\" stroke=\"#000\"/>");
                int arrow = x + c.Width - 14;
                Rule(s, arrow, y + 1, arrow, y + c.Height - 2);
                s.Append(CultureInfo.InvariantCulture, $"<path d=\"M{arrow + 2} {center - 3}h7l-3.5 6z\"/>");
                labelX = x + 3; labelWidth = c.Width - 20; break;
        }
        if (c.Text.Length > 0) c.Label.Append(s, labelX, y + (c.Height - c.Label.Height) / 2, labelWidth, c.Type == Widget.Button ? "center" : null);
    }

    private static void Rule(StringBuilder s, int x1, int y1, int x2, int y2) =>
        s.Append(CultureInfo.InvariantCulture, $"<line x1=\"{x1}\" y1=\"{y1}\" x2=\"{x2}\" y2=\"{y2}\" stroke=\"#000\"/>");
}

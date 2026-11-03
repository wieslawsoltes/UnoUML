using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

internal static class CreoleRenderer
{
    private sealed record Cell(string Text, bool Header, string Fill);
    private sealed record Row(int Line, Cell[] Cells, string Border);

    internal static string? TryRender(string source)
    {
        bool opened = false, closed = false, code = false;
        int line = -1, y = 10, width = 0, count = 0;
        int[] numbers = new int[16];
        var table = new List<Row>();
        var content = new StringBuilder();
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            line++;
            string text = raw.TrimEnd().ToString();
            if (!opened)
            {
                if (text.Trim().Length == 0) continue;
                if (text.Trim() != "@startcreole") return null;
                opened = true;
                continue;
            }
            if (closed) { if (text.Trim().Length != 0) return null; continue; }
            if (text.Trim() == "@endcreole")
            {
                if (code) return null;
                DrawTable(content, table, ref width, ref y);
                closed = true;
                continue;
            }
            if (++count > 1400 || text.Length > 65536 || y > 32600) return null;
            if (text.Trim() == "</code>" && code) { code = false; y += 4; continue; }
            if (code)
            {
                content.Append(CultureInfo.InvariantCulture, $"<g data-source-line=\"{line}\"><text x=\"12\" y=\"{y + 12}\" font-size=\"14\" font-family=\"monospace\" xml:space=\"preserve\">{SvgText.Escape(text)}</text></g>");
                width = Math.Max(width, Math.Max(SvgText.MeasurePlain(text), (int)Math.Ceiling(text.EnumerateRunes().Count() * 8.4)) + 24);
                y += 20;
                continue;
            }
            bool tableLine = text.StartsWith('|') && !text.StartsWith("|_", StringComparison.Ordinal) ||
                text.StartsWith("<#", StringComparison.Ordinal) && text.Contains(">|", StringComparison.Ordinal);
            if (tableLine)
            {
                if (!ReadRow(text, line, out var row)) return null;
                table.Add(row!);
                continue;
            }
            DrawTable(content, table, ref width, ref y);
            if (text.Trim() == "<code>") { code = true; y += 4; continue; }
            if (text.Length == 0) { y += 12; continue; }
            if (text[0] is '!' or '@' or '{' or ':' or ';' || text.StartsWith("|_", StringComparison.Ordinal)) return null;
            if (text.Length >= 4 && (text.All(c => c == '-') || text.All(c => c == '=')))
            {
                content.Append(CultureInfo.InvariantCulture, $"<path data-source-line=\"{line}\" d=\"M12 {y + 7}h{Math.Max(120, width - 24)}\" stroke=\"#888888\" stroke-width=\"{(text[0] == '=' ? 2 : 1)}\"/>");
                width = Math.Max(width, 144);
                y += 16;
                continue;
            }
            int level = 0, indent = 0;
            string marker = "";
            char prefix = text[0];
            if (prefix is '*' or '#')
            {
                while (level < text.Length && text[level] == prefix) level++;
                if (level < text.Length && char.IsWhiteSpace(text[level]))
                {
                    if (level > 15) return null;
                    text = text[level..].TrimStart();
                    indent = 19 * level;
                    if (prefix == '#') { numbers[level]++; marker = numbers[level].ToString(CultureInfo.InvariantCulture) + "."; }
                    else { numbers[level] = 0; marker = "•"; }
                    Array.Clear(numbers, level + 1, numbers.Length - level - 1);
                }
            }
            if (marker.Length == 0) Array.Clear(numbers);
            if (text.Length == 0 || !SvgText.IsSupported(text)) return null;
            int x = 12 + indent, baseline = y + SvgText.Ascent(text);
            content.Append(CultureInfo.InvariantCulture, $"<g data-source-line=\"{line}\">");
            if (marker.Length > 0) content.Append(CultureInfo.InvariantCulture, $"<text x=\"{x - 6}\" y=\"{baseline}\" text-anchor=\"end\">{marker}</text>");
            SvgText.Append(content, x, baseline, text, 14, "start");
            content.Append("</g>");
            width = Math.Max(width, x + SvgText.Measure(text) + 14);
            y += SvgText.Height(text) + 6;
        }
        if (!closed || count == 0 || width is <= 0 or > 32768 || y > 32758) return null;
        return TreeDiagram.Canvas(width, y + 10).Append(content).Append("</svg>").ToString();
    }

    private static bool ReadRow(string text, int line, out Row? row)
    {
        row = null;
        string fill = "none", border = "#181818";
        if (!ReadColor(ref text, ref fill, ref border) || !text.StartsWith('|') || !text.EndsWith('|')) return false;
        var cells = new List<Cell>();
        int start = 1;
        for (int at = 1; at < text.Length; at++)
        {
            if (text[at] == '~' && at + 1 < text.Length) { at++; continue; }
            if (text[at] != '|') continue;
            string cell = text[start..at].Trim(), background = fill, unusedBorder = border;
            bool header = cell.StartsWith('=');
            if (header) cell = cell[1..].TrimStart();
            if (!ReadColor(ref cell, ref background, ref unusedBorder)) return false;
            cell = cell.Replace("~|", "|", StringComparison.Ordinal);
            if (!SvgText.IsSupported(cell) || cells.Count >= 32) return false;
            cells.Add(new(cell, header, background));
            start = at + 1;
        }
        row = new(line, cells.ToArray(), border);
        return cells.Count > 0 && start == text.Length;
    }

    private static bool ReadColor(ref string text, ref string fill, ref string border)
    {
        if (!text.StartsWith("<#", StringComparison.Ordinal)) return true;
        int end = text.IndexOf('>');
        if (end < 0) return false;
        string[] colors = text[1..end].Split(',');
        if (colors.Length > 2 || ManagedSkin.Color(colors[0]) is not { } background) return false;
        fill = background;
        if (colors.Length == 2)
        {
            if (ManagedSkin.Color(colors[1]) is not { } outline) return false;
            border = outline;
        }
        text = text[(end + 1)..].TrimStart();
        return true;
    }

    private static void DrawTable(StringBuilder svg, List<Row> rows, ref int width, ref int y)
    {
        if (rows.Count == 0) return;
        int[] widths = new int[rows.Max(row => row.Cells.Length)];
        foreach (var row in rows)
            for (int column = 0; column < row.Cells.Length; column++)
                widths[column] = Math.Max(widths[column], SvgText.Measure(row.Cells[column].Text) + 12);
        foreach (var row in rows)
        {
            int height = Math.Max(14, row.Cells.Max(cell => SvgText.Height(cell.Text))) + 10;
            int x = 12;
            for (int column = 0; column < widths.Length; column++)
            {
                var cell = column < row.Cells.Length ? row.Cells[column] : new Cell("", false, "none");
                svg.Append(CultureInfo.InvariantCulture, $"<g class=\"creole-cell\" data-source-line=\"{row.Line}\" data-column=\"{column}\"><rect x=\"{x}\" y=\"{y}\" width=\"{widths[column]}\" height=\"{height}\" fill=\"{cell.Fill}\" stroke=\"{row.Border}\"/>");
                SvgText.Append(svg, x + 6, y + 5 + SvgText.Ascent(cell.Text), cell.Text, 14, "start", bold: cell.Header);
                svg.Append("</g>");
                x += widths[column];
            }
            width = Math.Max(width, x + 12);
            y += height;
        }
        rows.Clear();
        y += 6;
    }
}

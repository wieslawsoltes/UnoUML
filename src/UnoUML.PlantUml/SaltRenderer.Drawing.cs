using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

internal static partial class SaltRenderer
{
    private static void DrawForm(StringBuilder s, StringBuilder overlays, Form form, int x, int y, ref int width, ref int height)
    {
        int gridWidth = form.Width - form.Right, gridHeight = form.Height - form.Bottom;
        int rowY = y + form.Header;
        var ancestors = form.Kind == "tree" ? new (int X, int Y)?[129] : null;
        for (int row = 0; row < form.Rows.Count; row++)
        {
            int cellX = x, column = 0;
            foreach (var cell in form.Rows[row])
            {
                int available = 0;
                for (int span = 0; span < cell.Span; span++) available += form.Widths[column + span];
                int cellY = rowY + (form.Heights[row] - cell.Height) / 2;
                if (cell.Form is { } nested)
                {
                    s.Append("<g class=\"salt-group\">");
                    DrawForm(s, overlays, nested, cellX + 2, cellY + 2, ref width, ref height);
                    s.Append("</g>");
                }
                else if (cell.Type == Widget.Separator) DrawSeparator(s, cell.Separator, cellX + 3, cellY + cell.Height / 2, available - 6);
                else if (cell.Type != Widget.Space)
                {
                    if (ancestors != null && column == 0)
                    {
                        int nodeX = cellX + 12 * cell.Depth + 2, nodeY = cellY + cell.Height / 2;
                        if (cell.Depth > 0 && ancestors[cell.Depth - 1] is { } parent)
                        {
                            Rule(s, parent.X, parent.Y, parent.X, nodeY, " stroke-dasharray=\"1,2\"");
                            Rule(s, parent.X, nodeY, nodeX - 2, nodeY, " stroke-dasharray=\"1,2\"");
                        }
                        ancestors[cell.Depth] = (nodeX, nodeY);
                        Array.Clear(ancestors, cell.Depth + 1, ancestors.Length - cell.Depth - 1);
                    }
                    BeginCell(s, cell);
                    DrawCell(s, cell, cellX, cellY, available);
                    s.Append("</g>");
                    if (cell.Options is { Count: > 0 } options)
                        DrawPopup(overlays, options, cellX + 1, cellY + cell.Height - 2, cell.Width - 3, ref width, ref height);
                    if (form.Popups != null)
                        foreach (var popup in form.Popups.Where(p => p.Target == cell))
                            DrawPopup(overlays, popup.Items, cellX, cellY + cell.Height - 1, cell.Width, ref width, ref height);
                }
                column += cell.Span;
                cellX += available;
                if (form.Grid is '#' or '!' && column < form.Widths.Length)
                    Rule(s, cellX, rowY, cellX, rowY + form.Heights[row]);
            }
            rowY += form.Heights[row];
            if (form.Grid is '#' or '-' && row < form.Rows.Count - 1) Rule(s, x, rowY, x + gridWidth, rowY);
        }
        if (form.Grid is '#' or '+' or '-')
        {
            int top = y + form.Header / 2;
            if (form.Title is { } title)
            {
                Rule(s, x, top, x + 4, top);
                Rule(s, x + title.Width + 4, top, x + gridWidth, top);
                BeginCell(s, title, "salt-group-title");
                if (title.Length == 0)
                    s.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x + 6}\" y=\"{y}\" width=\"{title.Width}\" height=\"{title.Height}\" fill=\"transparent\" stroke=\"none\"/>");
                title.Label!.Append(s, x + 6, y, title.Width);
                s.Append("</g>");
            }
            else Rule(s, x, top, x + gridWidth, top);
            Rule(s, x, y + gridHeight, x + gridWidth, y + gridHeight);
        }
        if (form.Grid is '#' or '+' or '!')
        {
            Rule(s, x, y + form.Header / 2, x, y + gridHeight);
            Rule(s, x + gridWidth, y + form.Header / 2, x + gridWidth, y + gridHeight);
        }
        if (form.Kind is "tabs" or "menu") Rule(s, x, y + gridHeight, x + gridWidth, y + gridHeight);
        if (form.Right > 0) DrawScrollbar(s, x + gridWidth + 2, y, gridHeight, true);
        if (form.Bottom > 0) DrawScrollbar(s, x, y + gridHeight + 2, gridWidth, false);
    }

    private static void BeginCell(StringBuilder s, Cell cell, string? category = null)
    {
        s.Append(CultureInfo.InvariantCulture, $"<g class=\"{category ?? "salt-widget salt-" + cell.Type.ToString().ToLowerInvariant()}\" data-source-line=\"{cell.Line}\" data-source-column=\"{cell.Column}\" data-column-index=\"{cell.ColumnIndex}\" data-edit-start=\"{cell.Start}\" data-edit-length=\"{cell.Length}\" data-edit-kind=\"salt-text\" data-edit-role=\"label\"");
        StyleLabel.Scope(s, cell.Category);
        s.Append('>');
    }

    private static void DrawCell(StringBuilder s, Cell cell, int x, int y, int available)
    {
        int labelX = x + 1, labelWidth = Math.Max(1, available - 2);
        int center = y + cell.Height / 2;
        switch (cell.Type)
        {
            case Widget.Input:
                Rule(s, x + 1, y + cell.Height - 3, x + cell.Width - 4, y + cell.Height - 3);
                Rule(s, x + 1, y + cell.Height - 6, x + 1, y + cell.Height - 3);
                Rule(s, x + cell.Width - 4, y + cell.Height - 6, x + cell.Width - 4, y + cell.Height - 3);
                labelX = x + 3; labelWidth = cell.Width - 7; break;
            case Widget.Button:
                s.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x + 3}\" y=\"{y + 3}\" width=\"{cell.Width - 7}\" height=\"{cell.Height - 7}\" rx=\"5\" ry=\"5\" stroke=\"#000\" stroke-width=\"2.5\" fill=\"#EEE\"/>");
                labelX = x + 4; labelWidth = cell.Width - 9; break;
            case Widget.Check:
            case Widget.Radio:
                s.Append(CultureInfo.InvariantCulture, $"<g class=\"salt-state\" data-edit-start=\"{cell.TokenStart}\" data-edit-length=\"{cell.MarkerLength}\" data-edit-kind=\"salt-state\" data-edit-role=\"value\"><rect x=\"{x}\" y=\"{center - 8}\" width=\"17\" height=\"16\" fill=\"transparent\" stroke=\"none\"/>");
                if (cell.Type == Widget.Check)
                {
                    s.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x + 3}\" y=\"{center - 5}\" width=\"10\" height=\"10\" stroke=\"#000\" fill=\"none\"/>");
                    if (cell.Checked) s.Append(CultureInfo.InvariantCulture, $"<path d=\"M{x + 4} {center - 1}l3 4 7-10-7 7z\"/>");
                }
                else
                {
                    s.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{x + 8}\" cy=\"{center}\" r=\"5\" stroke=\"#000\" stroke-width=\"1.5\" fill=\"none\"/>");
                    if (cell.Checked) s.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{x + 8}\" cy=\"{center}\" r=\"2.5\"/>");
                }
                s.Append("</g>");
                labelX = x + 20; labelWidth = cell.Width - 22; break;
            case Widget.Choice:
                s.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x + 1}\" y=\"{y + 1}\" width=\"{cell.Width - 4}\" height=\"{cell.Height - 3}\" fill=\"#EEE\" stroke=\"#000\"/>");
                int arrow = x + cell.Width - 14;
                Rule(s, arrow, y + 1, arrow, y + cell.Height - 2);
                s.Append(CultureInfo.InvariantCulture, $"<path d=\"M{arrow + 2} {center - 3}h7l-3.5 6z\"/>");
                labelX = x + 3; labelWidth = cell.Width - 20; break;
            case Widget.Tree:
                labelX = x + 12 * cell.Depth + 4;
                labelWidth = cell.Width - (labelX - x) - 2;
                break;
            case Widget.Tab:
                s.Append(CultureInfo.InvariantCulture, $"<path d=\"M{x + 1} {y + cell.Height}V{y + 5}l4-4h{available - 10}l4 4v{cell.Height - 5}\" fill=\"#EEE\" stroke=\"#000\"/>");
                labelX = x + 8; labelWidth = available - 16;
                break;
            case Widget.Menu:
                s.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{y}\" width=\"{available}\" height=\"{cell.Height}\" fill=\"#EEE\"/>");
                labelX = x + 8; labelWidth = available - 16;
                break;
        }
        if (cell.Text.Length > 0) cell.Label!.Append(s, labelX, y + (cell.Height - cell.Label.Height) / 2, labelWidth, cell.Type == Widget.Button ? "center" : null);
    }

    private static void DrawPopup(StringBuilder s, List<Cell> items, int x, int y, int minimumWidth, ref int width, ref int height)
    {
        int popupWidth = Math.Max(minimumWidth, items.Max(item => item.Width) + 4);
        int popupHeight = items.Sum(item => item.Height) + 2;
        width = Math.Max(width, x + popupWidth + 2);
        height = Math.Max(height, y + popupHeight + 2);
        s.Append(CultureInfo.InvariantCulture, $"<g class=\"salt-popup\"><rect x=\"{x}\" y=\"{y}\" width=\"{popupWidth}\" height=\"{popupHeight}\" fill=\"#EEE\" stroke=\"#000\"/>");
        int rowY = y + 1;
        foreach (var item in items)
        {
            if (item.Type == Widget.Menu && item.Text == "-") Rule(s, x + 3, rowY + item.Height / 2, x + popupWidth - 3, rowY + item.Height / 2);
            else
            {
                BeginCell(s, item, "salt-widget salt-option");
                item.Label!.Append(s, x + 3, rowY + (item.Height - item.Label.Height) / 2, popupWidth - 6);
                s.Append("</g>");
            }
            rowY += item.Height;
        }
        s.Append("</g>");
    }

    private static void DrawSeparator(StringBuilder s, char kind, int x, int y, int width)
    {
        string extra = kind switch { '.' => " stroke-dasharray=\"1,2\"", '~' => " stroke-dasharray=\"4,2\"", _ => "" };
        Rule(s, x, y, x + Math.Max(1, width), y, extra);
        if (kind == '=') Rule(s, x, y + 2, x + Math.Max(1, width), y + 2);
    }

    private static void DrawScrollbar(StringBuilder s, int x, int y, int length, bool vertical)
    {
        int width = vertical ? 10 : length, height = vertical ? length : 10;
        s.Append(CultureInfo.InvariantCulture, $"<g class=\"salt-scrollbar\"><rect x=\"{x}\" y=\"{y}\" width=\"{width}\" height=\"{height}\" fill=\"#EEE\" stroke=\"#000\"/>");
        if (length >= 24)
        {
            if (vertical)
                s.Append(CultureInfo.InvariantCulture, $"<path d=\"M{x + 2} {y + 7}l3-4 3 4zM{x + 2} {y + height - 7}l3 4 3-4z\"/><rect x=\"{x + 2}\" y=\"{y + 11}\" width=\"6\" height=\"{Math.Max(2, (height - 22) / 2)}\" fill=\"#BBB\" stroke=\"#000\"/>");
            else
                s.Append(CultureInfo.InvariantCulture, $"<path d=\"M{x + 7} {y + 2}l-4 3 4 3zM{x + width - 7} {y + 2}l4 3-4 3z\"/><rect x=\"{x + 11}\" y=\"{y + 2}\" width=\"{Math.Max(2, (width - 22) / 2)}\" height=\"6\" fill=\"#BBB\" stroke=\"#000\"/>");
        }
        s.Append("</g>");
    }
}

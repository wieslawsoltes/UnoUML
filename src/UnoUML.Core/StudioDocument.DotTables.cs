using System.Globalization;
using System.Net;
using System.Text;

namespace UnoUML.Core;

public sealed partial class StudioDocument
{
    public bool SetDotTableCellText(DotLabelEdit label, int table, int row, int column, string text)
    {
        if (!ValidDotLabelText(text) || DotLabelCell(label, table, row, column) is not { } cell) return false;
        return ApplyDotLabelChanges(label, [new(cell.Element.OpenEnd, cell.Element.CloseStart, DotHtmlText(text))]);
    }

    public bool SetDotTableCellPort(DotLabelEdit label, int table, int row, int column, string port)
    {
        if (!DotLabelSyntax.ValidPort(port) || DotLabelCell(label, table, row, column) is not { } cell || cell.Element.Value("PORT") == port) return false;
        var changes = new List<DotChange>();
        DotHtmlAttribute(changes, cell.Element, "PORT", port.Length == 0 ? null : port);
        return ApplyDotLabelChanges(label, changes, cell.Element.Value("PORT"), port);
    }

    public bool InsertDotTableRow(DotLabelEdit label, int table, int index)
    {
        if (DotLabelTable(label, table) is not { Rectangular: true } grid || index < 0 || index > grid.Rows.Count || grid.Rows.Count >= 128) return false;
        var changes = new List<DotChange>();
        var crossing = grid.Cells.Where(cell => cell.Row < index && cell.Row + cell.RowSpan > index).ToArray();
        foreach (var cell in crossing) DotHtmlSpan(changes, cell.Element, "ROWSPAN", cell.RowSpan + 1);
        int free = Enumerable.Range(0, grid.Columns).Count(column => !crossing.Any(cell => column >= cell.Column && column < cell.Column + cell.ColumnSpan));
        if (free == 0) return false; // DOT's HTML grammar requires at least one TD in every TR.
        int at = index == grid.Rows.Count ? grid.Rows[^1].End : grid.Rows[index].Start;
        changes.Add(new(at, at, "<TR>" + string.Concat(Enumerable.Repeat("<TD>Cell</TD>", free)) + "</TR>"));
        return ApplyDotLabelChanges(label, changes);
    }

    public bool RemoveDotTableRow(DotLabelEdit label, int table, int index)
    {
        if (DotLabelTable(label, table) is not { Rectangular: true } grid || grid.Rows.Count <= 1 || index < 0 || index >= grid.Rows.Count) return false;
        var changes = new List<DotChange>();
        foreach (var cell in grid.Cells.Where(cell => cell.Row < index && cell.Row + cell.RowSpan > index))
            DotHtmlSpan(changes, cell.Element, "ROWSPAN", cell.RowSpan - 1);
        // A spanning cell starts at the next surviving row instead of losing
        // its contents, port, attributes, or nested tables when its first row goes.
        foreach (var cell in grid.Cells.Where(cell => cell.Row == index && cell.RowSpan > 1))
        {
            var local = new List<DotChange>(); DotHtmlSpan(local, cell.Element, "ROWSPAN", cell.RowSpan - 1);
            string? moved = DotLabelRange(label.Syntax.Value, cell.Element.Start, cell.Element.End, local);
            if (moved == null) return false;
            var before = grid.Cells.FirstOrDefault(next => next.Row == index + 1 && next.Column > cell.Column);
            int at = before?.Element.Start ?? grid.Rows[index + 1].CloseStart;
            changes.Add(new(at, at, moved));
        }
        changes.Add(new(grid.Rows[index].Start, grid.Rows[index].End, ""));
        return ApplyDotLabelChanges(label, changes);
    }

    public bool InsertDotTableColumn(DotLabelEdit label, int table, int index)
    {
        if (DotLabelTable(label, table) is not { Rectangular: true } grid || index < 0 || index > grid.Columns || grid.Columns >= 128) return false;
        var changes = new List<DotChange>();
        var crossing = grid.Cells.Where(cell => cell.Column < index && cell.Column + cell.ColumnSpan > index).ToArray();
        foreach (var cell in crossing) DotHtmlSpan(changes, cell.Element, "COLSPAN", cell.ColumnSpan + 1);
        for (int row = 0; row < grid.Rows.Count; row++)
        {
            if (crossing.Any(cell => row >= cell.Row && row < cell.Row + cell.RowSpan)) continue;
            var before = grid.Cells.FirstOrDefault(cell => cell.Row == row && cell.Column >= index);
            int at = before?.Element.Start ?? grid.Rows[row].CloseStart;
            changes.Add(new(at, at, "<TD>Cell</TD>"));
        }
        return ApplyDotLabelChanges(label, changes);
    }

    public bool RemoveDotTableColumn(DotLabelEdit label, int table, int index)
    {
        if (DotLabelTable(label, table) is not { Rectangular: true } grid || grid.Columns <= 1 || index < 0 || index >= grid.Columns) return false;
        var changes = new List<DotChange>();
        foreach (var cell in grid.Cells.Where(cell => cell.Column <= index && cell.Column + cell.ColumnSpan > index))
            if (cell.ColumnSpan > 1) DotHtmlSpan(changes, cell.Element, "COLSPAN", cell.ColumnSpan - 1);
            else changes.Add(new(cell.Element.Start, cell.Element.End, ""));
        return ApplyDotLabelChanges(label, changes);
    }

    /// <summary>Inserts a literal cell before a row's cell ordinal. Row/column operations preserve rectangular grids; this operation also permits ragged tables.</summary>
    public bool InsertDotTableCell(DotLabelEdit label, int table, int row, int index, string text = "Cell", string port = "")
    {
        if (!ValidDotLabelText(text) || !DotLabelSyntax.ValidPort(port) || DotLabelTable(label, table) is not { } grid ||
            row < 0 || row >= grid.Rows.Count || index < 0 || index > grid.Rows[row].Children.Count) return false;
        var cells = grid.Rows[row].Children;
        int at = index < cells.Count ? cells[index].Start : grid.Rows[row].CloseStart;
        return ApplyDotLabelChanges(label, [new(at, at, "<TD" + (port.Length == 0 ? "" : " PORT=\"" + WebUtility.HtmlEncode(port) + "\"") + ">" + DotHtmlText(text) + "</TD>")]);
    }

    public bool RemoveDotTableCell(DotLabelEdit label, int table, int row, int column)
    {
        if (DotLabelCell(label, table, row, column) is not { } cell) return false;
        return ApplyDotLabelChanges(label, [new(cell.Element.Start, cell.Element.End, "")]);
    }

    /// <summary>Merges whole cells in a rectangle. The upper-left cell keeps its attributes and port; referenced ports on other cells prevent removal.</summary>
    public bool MergeDotTableCells(DotLabelEdit label, int table, int row, int column, int rows, int columns)
    {
        if (DotLabelTable(label, table) is not { Rectangular: true } grid || row < 0 || column < 0 || rows < 1 || columns < 1 ||
            rows > grid.Rows.Count - row || columns > grid.Columns - column) return false;
        var cells = grid.Cells.Where(cell => cell.Row < row + rows && cell.Row + cell.RowSpan > row &&
            cell.Column < column + columns && cell.Column + cell.ColumnSpan > column).OrderBy(cell => cell.Row).ThenBy(cell => cell.Column).ToArray();
        if (cells.Length < 2 || cells[0].Row != row || cells[0].Column != column || cells.Any(cell => cell.Row < row || cell.Column < column ||
            cell.Row + cell.RowSpan > row + rows || cell.Column + cell.ColumnSpan > column + columns)) return false;
        // Tables and images have different HTML content productions than text.
        // Concatenating either with text or another cell would create an invalid TD.
        static bool TextContent(DotLabelSyntax.Element element) => element.Children.All(child =>
            child.Name is not ("TABLE" or "IMG") && TextContent(child));
        if (cells.Any(cell => !TextContent(cell.Element))) return false;
        var first = cells[0]; var changes = new List<DotChange>();
        DotHtmlSpan(changes, first.Element, "ROWSPAN", rows);
        DotHtmlSpan(changes, first.Element, "COLSPAN", columns);
        string content = string.Join("<BR/>", cells.Select(cell => label.Syntax.Value[cell.Element.OpenEnd..cell.Element.CloseStart]));
        changes.Add(new(first.Element.OpenEnd, first.Element.CloseStart, content));
        foreach (var cell in cells.Skip(1)) changes.Add(new(cell.Element.Start, cell.Element.End, ""));
        return ApplyDotLabelChanges(label, changes);
    }

    /// <summary>Splits a spanning cell into unit cells. Its original contents and port remain in the upper-left cell.</summary>
    public bool SplitDotTableCell(DotLabelEdit label, int table, int row, int column)
    {
        if (DotLabelTable(label, table) is not { Rectangular: true } grid || DotLabelCell(label, table, row, column) is not { } cell ||
            cell.RowSpan == 1 && cell.ColumnSpan == 1) return false;
        var changes = new List<DotChange>();
        DotHtmlSpan(changes, cell.Element, "ROWSPAN", 1); DotHtmlSpan(changes, cell.Element, "COLSPAN", 1);
        if (cell.ColumnSpan > 1) changes.Add(new(cell.Element.End, cell.Element.End, string.Concat(Enumerable.Repeat("<TD>Cell</TD>", cell.ColumnSpan - 1))));
        for (int r = row + 1; r < row + cell.RowSpan; r++)
        {
            var before = grid.Cells.FirstOrDefault(next => next.Row == r && next.Column > column);
            int at = before?.Element.Start ?? grid.Rows[r].CloseStart;
            changes.Add(new(at, at, string.Concat(Enumerable.Repeat("<TD>Cell</TD>", cell.ColumnSpan))));
        }
        return ApplyDotLabelChanges(label, changes);
    }

    private DotLabelSyntax.Table? DotLabelTable(DotLabelEdit label, int table) => CurrentDotLabel(label, "html") && table >= 0 && table < label.Syntax.HtmlTables.Count ? label.Syntax.HtmlTables[table] : null;
    private DotLabelSyntax.Cell? DotLabelCell(DotLabelEdit label, int table, int row, int column) => DotLabelTable(label, table)?.Cells.FirstOrDefault(cell => cell.Row == row && cell.Column == column);
    private static string DotHtmlText(string text) => WebUtility.HtmlEncode(text.ReplaceLineEndings("\n")).Replace("\n", "<BR/>", StringComparison.Ordinal);
    private static void DotHtmlSpan(List<DotChange> changes, DotLabelSyntax.Element element, string name, int value) =>
        DotHtmlAttribute(changes, element, name, value == 1 ? null : value.ToString(CultureInfo.InvariantCulture));

    private static void DotHtmlAttribute(List<DotChange> changes, DotLabelSyntax.Element element, string name, string? value)
    {
        var old = element.Find(name);
        if (old != null)
        {
            if (value == null) changes.Add(new(old.Start, old.End, ""));
            else if (old.Value != value) changes.Add(new(old.ValueStart, old.ValueEnd, WebUtility.HtmlEncode(value)));
        }
        else if (value != null) changes.Add(new(element.OpenEnd - 1, element.OpenEnd - 1, " " + name + "=\"" + WebUtility.HtmlEncode(value) + "\""));
    }

    private bool ApplyDotLabelChanges(DotLabelEdit label, IReadOnlyList<DotChange> changes, string oldPort = "", string newPort = "") =>
        DotLabelRange(label.Syntax.Value, 0, label.Syntax.Value.Length, changes) is { } value && EditDotLabel(label, value, oldPort, newPort);

    private static string? DotLabelRange(string source, int start, int end, IReadOnlyList<DotChange> changes)
    {
        var result = new StringBuilder(); int at = start;
        foreach (var change in changes.OrderBy(change => change.Start).ThenBy(change => change.End))
        {
            if (change.Start < at || change.End < change.Start || change.End > end) return null;
            result.Append(source, at, change.Start - at).Append(change.Text); at = change.End;
        }
        return result.Append(source, at, end - at).ToString();
    }
}

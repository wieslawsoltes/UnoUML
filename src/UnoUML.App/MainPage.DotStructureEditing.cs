using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UnoUML.App.Rendering;
using UnoUML.Core;
using DotStructuredLabelEdit = UnoUML.Core.DotLabelEdit;

namespace UnoUML.App;

public sealed partial class MainPage
{
    private const string DotStructureFailure = "The source changed, or this change would remove a connected port or create an invalid field/table structure.";

    private void AddDotFieldStructureActions(SourceFieldEdit field)
    {
        if (field.Kind is "dot-record-text" or "dot-html-text" && DotSelection is { } item)
            AddDotStructureActions(item, field);
    }

    private void AddDotStructureActions(DotObjectEdit item, SourceFieldEdit? field = null)
    {
        if (item.Location.Kind is not ("node" or "edge")) return;
        if (doc.OpenDotLabel(item) is not { } label)
        {
            if (item.Location.Kind == "node")
                Properties.Children.Add(Item("Create record fields", "layers", () => Edit(() => doc.CreateDotRecordLabel(item, item.Text), DotStructureFailure)));
            Properties.Children.Add(Item("Create table cells", "layers", () => Edit(() => doc.CreateDotTableLabel(item), DotStructureFailure)));
            return;
        }
        if (item.Location.Kind == "node")
            Properties.Children.Add(Caption("Ports stay connected when renamed. Removing a connected port is blocked."));
        int fieldStart = field == null ? -1 : field.Offset - item.Offset + item.Location.Start;
        if (label.Kind == "record") AddDotRecordActions(label, fieldStart);
        else AddDotTableActions(label, fieldStart);
    }

    private void AddDotRecordActions(DotStructuredLabelEdit label, int fieldStart)
    {
        var fields = label.RecordFields;
        var picker = DotStructurePicker("Record field", fields.Select(field => field.Path + ": " +
            (field.IsGroup ? "Field group" : (field.Port.Length == 0 ? "" : "<" + field.Port + "> ") + field.Text)).ToArray());
        int selected = fields.Select((field, index) => (field, index)).Where(pair => !pair.field.IsGroup &&
            pair.field.Start <= fieldStart && pair.field.Start + pair.field.Length > fieldStart).Select(pair => pair.index).FirstOrDefault();
        picker.SelectedIndex = selected;
        Properties.Children.Add(picker);
        var text = DotStructureInput("Record field text", fields[0].Text, true);
        var port = DotStructureInput("Record field port", fields[0].Port);
        Properties.Children.Add(text); Properties.Children.Add(port);
        DotRecordField Selected() => fields[Math.Clamp(picker.SelectedIndex, 0, fields.Count - 1)];
        void Select()
        {
            var field = Selected(); text.Text = field.Text; port.Text = field.Port;
            text.IsEnabled = port.IsEnabled = !field.IsGroup;
        }
        picker.SelectionChanged += (_, _) => Select(); Select();
        Properties.Children.Add(Item("Apply field text", "edit", () => Edit(() => doc.SetDotRecordFieldText(label, Selected().Path, text.Text), DotStructureFailure)));
        Properties.Children.Add(Item("Apply field port", "edit", () => Edit(() => doc.SetDotRecordFieldPort(label, Selected().Path, port.Text), DotStructureFailure)));
        Properties.Children.Add(Item("Insert field before", "plus", () => Edit(() => doc.InsertDotRecordField(label, Selected().ParentPath, Selected().Index), DotStructureFailure)));
        Properties.Children.Add(Item("Insert field after", "plus", () => Edit(() => doc.InsertDotRecordField(label, Selected().ParentPath, Selected().Index + 1), DotStructureFailure)));
        Properties.Children.Add(Item("Move field earlier", "up", () => Edit(() => doc.MoveDotRecordField(label, Selected().Path, Selected().Index - 1), DotStructureFailure)));
        Properties.Children.Add(Item("Move field later", "down", () => Edit(() => doc.MoveDotRecordField(label, Selected().Path, Selected().Index + 1), DotStructureFailure)));
        Properties.Children.Add(Item("Group with next field", "layers", () => Edit(() => doc.GroupDotRecordFields(label, Selected().ParentPath, Selected().Index, 2), DotStructureFailure)));
        Properties.Children.Add(Item("Ungroup record fields", "layers", () => Edit(() => doc.UngroupDotRecordField(label, Selected().Path), DotStructureFailure)));
        Properties.Children.Add(Item("Remove record field", "delete", () => Edit(() => doc.RemoveDotRecordField(label, Selected().Path), DotStructureFailure)));
    }

    private void AddDotTableActions(DotStructuredLabelEdit label, int fieldStart)
    {
        var tables = label.Tables;
        var tablePicker = DotStructurePicker("Label table", tables.Select(table => $"Table {table.Index + 1}: {table.Rows} rows × {table.Columns} columns").ToArray());
        var selected = tables.SelectMany(table => table.Cells.Select((cell, index) => (table, cell, index)))
            .Where(pair => pair.cell.Start <= fieldStart && pair.cell.Start + pair.cell.Length > fieldStart).OrderBy(pair => pair.cell.Length).FirstOrDefault();
        if (selected.table != null) tablePicker.SelectedIndex = selected.table.Index;
        var cellPicker = DotStructurePicker("Table cell", []);
        var text = DotStructureInput("Table cell text", "", true);
        var port = DotStructureInput("Table cell port", "");
        Properties.Children.Add(tablePicker); Properties.Children.Add(cellPicker);
        Properties.Children.Add(text); Properties.Children.Add(port);
        DotTable Table() => tables[Math.Clamp(tablePicker.SelectedIndex, 0, tables.Count - 1)];
        DotTableCell Cell() => Table().Cells[Math.Clamp(cellPicker.SelectedIndex, 0, Table().Cells.Count - 1)];
        void SelectCell()
        {
            if (cellPicker.SelectedIndex < 0) return;
            var cell = Cell(); text.Text = cell.Text; port.Text = cell.Port;
        }
        void SelectTable()
        {
            cellPicker.ItemsSource = Table().Cells.Select(cell => $"Row {cell.Row + 1}, column {cell.Column + 1}: {cell.Text}").ToArray();
            cellPicker.SelectedIndex = 0; SelectCell();
        }
        tablePicker.SelectionChanged += (_, _) => SelectTable();
        cellPicker.SelectionChanged += (_, _) => SelectCell(); SelectTable();
        if (selected.table != null) { cellPicker.SelectedIndex = selected.index; SelectCell(); }
        void Apply(Func<DotTable, DotTableCell, bool> change) => Edit(() => change(Table(), Cell()), DotStructureFailure);
        Properties.Children.Add(Item("Apply cell text", "edit", () => Apply((table, cell) => doc.SetDotTableCellText(label, table.Index, cell.Row, cell.Column, text.Text))));
        Properties.Children.Add(Item("Apply cell port", "edit", () => Apply((table, cell) => doc.SetDotTableCellPort(label, table.Index, cell.Row, cell.Column, port.Text))));
        Properties.Children.Add(Item("Insert row above", "plus", () => Apply((table, cell) => doc.InsertDotTableRow(label, table.Index, cell.Row))));
        Properties.Children.Add(Item("Insert row below", "plus", () => Apply((table, cell) => doc.InsertDotTableRow(label, table.Index, cell.Row + cell.RowSpan))));
        Properties.Children.Add(Item("Insert column before", "plus", () => Apply((table, cell) => doc.InsertDotTableColumn(label, table.Index, cell.Column))));
        Properties.Children.Add(Item("Insert column after", "plus", () => Apply((table, cell) => doc.InsertDotTableColumn(label, table.Index, cell.Column + cell.ColumnSpan))));
        Properties.Children.Add(Item("Insert cell after", "plus", () => Apply((table, cell) => doc.InsertDotTableCell(label, table.Index, cell.Row,
            table.Cells.Count(other => other.Row == cell.Row && other.Column <= cell.Column)))));
        Properties.Children.Add(Item("Merge with next cell", "layers", () => Apply((table, cell) => table.Cells.FirstOrDefault(other =>
            other.Row == cell.Row && other.Column == cell.Column + cell.ColumnSpan) is { } next &&
            doc.MergeDotTableCells(label, table.Index, cell.Row, cell.Column, cell.RowSpan, cell.ColumnSpan + next.ColumnSpan))));
        Properties.Children.Add(Item("Split spanning cell", "layers", () => Apply((table, cell) => doc.SplitDotTableCell(label, table.Index, cell.Row, cell.Column))));
        Properties.Children.Add(Item("Remove table row", "delete", () => Apply((table, cell) => doc.RemoveDotTableRow(label, table.Index, cell.Row))));
        Properties.Children.Add(Item("Remove table column", "delete", () => Apply((table, cell) => doc.RemoveDotTableColumn(label, table.Index, cell.Column))));
        Properties.Children.Add(Item("Remove table cell", "delete", () => Apply((table, cell) => doc.RemoveDotTableCell(label, table.Index, cell.Row, cell.Column))));
    }

    private static ComboBox DotStructurePicker(string name, string[] values)
    {
        var picker = new ComboBox { Header = name, ItemsSource = values, SelectedIndex = values.Length > 0 ? 0 : -1, HorizontalAlignment = HorizontalAlignment.Stretch };
        BrowserAccessibility.AttachComboBox(picker);
        BrowserAccessibility.SetName(picker, name); return picker;
    }
    private static TextBox DotStructureInput(string name, string value, bool multiline = false)
    {
        var field = new TextBox { Header = name, Text = value, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap, MaxHeight = 160 };
        BrowserAccessibility.SetName(field, name); return field;
    }
}

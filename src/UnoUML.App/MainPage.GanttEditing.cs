using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using UnoUML.Core;
using UnoUML.App.Rendering;

namespace UnoUML.App;

public sealed partial class MainPage
{
    private const string GanttFailure = "This schedule would create a cycle, conflicting dates, an invalid task, or exceed the supported calendar range. The source may also have changed.";
    private (StudioDocument Document, int Revision, long History, string Id)? pendingGanttSelection;

    private void EditGanttSelection(string id, Func<bool> operation)
    {
        Edit(() =>
        {
            if (!operation()) return false;
            pendingGanttSelection = (doc, revision + 1, doc.HistoryState, id);
            return true;
        }, GanttFailure);
    }

    private void GanttSelectionRendered()
    {
        if (pendingGanttSelection is not { } pending) return;
        pendingGanttSelection = null;
        if (!ReferenceEquals(doc, pending.Document) || revision != pending.Revision || doc.HistoryState != pending.History ||
            viewport.ActiveRenderer != "C#" || doc.OpenGanttDiagram()?.Tasks.FirstOrDefault(t => t.Id == pending.Id) is not { } task) return;
        selected = null;
        SelectSourceField(new SourceFieldHit(task.NameStart, task.NameLength, "gantt-label", "label"));
        ShowInspector();
    }

    private void AddGanttDiagramActions()
    {
        if (doc.OpenGanttDiagram() is not { } chart) return;
        Properties.Children.Add(Caption($"{chart.Tasks.Count} tasks · calendar-day schedule"));
        Properties.Children.Add(Item("Schedule settings…", "edit", () => _ = EditGanttSettingsAsync(chart)));
        Properties.Children.Add(Item("Insert task or milestone…", "add", () => _ = EditGanttTaskAsync(chart, null)));
        if (chart.Tasks.Count == 0) return;
        var task = StructuredPicker("Schedule task", chart.Tasks.Select(t => t.Label), 0);
        Properties.Children.Add(task);
        Properties.Children.Add(Item("Edit task schedule…", "edit", () => _ = EditGanttTaskAsync(chart, chart.Tasks[task.SelectedIndex])));
        Properties.Children.Add(Item("Reorder schedule task…", "up", () => _ = ReorderGanttTaskAsync(chart.Tasks[task.SelectedIndex])));
        Properties.Children.Add(Item("Remove schedule task…", "delete", () => _ = RemoveGanttTaskAsync(chart.Tasks[task.SelectedIndex])));
    }

    private void AddGanttFieldActions(SourceFieldEdit field)
    {
        if (doc.OpenGanttTask(field) is { } task) AddGanttTaskActions(task);
    }
    private void AddGanttObjectActions(DiagramObject item)
    {
        if (doc.OpenGanttTask(item) is { } task) AddGanttTaskActions(task);
        else if (doc.Page.Family == "Gantt") AddGanttDiagramActions();
    }
    private void AddGanttTaskActions(GanttTaskEdit task)
    {
        Properties.Children.Add(Caption(task.IsMilestone ? $"Milestone · day {task.StartDay + 1}" : $"Days {task.StartDay + 1}–{task.EndDay + 1} · {task.Duration} days"));
        Properties.Children.Add(Caption("Drag the timeline bar to move its start. Drag either end to resize. Arrow keys move by one day; Shift-arrow moves by seven days."));
        Properties.Children.Add(Item("Edit task schedule…", "edit", () => _ = EditGanttTaskAsync(task.Diagram, task)));
        Properties.Children.Add(Item("Rename schedule task…", "edit", () => _ = RenameGanttTaskAsync(task)));
        Properties.Children.Add(Item("Reorder schedule task…", "up", () => _ = ReorderGanttTaskAsync(task)));
        Properties.Children.Add(Item("Insert task or milestone…", "add", () => _ = EditGanttTaskAsync(task.Diagram, null)));
        Properties.Children.Add(Item("Remove schedule task…", "delete", () => _ = RemoveGanttTaskAsync(task)));
        Properties.Children.Add(Item("Schedule settings…", "edit", () => _ = EditGanttSettingsAsync(task.Diagram)));
    }

    private async Task EditGanttTaskAsync(GanttDiagramEdit chart, GanttTaskEdit? task)
    {
        var document = doc;
        var fields = new StackPanel { Spacing = 10 };
        var label = StructuredTextInput("Task name", task?.Label ?? "New task");
        var alias = StructuredTextInput("Task identifier (optional)", "");
        if (task == null) { fields.Children.Add(label); fields.Children.Add(alias); }
        var milestone = new CheckBox { Content = "Milestone", IsChecked = task?.IsMilestone ?? false }; AutomationProperties.SetName(milestone, "Milestone");
        var start = StructuredTextInput("Start day (0 = project start)", (task?.StartDay ?? 0).ToString(CultureInfo.InvariantCulture));
        var duration = StructuredTextInput("Duration in days", (task?.Duration ?? 1).ToString(CultureInfo.InvariantCulture));
        var predecessors = chart.Tasks.Where(t => t.Id != task?.Id).ToArray();
        int predecessorIndex = task?.PredecessorId is { } id ? Array.FindIndex(predecessors, t => t.Id == id) + 1 : 0;
        var predecessor = StructuredPicker("Start constraint", new[] { "Explicit start day" }.Concat(predecessors.Select(t => "After " + t.Label)), predecessorIndex);
        var endpoint = StructuredPicker("Predecessor endpoint", ["End", "Start"], task?.AfterPredecessorEnd == false ? 1 : 0);
        var completion = StructuredTextInput("Completion percent (optional)", task?.Completion?.ToString(CultureInfo.InvariantCulture) ?? "");
        var color = StructuredTextInput("Task color (#RRGGBB)", task?.Color ?? "#a0b9e7");
        foreach (var control in new FrameworkElement[] { milestone, predecessor, endpoint, start, duration, completion, color }) fields.Children.Add(control);
        void EnableInputs()
        {
            bool isMilestone = milestone.IsChecked == true;
            duration.IsEnabled = completion.IsEnabled = !isMilestone;
            start.IsEnabled = predecessor.SelectedIndex == 0;
            endpoint.IsEnabled = predecessor.SelectedIndex > 0;
        }
        milestone.Checked += (_, _) => EnableInputs(); milestone.Unchecked += (_, _) => EnableInputs();
        predecessor.SelectionChanged += (_, _) => EnableInputs(); EnableInputs();
        fields.Children.Add(Caption("Dependencies schedule automatically. An explicit start or a canvas drag replaces the previous start dependency. All dates use inclusive calendar days."));
        if (await AskAsync(task == null ? "Insert schedule task" : "Edit task schedule", fields, task == null ? "Insert" : "Apply") != ContentDialogResult.Primary || !StructuredStillCurrent(document)) return;
        bool isMilestone = milestone.IsChecked == true;
        if (!StructuredInteger(start, out int day) || !StructuredInteger(duration, out int days)) { Status("Use whole numbers for start and duration.", true); return; }
        int? percent = null;
        if (!isMilestone && completion.Text.Trim().Length > 0)
        {
            if (!StructuredInteger(completion, out int value)) { Status("Completion must be a whole number from 0 to 100.", true); return; }
            percent = value;
        }
        var schedule = new GanttTaskSchedule(day, isMilestone ? 1 : days, isMilestone,
            predecessor.SelectedIndex > 0 ? predecessors[predecessor.SelectedIndex - 1].Id : null,
            endpoint.SelectedIndex == 0, percent, color.Text.Trim());
        EditGanttSelection(task?.Id ?? (alias.Text.Trim().Length > 0 ? alias.Text.Trim() : label.Text.Trim()),
            () => task == null ? doc.InsertGanttTask(chart, chart.Tasks.Count, label.Text.Trim(), alias.Text.Trim(), schedule) : doc.SetGanttTaskSchedule(task, schedule));
    }

    private async Task RenameGanttTaskAsync(GanttTaskEdit task)
    {
        var document = doc; var name = StructuredTextInput("Task name", task.Label);
        if (await AskAsync("Rename schedule task", name, "Rename") == ContentDialogResult.Primary && StructuredStillCurrent(document))
            EditGanttSelection(task.HasAlias ? task.Id : name.Text, () => doc.RenameGanttTask(task, name.Text));
    }
    private async Task ReorderGanttTaskAsync(GanttTaskEdit task)
    {
        var document = doc; var position = StructuredPicker("Final position", task.Diagram.Tasks.Select((t, i) => $"{i + 1}: {t.Label}"), task.Index);
        if (await AskAsync("Reorder schedule task", position, "Move") == ContentDialogResult.Primary && StructuredStillCurrent(document))
            EditGanttSelection(task.Id, () => doc.ReorderGanttTask(task, position.SelectedIndex));
    }
    private async Task RemoveGanttTaskAsync(GanttTaskEdit task)
    {
        var document = doc; int count = task.Diagram.Tasks.Count(t => t.PredecessorId == task.Id);
        var fields = new StackPanel { Spacing = 10 };
        fields.Children.Add(Caption($"Remove “{task.Label}” from this schedule?"));
        var detach = new CheckBox { Content = "Keep dependent tasks at their current dates", IsChecked = true }; AutomationProperties.SetName(detach, "Keep dependent tasks at their current dates");
        if (count > 0) { fields.Children.Add(Caption($"{count} task(s) depend on this task.")); fields.Children.Add(detach); }
        if (await AskAsync("Remove schedule task", fields, "Remove") == ContentDialogResult.Primary && StructuredStillCurrent(document))
            Edit(() => doc.RemoveGanttTask(task, count == 0 || detach.IsChecked == true), GanttFailure);
    }
    private async Task EditGanttSettingsAsync(GanttDiagramEdit chart)
    {
        var document = doc; var fields = new StackPanel { Spacing = 10 };
        var title = StructuredTextInput("Schedule title", chart.Title);
        var start = StructuredTextInput("Project start (YYYY-MM-DD, optional)", chart.ProjectStart?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "");
        var footer = new CheckBox { Content = "Show calendar footer", IsChecked = chart.ShowFooter }; AutomationProperties.SetName(footer, "Show calendar footer");
        fields.Children.Add(title); fields.Children.Add(start); fields.Children.Add(footer);
        fields.Children.Add(Caption("Day offsets follow project start. Existing absolute-date constraints retain their dates and must still form a valid schedule."));
        if (await AskAsync("Schedule settings", fields, "Apply") != ContentDialogResult.Primary || !StructuredStillCurrent(document)) return;
        DateOnly? date = null;
        if (start.Text.Trim().Length > 0)
        {
            if (!DateOnly.TryParseExact(start.Text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) { Status("Enter a valid date in YYYY-MM-DD format.", true); return; }
            date = parsed;
        }
        Edit(() => doc.SetGanttSettings(chart, date, title.Text.Trim(), footer.IsChecked == true), GanttFailure);
    }

    private bool TryHandleGanttCanvasCommand(string action, string text)
    {
        if (doc.Page.Family != "Gantt") return false;
        if (action == "gantt-geometry")
        {
            try
            {
                if (text.Length > 4096) return true;
                using var json = JsonDocument.Parse(text);
                var value = json.RootElement;
                if (value.GetProperty("revision").GetInt32() != revision || doc.OpenGanttDiagram() is not { } chart) return true;
                string? id = value.GetProperty("id").GetString();
                var task = chart.Tasks.FirstOrDefault(t => t.Id == id);
                string? mode = value.GetProperty("mode").GetString();
                if (task != null && mode is "move" or "start" or "end")
                    EditGanttSelection(task.Id, () => mode == "end" ? doc.ResizeGanttTask(task, value.GetProperty("duration").GetInt32()) :
                        doc.MoveGanttTask(task, value.GetProperty("start").GetInt32(), value.GetProperty("duration").GetInt32()));
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
            { Status("The timeline gesture could not be applied to the current schedule.", true); }
            return true;
        }
        var selectedTask = FieldSelection is { } field ? doc.OpenGanttTask(field) : selected is { } item ? doc.OpenGanttTask(item) : null;
        if (action == "delete" && selectedTask != null) _ = RemoveGanttTaskAsync(selectedTask);
        else Status("Use the schedule inspector to insert, reorder and remove tasks while preserving dependencies.");
        return true;
    }
}

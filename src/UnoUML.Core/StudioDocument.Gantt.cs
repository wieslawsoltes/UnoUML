using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnoUML.Syntax;

namespace UnoUML.Core;

/// <summary>A source snapshot of a calendar-day schedule. Reopen after any document mutation.</summary>
public sealed class GanttDiagramEdit
{
    internal GanttDiagramEdit(string source, int page, int offset, GanttSource syntax)
    {
        Source = source; Page = page; Offset = offset; Syntax = syntax;
        Tasks = Array.AsReadOnly(syntax.Tasks.Select((_, i) => new GanttTaskEdit(this, i)).ToArray());
    }
    internal string Source { get; }
    internal int Page { get; }
    internal int Offset { get; }
    internal GanttSource Syntax { get; }
    public IReadOnlyList<GanttTaskEdit> Tasks { get; }
    public DateOnly? ProjectStart => Syntax.Epoch;
    public string Title => Syntax.Title;
    public bool ShowFooter => Syntax.Footer;
}

public sealed class GanttTaskEdit
{
    internal GanttTaskEdit(GanttDiagramEdit diagram, int index) { Diagram = diagram; Index = index; }
    public GanttDiagramEdit Diagram { get; }
    public int Index { get; }
    internal GanttSourceTask Syntax => Diagram.Syntax.Tasks[Index];
    public string Id => Syntax.Id;
    public string Label => Syntax.Caption;
    public bool HasAlias => Syntax.Statements.Any(s => s.AliasToken.Length > 0);
    public int StartDay => Syntax.Start - Diagram.Syntax.EpochDay;
    public int EndDay => Syntax.End - Diagram.Syntax.EpochDay;
    public int Duration => EndDay - StartDay + 1;
    public bool IsMilestone => Syntax.Milestone;
    public string? PredecessorId => Syntax.Predecessor?.Id;
    public bool AfterPredecessorEnd => Syntax.PredecessorEnd;
    public int? Completion => Syntax.Percent >= 0 ? Syntax.Percent : null;
    public string Color => Syntax.Fill;
    internal SourceToken NameToken => Syntax.Statements.FirstOrDefault()?.NameToken ?? Diagram.Syntax.Statements.First(s => s.ArrowTarget == Syntax).ArrowTargetToken;
    public int NameStart => NameToken.Start;
    public int NameLength => NameToken.Length;
}

/// <summary>Inclusive day offsets from project start. A predecessor replaces the explicit start constraint.</summary>
public sealed record GanttTaskSchedule(int StartDay, int Duration = 1, bool IsMilestone = false,
    string? PredecessorId = null, bool AfterPredecessorEnd = true, int? Completion = null, string Color = "#a0b9e7");

public sealed partial class StudioDocument
{
    public GanttDiagramEdit? OpenGanttDiagram() => Page.Family == "Gantt" && GanttSource.Parse(Page.Source) is { } syntax
        ? new(Source, ActivePage, Index.Lines[Page.StartLine].Start, syntax) : null;

    public GanttTaskEdit? OpenGanttTask(SourceFieldEdit field)
    {
        if (!CurrentStructuredField(field) || field.Kind != "gantt-label" || OpenGanttDiagram() is not { } diagram) return null;
        int at = field.Offset - diagram.Offset;
        return diagram.Tasks.FirstOrDefault(t => t.Syntax.Statements.Any(s => s.NameToken.Start == at && s.NameToken.Length == field.Length) ||
            diagram.Syntax.Statements.Any(s => s.ArrowTarget == t.Syntax && s.ArrowTargetToken.Start == at && s.ArrowTargetToken.Length == field.Length));
    }

    public GanttTaskEdit? OpenGanttTask(DiagramObject item)
    {
        if (!Objects.Contains(item) || OpenGanttDiagram() is not { } diagram) return null;
        int line = item.Line - Page.StartLine;
        return diagram.Tasks.FirstOrDefault(t => t.Syntax.Line == line || t.Syntax.Statements.Any(s => s.Row.Index == line));
    }

    private bool Current(GanttDiagramEdit diagram) => diagram.Page == ActivePage && ReferenceEquals(diagram.Source, Source);
    private bool ApplyGanttLabelField(SourceFieldEdit field, string value) => OpenGanttTask(field) is { } task && RenameGanttTask(task, value);

    /// <summary>Renames the display caption and every unaliased reference without changing unrelated literals.</summary>
    public bool RenameGanttTask(GanttTaskEdit edit, string label)
    {
        var diagram = edit.Diagram;
        if (!Current(diagram) || !GanttSource.Name(label) || label == edit.Label ||
            !edit.HasAlias && diagram.Syntax.Tasks.Any(t => t != edit.Syntax && (t.Id == label || t.Caption == label))) return false;
        var changes = new List<Change>();
        foreach (var statement in diagram.Syntax.Statements)
        {
            string name = Page.Source.Substring(statement.NameToken.Start, statement.NameToken.Length);
            if (statement.Task == edit.Syntax && (!edit.HasAlias || statement.AliasToken.Length > 0 || name == edit.Label && name != edit.Id))
                changes.Add(new(statement.NameToken.Start, statement.NameToken.End, edit.HasAlias && statement.AliasToken.Length == 0 ? edit.Id : label));
            if (statement.ArrowTarget == edit.Syntax && (!edit.HasAlias ||
                edit.Label != edit.Id && Page.Source.Substring(statement.ArrowTargetToken.Start, statement.ArrowTargetToken.Length) == edit.Label))
                changes.Add(new(statement.ArrowTargetToken.Start, statement.ArrowTargetToken.End, edit.HasAlias ? edit.Id : label));
            foreach (var clause in statement.Clauses.Where(c => c.Reference.Length > 0 && diagram.Syntax.Resolve(c.Reference) == edit.Syntax &&
                (!edit.HasAlias || c.Reference == edit.Label && c.Reference != edit.Id)))
                changes.Add(new(clause.ReferenceToken.Start, clause.ReferenceToken.End, edit.HasAlias ? edit.Id : label));
        }
        return CommitGantt(diagram, changes);
    }

    public bool InsertGanttTask(GanttDiagramEdit diagram, int index, string label, string alias, GanttTaskSchedule schedule)
    {
        if (!Current(diagram) || index < 0 || index > diagram.Tasks.Count || !GanttSource.Name(label) ||
            alias.Length > 0 && !GanttSource.Name(alias) || !ValidGanttSchedule(diagram, schedule)) return false;
        string id = alias.Length > 0 ? alias : label;
        if (diagram.Syntax.Ids.ContainsKey(id) || diagram.Syntax.Tasks.Any(t => t.Caption == id) || alias.Length == 0 && diagram.Syntax.Tasks.Any(t => t.Caption == label)) return false;
        string text = GanttSubject(id, label, alias.Length > 0) + " " + GanttScheduleText(schedule);
        if (index == diagram.Tasks.Count)
        {
            string nl = StructuredNewLine(diagram.Syntax.InsertAt);
            return CommitGantt(diagram, [new(diagram.Syntax.InsertAt, diagram.Syntax.InsertAt, text + nl)]);
        }
        // Reordering deliberately normalizes task contributions while keeping all comments and directives.
        var ordered = diagram.Syntax.Tasks.Select(t => GanttCanonical(diagram, t)).ToList();
        ordered.Insert(index, text);
        return RebuildGanttTaskRows(diagram, ordered);
    }

    public bool SetGanttTaskSchedule(GanttTaskEdit task, GanttTaskSchedule schedule)
    {
        if (!Current(task.Diagram) || !ValidGanttSchedule(task.Diagram, schedule) || schedule.PredecessorId == task.Id) return false;
        return RewriteGanttTasks(task.Diagram, new() { [task.Syntax] = GanttSubject(task.Id, task.Label, task.HasAlias) + " " + GanttScheduleText(schedule) });
    }

    /// <summary>Moves/resizes a timeline bar. Explicit movement replaces its prior start dependency.</summary>
    public bool MoveGanttTask(GanttTaskEdit task, int startDay, int duration) => SetGanttTaskSchedule(task,
        new(startDay, task.IsMilestone ? 1 : duration, task.IsMilestone, Completion: task.Completion, Color: task.Color));

    /// <summary>Changes the finish by duration while retaining the existing start dependency.</summary>
    public bool ResizeGanttTask(GanttTaskEdit task, int duration) => !task.IsMilestone && SetGanttTaskSchedule(task,
        new(task.StartDay, duration, false, task.PredecessorId, task.AfterPredecessorEnd, task.Completion, task.Color));

    public bool ReorderGanttTask(GanttTaskEdit task, int index)
    {
        if (!Current(task.Diagram) || index < 0 || index >= task.Diagram.Tasks.Count || index == task.Index) return false;
        var rows = task.Diagram.Syntax.Tasks.Select(t => GanttCanonical(task.Diagram, t)).ToList();
        string moved = rows[task.Index]; rows.RemoveAt(task.Index); rows.Insert(index, moved);
        return RebuildGanttTaskRows(task.Diagram, rows);
    }

    /// <summary>Removes a task. Optional detachment fixes direct dependents at their current scheduled dates.</summary>
    public bool RemoveGanttTask(GanttTaskEdit task, bool detachDependents = false)
    {
        if (!Current(task.Diagram)) return false;
        var dependents = task.Diagram.Tasks.Where(t => t.Syntax.Predecessor == task.Syntax).ToArray();
        if (dependents.Length > 0 && !detachDependents) return false;
        var replacements = new Dictionary<GanttSourceTask, string?> { [task.Syntax] = null };
        foreach (var next in dependents)
            replacements[next.Syntax] = GanttSubject(next.Id, next.Label, next.HasAlias) + " " + GanttScheduleText(new(next.StartDay, next.Duration, next.IsMilestone, Completion: next.Completion, Color: next.Color));
        return RewriteGanttTasks(task.Diagram, replacements);
    }

    public bool SetGanttSettings(GanttDiagramEdit diagram, DateOnly? projectStart, string title, bool showFooter)
    {
        if (!Current(diagram) || title.Length > 0 && !GanttSource.Name(title)) return false;
        var changes = new List<Change>(); var additions = new StringBuilder(); string nl = StructuredNewLine(diagram.Syntax.Header.Start); bool valid = true;
        void Set(SourceRow? row, string text)
        {
            if (row == null) { if (text.Length > 0) additions.Append(text).Append(nl); return; }
            if (!GanttRowReplacement(row, text.Length == 0 ? [] : [text], out var change)) valid = false;
            else changes.Add(change);
        }
        if (projectStart != diagram.ProjectStart) Set(diagram.Syntax.ProjectRow, projectStart is { } date ? "Project starts " + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "");
        if (title != diagram.Title) Set(diagram.Syntax.TitleRow, title.Length > 0 ? "title " + title : "");
        if (showFooter != diagram.ShowFooter) Set(diagram.Syntax.FooterRow, showFooter ? "" : "hide footbox");
        if (additions.Length > 0) changes.Add(new(diagram.Syntax.Header.Next, diagram.Syntax.Header.Next, additions.ToString()));
        return valid && CommitGantt(diagram, changes);
    }

    private static bool ValidGanttSchedule(GanttDiagramEdit diagram, GanttTaskSchedule value) =>
        value.StartDay is >= 0 and < 1096 && value.Duration is > 0 and <= 1096 &&
        (!value.IsMilestone || value.Duration == 1 && value.Completion == null) &&
        (value.Completion == null || value.Completion is >= 0 and <= 100) &&
        Regex.IsMatch(value.Color, "^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant) &&
        (value.PredecessorId == null || diagram.Syntax.Ids.ContainsKey(value.PredecessorId));

    private static string GanttSubject(string id, string caption, bool explicitAlias = false) => "[" + caption + "]" + (id == caption && !explicitAlias ? "" : " as [" + id + "]");
    private static string GanttScheduleText(GanttTaskSchedule value)
    {
        var clauses = new List<string>();
        if (!value.IsMilestone) clauses.Add("lasts " + value.Duration.ToString(CultureInfo.InvariantCulture) + " days");
        string verb = value.IsMilestone ? "happens" : "starts";
        clauses.Add(value.PredecessorId is { } predecessor ? verb + " at [" + predecessor + "]'s " + (value.AfterPredecessorEnd ? "end" : "start") : verb + " D+" + value.StartDay.ToString(CultureInfo.InvariantCulture));
        if (value.Completion is { } completion) clauses.Add("is " + completion.ToString(CultureInfo.InvariantCulture) + "% completed");
        clauses.Add("is colored in " + value.Color);
        return string.Join(" and ", clauses);
    }

    private string GanttCanonical(GanttDiagramEdit diagram, GanttSourceTask task)
    {
        var clauses = new List<string>();
        string? Original(string kind)
        {
            var clause = task.Statements.SelectMany(s => s.Clauses).LastOrDefault(c => c.Kind == kind);
            if (clause == null) return null;
            var row = task.Statements.First(s => s.Clauses.Contains(clause)).Row;
            return row.Code.Substring(clause.Token.Start - row.Start, clause.Token.Length).Trim();
        }
        if (Original("duration") is { } duration) clauses.Add(duration);
        if (Original("start") is { } start) clauses.Add(start);
        if (Original("end") is { } end) clauses.Add(end);
        if (task.Predecessor is { } predecessor) clauses.Add((task.Milestone ? "happens" : "starts") + " at [" + predecessor.Id + "]'s " + (task.PredecessorEnd ? "end" : "start"));
        if (clauses.Count == 0) clauses.Add("lasts 1 day");
        if (task.Percent >= 0) clauses.Add("is " + task.Percent.ToString(CultureInfo.InvariantCulture) + "% completed");
        if (Original("color") is { } color) clauses.Add(color);
        return GanttSubject(task.Id, task.Caption, task.Statements.Any(s => s.AliasToken.Length > 0)) + " " + string.Join(" and ", clauses);
    }

    private bool RewriteGanttTasks(GanttDiagramEdit diagram, Dictionary<GanttSourceTask, string?> replacements)
    {
        // An earlier contribution may be removed while a following physical row
        // starts with `then`. Materialize every existing implicit dependency so
        // no surviving clause can silently bind to a different previous task.
        var changedRows = diagram.Syntax.Statements.Where(s => replacements.ContainsKey(s.Task) || s.ArrowTarget != null && replacements.ContainsKey(s.ArrowTarget) || s.ChainFrom != null)
            .Select(s => s.Row).Distinct().OrderBy(r => r.Start).ToArray();
        var emitted = new HashSet<GanttSourceTask>(); var changes = new List<Change>();
        foreach (var row in changedRows)
        {
            var output = new List<string>();
            foreach (var statement in diagram.Syntax.Statements.Where(s => s.Row == row))
            {
                if (replacements.TryGetValue(statement.Task, out var replacement))
                {
                    if (replacement != null && emitted.Add(statement.Task)) output.Add(replacement);
                    if (statement.ArrowTarget is { } target && !replacements.ContainsKey(target))
                        output.Add("[" + statement.Task.Id + "] -> [" + target.Id + "]");
                }
                else if (statement.ArrowTarget is { } target && replacements.ContainsKey(target))
                {
                    if (statement.Task.Statements.All(s => s.ArrowTarget != null && replacements.ContainsKey(s.ArrowTarget)) && emitted.Add(statement.Task))
                        output.Add(GanttCanonical(diagram, statement.Task));
                }
                else
                {
                    if (statement.ChainFrom is { } predecessor)
                        output.Add(GanttSubject(statement.Task.Id, statement.Task.Caption) + " starts at [" + predecessor.Id + "]'s end");
                    output.Add(row.Code.Substring(statement.Token.Start - row.Start, statement.Token.Length).Trim());
                }
                if (statement.ArrowTarget is { } other && replacements.TryGetValue(other, out var targetText) && targetText != null && emitted.Add(other)) output.Add(targetText);
            }
            if (!GanttRowReplacement(row, output, out var change)) return false;
            changes.Add(change);
        }
        return CommitGantt(diagram, changes);
    }

    private bool RebuildGanttTaskRows(GanttDiagramEdit diagram, IReadOnlyList<string> declarations)
    {
        var rows = diagram.Syntax.Statements.Select(s => s.Row).Distinct().OrderBy(r => r.Start).ToArray();
        if (rows.Length == 0) return declarations.Count > 0 && CommitGantt(diagram, [new(diagram.Syntax.InsertAt, diagram.Syntax.InsertAt, string.Join(StructuredNewLine(diagram.Syntax.InsertAt), declarations) + StructuredNewLine(diagram.Syntax.InsertAt))]);
        var changes = new List<Change>(); int cursor = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            int count = i == rows.Length - 1 ? declarations.Count - cursor : Math.Min(declarations.Count - cursor, diagram.Syntax.Statements.Count(s => s.Row == rows[i]));
            if (!GanttRowReplacement(rows[i], declarations.Skip(cursor).Take(count).ToArray(), out var change)) return false;
            changes.Add(change); cursor += count;
        }
        return CommitGantt(diagram, changes);
    }

    private bool GanttRowReplacement(SourceRow row, IReadOnlyList<string> statements, out Change change)
    {
        change = default!;
        string original = Page.Source[row.Start..row.End], nl = StructuredNewLine(row.Start);
        var comments = new List<string>(); int brackets = 0;
        for (int i = 0; i < original.Length; i++)
        {
            char c = original[i];
            if (c == '[') brackets++;
            else if (c == ']') brackets--;
            else if (brackets == 0 && c == '/' && i + 1 < original.Length && original[i + 1] == '\'')
            {
                int end = original.IndexOf("'/", i + 2, StringComparison.Ordinal);
                if (end < 0) return false; // A structural row cannot detach a multi-line comment delimiter.
                comments.Add(original[i..(end + 2)]); i = end + 1;
            }
            else if (brackets == 0 && c == '\'' && i + 1 < original.Length && original[i + 1] == '/') return false;
            else if (brackets == 0 && c == '\'' && (i == 0 || char.IsWhiteSpace(original[i - 1]))) { comments.Add(original[i..]); break; }
        }
        string indent = new(original.TakeWhile(c => c is ' ' or '\t').ToArray());
        var output = new StringBuilder();
        foreach (string statement in statements) output.Append(indent).Append(statement).Append(nl);
        foreach (string comment in comments) output.Append(indent).Append(comment).Append(nl);
        change = new(row.Start, row.Next, output.ToString()); return true;
    }

    private bool CommitGantt(GanttDiagramEdit diagram, List<Change> changes) => Current(diagram) &&
        CommitStructured(diagram.Offset, changes, candidate => GanttSource.Parse(candidate) != null);
}

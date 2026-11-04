using System.Globalization;
using System.Text.RegularExpressions;

namespace UnoUML.Syntax;

internal sealed class GanttSourceTask(string id, string caption, int line)
{
    internal readonly string Id = id, Caption = caption;
    internal readonly int Line = line;
    internal readonly List<GanttSourceStatement> Statements = [];
    internal int? Duration, StartAt, EndAt;
    internal int DurationLine = line, StartLine = line, EndLine = line;
    internal int Start, End, Percent = -1;
    internal bool Milestone, PredecessorEnd;
    internal GanttSourceTask? Predecessor;
    internal string? PredecessorName;
    internal string Fill = "#a0b9e7";
}

internal sealed class GanttSourceStatement(SourceRow row, SourceToken token, SourceToken name, SourceToken alias, GanttSourceTask task)
{
    internal readonly SourceRow Row = row;
    internal readonly SourceToken Token = token, NameToken = name, AliasToken = alias;
    internal readonly GanttSourceTask Task = task;
    internal readonly List<GanttSourceClause> Clauses = [];
    internal GanttSourceTask? ChainFrom, ArrowTarget;
    internal SourceToken ArrowTargetToken;
}

internal sealed record GanttSourceClause(string Kind, SourceToken Token, SourceToken ValueToken, string Reference = "", SourceToken ReferenceToken = default);

/// <summary>Bounded, source-addressed calendar-day scheduling shared by authoring and native rendering.</summary>
internal sealed partial class GanttSource
{
    internal readonly List<GanttSourceTask> Tasks = [];
    internal readonly List<GanttSourceStatement> Statements = [];
    internal readonly Dictionary<string, GanttSourceTask> Ids = new(StringComparer.Ordinal);
    internal DateOnly? Epoch;
    internal string Title = "";
    internal bool Footer = true;
    internal SourceRow Header = null!, Closing = null!;
    internal SourceRow? ProjectRow, TitleRow, FooterRow;
    internal int InsertAt => Closing.Start;
    internal int EpochDay => Epoch?.DayNumber ?? 0;

    [GeneratedRegex(@"^\[(?<name>[^\[\]]+)\](?:\s+as\s+\[(?<alias>[^\[\]]+)\])?(?:\s+(?<body>.+)|(?<body>->\s*\[[^\[\]]+\]))$", RegexOptions.CultureInvariant)]
    private static partial Regex Subject();
    [GeneratedRegex(@"^(?:lasts|requires)\s+(?<value>[1-9][0-9]{0,3})\s+(?<unit>day|week)s?$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();
    [GeneratedRegex(@"^(?<kind>starts|ends|happens)(?:\s+on|\s+at|\s+the)?\s+(?<value>\d{4}[-/]\d{2}[-/]\d{2}|D\+[0-9]{1,5})$", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();
    [GeneratedRegex(@"^(?<kind>starts|happens)\s+at\s+\[(?<name>[^\[\]]+)\]'s\s+(?<point>start|end)$", RegexOptions.CultureInvariant)]
    private static partial Regex RelativePattern();
    [GeneratedRegex(@"^is\s+(?<value>\d{1,3})%\s+(?:completed|complete)$", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressPattern();
    [GeneratedRegex(@"^is\s+colored\s+in\s+(?<value>#[A-Fa-f0-9]{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();
    [GeneratedRegex(@"^->\s*\[(?<name>[^\[\]]+)\]$", RegexOptions.CultureInvariant)]
    private static partial Regex ArrowPattern();

    internal static bool Name(string value) => value.Length is > 0 and <= 450 && value == value.Trim() &&
        !value.Any(char.IsControl) && value.IndexOfAny(['[', ']', '<', '>']) < 0 &&
        !value.Contains("\\n", StringComparison.Ordinal) && !value.Contains("\\r", StringComparison.Ordinal);

    internal GanttSourceTask? Resolve(string name)
    {
        if (Ids.TryGetValue(name, out var exact)) return exact;
        GanttSourceTask? found = null;
        foreach (var task in Tasks)
            if (task.Caption == name) { if (found != null) return null; found = task; }
        return found;
    }

    private GanttSourceTask? GetTask(string name, string alias, int line)
    {
        if (!Name(name) || alias.Length > 0 && !Name(alias)) return null;
        if (alias.Length > 0)
        {
            if (Ids.TryGetValue(alias, out var existing)) return existing.Caption == name ? existing : null;
            if (Tasks.Any(t => t.Id == name && t.Caption != name)) return null;
        }
        else if (Resolve(name) is { } existing) return existing;
        else if (Tasks.Any(t => t.Caption == name)) return null; // Ambiguous unaliased reference.
        if (Tasks.Count >= 760) return null;
        var task = new GanttSourceTask(alias.Length > 0 ? alias : name, name, line);
        if (!Ids.TryAdd(task.Id, task)) return null;
        Tasks.Add(task); return task;
    }

    internal static GanttSource? Parse(string source)
    {
        if (source.Length > 524288 || Rows(source) is not { } rows) return null;
        var chart = new GanttSource();
        bool opened = false, closed = false;
        GanttSourceTask? previous = null;
        foreach (var row in rows)
        {
            string text = row.Trimmed;
            if (text.Length == 0) continue;
            if (text.Length > 3000 || closed) return null;
            if (!opened)
            {
                if (!StructuredText.Header(text, "gantt")) return null;
                chart.Header = row; opened = true; continue;
            }
            if (text == "@endgantt") { chart.Closing = row; closed = true; continue; }
            if (Regex.Match(text, @"^skinparam\s+dpi\s+(\d+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase) is { Success: true } dpi)
            {
                if (!int.TryParse(dpi.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value is < 36 or > 600) return null;
                continue;
            }
            if (text == "hide footbox") { if (chart.FooterRow != null) return null; chart.Footer = false; chart.FooterRow = row; continue; }
            if (text.StartsWith("title ", StringComparison.Ordinal))
            {
                if (chart.TitleRow != null || !Name(text[6..].Trim())) return null;
                chart.Title = text[6..].Trim(); chart.TitleRow = row; continue;
            }
            if (text.StartsWith("Project ", StringComparison.Ordinal))
            {
                var match = DatePattern().Match(text[8..]);
                if (chart.ProjectRow != null || chart.Tasks.Count > 0 || !match.Success || match.Groups["kind"].Value != "starts" ||
                    !DateOnly.TryParseExact(match.Groups["value"].Value.Replace('/', '-'), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return null;
                chart.Epoch = date; chart.ProjectRow = row; continue;
            }
            bool chained = text.StartsWith("then [", StringComparison.Ordinal);
            int first = chained ? 5 : 0, offset = row.ContentStart;
            foreach (var part in Split(text, first, " then "))
            {
                string sentence = text.Substring(part.Start, part.Length);
                if (!chart.ReadStatement(row, sentence, offset + part.Start, chained, ref previous)) return null;
                chained = true;
            }
        }
        if (!closed || !chart.Schedule()) return null;
        return chart;
    }

    private bool ReadStatement(SourceRow row, string text, int offset, bool chained, ref GanttSourceTask? previous)
    {
        var match = Subject().Match(text);
        if (!match.Success || GetTask(match.Groups["name"].Value, match.Groups["alias"].Value, row.Index) is not { } task) return false;
        var statement = new GanttSourceStatement(row, new(offset, text.Length), StructuredText.Token(match.Groups["name"], offset), StructuredText.Token(match.Groups["alias"], offset), task);
        task.Statements.Add(statement); Statements.Add(statement);
        if (chained)
        {
            if (previous == null || !Depends(task, previous.Id, true, row.Index)) return false;
            statement.ChainFrom = previous;
        }
        string body = match.Groups["body"].Value;
        int bodyOffset = offset + match.Groups["body"].Index;
        if (ArrowPattern().Match(body) is { Success: true } arrow)
        {
            var other = GetTask(arrow.Groups["name"].Value, "", row.Index);
            if (other == null || !Depends(other, task.Id, true, row.Index)) return false;
            statement.ArrowTarget = other; statement.ArrowTargetToken = StructuredText.Token(arrow.Groups["name"], bodyOffset);
            previous = other; return true;
        }
        foreach (var part in Split(body, 0, " and "))
        {
            string clause = body.Substring(part.Start, part.Length).Trim();
            int at = bodyOffset + part.Start + body.Substring(part.Start, part.Length).Length - body.Substring(part.Start, part.Length).TrimStart().Length;
            if (!Apply(task, statement, clause, at)) return false;
        }
        previous = task; return true;
    }

    private static IEnumerable<SourceToken> Split(string value, int first, string separator)
    {
        int start = first, depth = 0;
        for (int i = first; i <= value.Length - separator.Length; i++)
        {
            if (value[i] == '[') depth++;
            else if (value[i] == ']') depth--;
            if (depth != 0 || !value.AsSpan(i).StartsWith(separator, StringComparison.Ordinal)) continue;
            yield return new(start, i - start); start = i + separator.Length; i = start - 1;
        }
        yield return new(start, value.Length - start);
    }

    private static bool Depends(GanttSourceTask task, string name, bool end, int line)
    {
        if (task.PredecessorName != null || task.StartAt != null || task.EndAt != null || task.Id == name) return false;
        task.PredecessorName = name; task.PredecessorEnd = end; task.StartLine = line; return true;
    }

    private bool Apply(GanttSourceTask task, GanttSourceStatement statement, string clause, int offset)
    {
        SourceToken Token(Group group) => StructuredText.Token(group, offset);
        var token = new SourceToken(offset, clause.Length);
        var match = DurationPattern().Match(clause);
        if (match.Success)
        {
            if (task.Duration != null || task.Milestone) return false;
            task.Duration = int.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture) * (match.Groups["unit"].Value == "week" ? 7 : 1);
            task.DurationLine = statement.Row.Index;
            statement.Clauses.Add(new("duration", token, Token(match.Groups["value"]))); return true;
        }
        match = DatePattern().Match(clause);
        if (match.Success)
        {
            string value = match.Groups["value"].Value, kind = match.Groups["kind"].Value;
            int day;
            if (value.StartsWith("D+", StringComparison.Ordinal)) day = EpochDay + int.Parse(value.AsSpan(2), CultureInfo.InvariantCulture);
            else if (Epoch != null && DateOnly.TryParseExact(value.Replace('/', '-'), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) day = date.DayNumber;
            else return false;
            if (day < EpochDay) return false;
            if (kind == "ends")
            {
                if (task.EndAt != null || task.PredecessorName != null || task.Milestone) return false;
                task.EndAt = day; task.EndLine = statement.Row.Index;
            }
            else
            {
                if (task.StartAt != null || task.PredecessorName != null || task.Milestone) return false;
                task.Milestone = kind == "happens";
                if (task.Milestone && (task.Duration != null || task.EndAt != null || task.Percent >= 0)) return false;
                task.StartAt = day; task.StartLine = statement.Row.Index;
            }
            statement.Clauses.Add(new(kind == "ends" ? "end" : "start", token, Token(match.Groups["value"]))); return true;
        }
        match = RelativePattern().Match(clause);
        if (match.Success)
        {
            task.Milestone = match.Groups["kind"].Value == "happens";
            if (task.Milestone && (task.Duration != null || task.Percent >= 0) || !Depends(task, match.Groups["name"].Value, match.Groups["point"].Value == "end", statement.Row.Index)) return false;
            statement.Clauses.Add(new("dependency", token, default, match.Groups["name"].Value, Token(match.Groups["name"]))); return true;
        }
        match = ProgressPattern().Match(clause);
        if (match.Success)
        {
            int percent = int.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
            if (percent > 100 || task.Milestone) return false;
            task.Percent = percent; statement.Clauses.Add(new("progress", token, Token(match.Groups["value"]))); return true;
        }
        match = ColorPattern().Match(clause);
        if (match.Success)
        {
            task.Fill = match.Groups["value"].Value;
            statement.Clauses.Add(new("color", token, Token(match.Groups["value"]))); return true;
        }
        return false;
    }

    private bool Schedule()
    {
        foreach (var task in Tasks)
            if (task.PredecessorName is { } name)
            {
                task.Predecessor = Resolve(name);
                if (task.Predecessor == null || task.Predecessor == task) return false;
            }
        var ready = new Queue<GanttSourceTask>(Tasks.Where(t => t.Predecessor == null));
        var dependents = Tasks.Where(t => t.Predecessor != null).ToLookup(t => t.Predecessor!);
        int count = 0;
        while (ready.TryDequeue(out var task))
        {
            int duration = task.Duration ?? 1;
            task.Start = task.Predecessor is { } pred ? task.PredecessorEnd ? pred.End + (task.Milestone ? 0 : 1) : pred.Start : task.StartAt ?? (task.EndAt is { } end ? end - duration + 1 : EpochDay);
            task.End = task.EndAt ?? task.Start + duration - 1;
            if (task.Start < EpochDay || task.End < task.Start || task.End > DateOnly.MaxValue.DayNumber || task.End - EpochDay >= 1096 ||
                task.Duration != null && task.End - task.Start + 1 != duration) return false;
            count++;
            foreach (var dependent in dependents[task]) ready.Enqueue(dependent);
        }
        return count == Tasks.Count;
    }

    // Apostrophes and conjunctions inside bracketed task names are literal text.
    private static List<SourceRow>? Rows(string source)
    {
        var result = new List<SourceRow>(); bool block = false;
        for (int start = 0, index = 0; start < source.Length; index++)
        {
            int end = start; while (end < source.Length && source[end] is not ('\r' or '\n')) end++;
            if (end - start > 32768) return null;
            int next = end; if (next < source.Length && source[next] == '\r') next++; if (next < source.Length && source[next] == '\n') next++;
            var code = source[start..end].ToCharArray(); int brackets = 0;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (start == 0 && i == 0 && c == '\uFEFF') { code[i] = ' '; continue; }
                if (block)
                {
                    code[i] = ' ';
                    if (c == '\'' && i + 1 < code.Length && code[i + 1] == '/') { code[++i] = ' '; block = false; }
                }
                else if (brackets > 0 && c == '/' && i + 1 < code.Length && code[i + 1] == '\'') return null; // Preprocessing transforms this caption; no literal authoring projection.
                else if (c == '[') brackets++;
                else if (c == ']') { if (--brackets < 0) return null; }
                else if (brackets == 0 && c == '/' && i + 1 < code.Length && code[i + 1] == '\'') { code[i] = code[++i] = ' '; block = true; }
                else if (brackets == 0 && c == '\'' && (i == 0 || char.IsWhiteSpace(code[i - 1]))) { Array.Fill(code, ' ', i, code.Length - i); break; }
            }
            if (brackets != 0) return null;
            result.Add(new(index, start, end, next, new(code))); start = next;
        }
        return block ? null : result;
    }
}

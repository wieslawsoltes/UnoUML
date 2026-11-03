using System.Globalization;
using System.Text.RegularExpressions;

namespace UnoUML.PlantUml;

internal static partial class TimingRenderer
{
    private const string Decimal = @"[+-]?(?:\d+(?:\.\d*)?|\.\d+)";
    private const string Identifier = @"[\p{L}_][\w.]*";
    private const string StateCode = @"[\p{L}\p{N}_.+\-]+";
    private const string Quoted = "\"(?:\\\\.|[^\"\\\\])*\"";
    private const string Name = "(?:" + Quoted + "|" + Identifier + ")";
    private const string TimeExpression = @"(?::[\p{L}\p{N}_.]+(?:[+-](?:\d+(?:\.\d*)?|\.\d+))?|\d+/\d+/\d+|\d+:\d+:\d+|" +
        Identifier + @"\*\d+|\+-?(?:\d+(?:\.\d*)?|\.\d+)|-?(?:\d+(?:\.\d*)?|\.\d+))";
    private const string StateExpression = "(?:" + Quoted + @"|\{(?:hidden|\.\.\.|-|\?|" + StateCode + @"\s*,\s*" + StateCode + @")\}|" + StateCode + ")";
    private const string ColorExpression = @"(?:\s*(?<color>#[\w]+(?:;(?:line|back):#?[\w]+)*))?";
    private const RegexOptions Grammar = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    [GeneratedRegex(@"^(?<compact>compact\s+)?(?<kind>robust|concise|rectangle|binary|analog|clock)\s+(?<name>" + Name +
        @")(?:\s+<<(?<stereotype>[^<>\r\n]+)>>)?(?:\s+(?:between|from)\s+(?<low>" + Decimal + @")\s+(?:and|to)\s+(?<high>" + Decimal + @"))?" +
        @"(?:\s+as\s+(?<alias>" + Name + @"))?(?:\s+<<(?<stereotype>[^<>\r\n]+)>>)?" +
        @"(?:\s+with\s+period\s+(?<period>" + Decimal + @")(?:\s+pulse\s+(?<pulse>" + Decimal + @"))?(?:\s+offset\s+(?<offset>" + Decimal + @"))?)?" +
        @"(?:\s+<<(?<stereotype>[^<>\r\n]+)>>)?" + ColorExpression + "$", Grammar)]
    private static partial Regex Declaration();
    [GeneratedRegex(@"^(?<subject>" + Identifier + "|" + TimeExpression + @")\s+is\s+(?<state>" + StateExpression + ")" +
        ColorExpression + @"(?:\s*:\s*(?<annotation>.*))?$", Grammar)]
    private static partial Regex Transition();
    [GeneratedRegex(@"^@(?<time>" + TimeExpression + @")(?:\s+as\s+:(?<anchor>[\p{L}\p{N}_.]+))?$", Grammar)]
    private static partial Regex TimeCommand();
    [GeneratedRegex(@"^(?<from>" + Identifier + @")(?:@(?<start>" + TimeExpression + @"))?\s*(?<arrow>-+(?:\[[^\]\r\n]*\])?>)\s*(?<to>" +
        Identifier + @")(?:@(?<end>" + TimeExpression + @"))?(?:\s*:\s*(?<label>.*))?$", Grammar)]
    private static partial Regex Message();
    [GeneratedRegex(@"^(?<track>" + Identifier + @")?@(?<start>" + TimeExpression + @")\s*(?<arrow><-+(?:\[[^\]\r\n]*\])?-*>)\s*@(?<end>" +
        TimeExpression + @")(?:\s*:\s*(?<label>.*))?$", Grammar)]
    private static partial Regex ConstraintCommand();
    [GeneratedRegex(@"^highlight\s+(?<start>" + TimeExpression + @")\s+to\s+(?<end>" + TimeExpression + ")" +
        ColorExpression + @"(?:\s*:\s*(?<label>.*))?$", Grammar)]
    private static partial Regex HighlightCommand();
    [GeneratedRegex(@"^note\s+(?<position>top|bottom)\s+of\s+(?<track>" + Identifier +
        @")(?:\s+<<(?<stereotype>[^<>\r\n]+)>>)?(?:\s*:\s*(?<label>.+))?$", Grammar)]
    private static partial Regex NoteCommand();
    [GeneratedRegex(@"^scale\s+(?<ticks>\d+)\s*(?<unit>[smhdy])?\s+as\s+(?<pixels>\d+)\s+pixels?$", Grammar)]
    private static partial Regex ScaleCommand();
    [GeneratedRegex(@"^use\s*date\s*format\s*(?<format>" + Quoted + ")$", Grammar)]
    private static partial Regex DateFormatCommand();
    [GeneratedRegex(@"^(?<track>" + Identifier + @")\s+has\s+(?:(?<label>" + Quoted + @")\s+as\s+(?<alias>" + StateCode +
        @")|(?<states>" + StateCode + @"(?:\s*,\s*" + StateCode + @")*))$", Grammar)]
    private static partial Regex DefineStateCommand();
    [GeneratedRegex(@"^(?<track>" + Identifier + @")\s+is\s+(?<height>\d+)\s+pixels?\s+height$", Grammar)]
    private static partial Regex HeightCommand();
    [GeneratedRegex(@"^(?<track>" + Identifier + @")\s+ticks\s+(?:every|num\s+on\s+multiple)\s+(?<step>\d+)$", Grammar)]
    private static partial Regex TicksCommand();
    [GeneratedRegex(@"^(?<command>hide|manual)\s+time[- ]?axis$", Grammar)]
    private static partial Regex AxisCommand();

    private readonly record struct TextToken(int Start, int Length, string Kind);
    private readonly record struct Paint(string? Background = null, string? Line = null, string? Dash = null, int? Thickness = null);
    private sealed record StateSource(string Label, int Line, TextToken? Token);
    private sealed record Change(decimal Time, string[] States, double Value, string Annotation, int Line,
        TextToken? StateToken, TextToken AnnotationToken, Paint Paint)
    {
        internal string State => States[0];
        internal StyleLabel StateLabel = null!, AnnotationLabel = null!;
    }
    private sealed record Link(string From, string To, decimal Start, decimal End, string Label, int Line, TextToken Token, Paint Paint)
    {
        internal StyleLabel Caption = null!;
        internal int CaptionTop;
        internal double CaptionLeft;
    }
    private sealed record Constraint(decimal Start, decimal End, string Label, int Line, TextToken Token, Paint Paint)
    {
        internal StyleLabel Caption = null!;
        internal int Top;
    }
    private sealed record Highlight(decimal Start, decimal End, string Label, int Line, TextToken Token, Paint Paint)
    {
        internal StyleLabel Caption = null!;
        internal int Top;
    }
    private sealed record Note(bool Above, decimal Time, int Line, string Stereotype)
    {
        internal readonly List<StateSource> Rows = [];
        internal StyleLabel[] Labels = [];
        internal int Top;
        internal int Width => Math.Max(30, Labels.Max(label => label.Width) + 18);
        internal int Height => Labels.Sum(label => label.Height + 4) + 10;
    }
    private sealed record Legend(bool Above, string Align, int Line)
    {
        internal readonly List<string> Rows = [];
        internal readonly List<TextToken> Tokens = [];
        internal StyleLabel[] Labels = [];
        internal int Width => Math.Max(48, Labels.Max(label => label.Width) + 18);
        internal int Height => Labels.Sum(label => label.Height + 4) + 10;
    }
    private sealed class Track(string kind, string id, string label, int line)
    {
        internal readonly string Kind = kind, Id = id, Label = label;
        internal readonly int Line = line;
        internal readonly List<Change> Changes = [];
        internal readonly List<Link> Messages = [];
        internal readonly List<Constraint> Constraints = [];
        internal readonly List<Note> Notes = [];
        internal readonly Dictionary<string, StateSource> Definitions = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, StateSource> StateSources = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, StyleLabel> StateLabels = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, double> StateOffsets = new(StringComparer.Ordinal);
        internal readonly List<(double Value, StyleLabel Label)> ValueTicks = [];
        internal Change? Initial;
        internal StyleLabel Caption = null!, LowLabel = null!, HighLabel = null!;
        internal string Stereotype = "";
        internal string NumberFormat = "0.0##########";
        internal TextToken? CaptionToken;
        internal Paint Paint;
        internal bool Compact, HasBounds;
        internal int Top, Height, HeaderHeight, SignalTop, SignalHeight, RibbonHeight, InitialWidth, RequestedHeight, TickStep;
        internal double Low, High;
        internal double? FirstAnalogValue;
        internal decimal Period, Pulse, Offset, StartTime;
        internal bool Ribbon => Kind is "concise" or "rectangle";
    }
    private sealed class TimingDocument
    {
        internal readonly Dictionary<string, Track> Tracks = new(StringComparer.Ordinal);
        internal readonly SortedDictionary<decimal, int> Ticks = [];
        internal readonly Dictionary<string, TimeMark> Anchors = new(StringComparer.Ordinal);
        internal readonly List<Link> Links = [];
        internal readonly List<Highlight> Highlights = [];
        internal Legend? Legend;
        internal Track? Selected;
        internal TimeMark? Now;
        internal TimeMark AxisFormat;
        internal string Title = "";
        internal string? DatePattern;
        internal int TitleLine = -1, ScalePixels;
        internal decimal ScaleTicks;
        internal bool ShowAxis = true, ManualAxis, Compact;

        internal void AddTime(TimeMark time, int line, bool move = true)
        {
            if (move) Now = time;
            Ticks.TryAdd(time.Value, line);
            if (time.Format != TimeFormat.Decimal) AxisFormat = time;
        }
    }

    internal static string? TryRender(string source)
    {
        if (source.Length > 512_000) return null;
        var document = new TimingDocument();
        bool opened = false, closed = false, inLegend = false;
        Note? openNote = null;
        int sourceLine = -1, nextOffset = 0;
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            sourceLine++;
            int lineOffset = nextOffset + raw.Length - raw.TrimStart().Length;
            nextOffset += raw.Length;
            if (nextOffset < source.Length && source[nextOffset] == '\r') nextOffset++;
            if (nextOffset < source.Length && source[nextOffset] == '\n') nextOffset++;
            string line = raw.Trim().ToString();
            if (sourceLine == 0 && line.StartsWith('\uFEFF'))
            {
                line = line[1..];
                lineOffset += 1 + line.Length - line.TrimStart().Length;
                line = line.TrimStart();
            }
            if (line.Length > 4096) return null;
            if (openNote != null)
            {
                if (line.Equals("end note", StringComparison.OrdinalIgnoreCase) || line.Equals("endnote", StringComparison.OrdinalIgnoreCase))
                {
                    if (openNote.Rows.Count == 0) return null;
                    openNote = null;
                }
                else
                {
                    if (openNote.Rows.Count >= 128 || !SvgText.IsSupported(line)) return null;
                    openNote.Rows.Add(new(line, sourceLine, new(lineOffset, line.Length, "text")));
                }
                continue;
            }
            if (inLegend)
            {
                if (line.Equals("endlegend", StringComparison.OrdinalIgnoreCase) || line.Equals("end legend", StringComparison.OrdinalIgnoreCase))
                {
                    if (document.Legend!.Rows.Count == 0) return null;
                    inLegend = false;
                }
                else
                {
                    if (document.Legend!.Rows.Count >= 128 || !SvgText.IsSupported(line) ||
                        (line.Length != 0 && line[0] is '!' or '@' or '|')) return null;
                    document.Legend.Rows.Add(line);
                    document.Legend.Tokens.Add(new(lineOffset, line.Length, "text"));
                }
                continue;
            }
            if (line.Length == 0 || line[0] == '\'') continue;
            if (!opened) { if (line != "@startuml") return null; opened = true; continue; }
            if (closed) return null;
            if (line == "@enduml") { closed = true; continue; }
            if (line.StartsWith("title ", StringComparison.OrdinalIgnoreCase))
            {
                if (document.TitleLine >= 0 || !SvgText.IsSupported(document.Title = line[6..].Trim())) return null;
                document.TitleLine = sourceLine;
                continue;
            }
            if (line.Equals("legend", StringComparison.OrdinalIgnoreCase) || line.StartsWith("legend ", StringComparison.OrdinalIgnoreCase))
            {
                if (document.Legend != null || (document.Legend = ParseLegend(line, sourceLine)) == null) return null;
                inLegend = true;
                continue;
            }
            if (!ReadCommand(document, line, sourceLine, lineOffset, out openNote) || document.Ticks.Count > 4096) return null;
        }
        if (!closed || inLegend || openNote != null || document.Tracks.Count == 0) return null;
        if (document.Ticks.Count == 0) document.Ticks.Add(0, -1);
        if (document.AxisFormat.Format == TimeFormat.Decimal && document.Tracks.Values.Any(track => track.Kind == "clock"))
            document.Ticks.TryAdd(0, -1);
        if (document.Ticks.First().Key < 0 && document.Ticks.Last().Key > 0) document.Ticks.TryAdd(0, -1);
        return Render(document);
    }

    private static bool ReadCommand(TimingDocument document, string line, int sourceLine, int offset, out Note? openNote)
    {
        openNote = null;
        var axis = AxisCommand().Match(line);
        if (axis.Success)
        {
            document.ShowAxis = axis.Groups["command"].Value.Equals("manual", StringComparison.OrdinalIgnoreCase);
            document.ManualAxis = document.ShowAxis;
            return true;
        }
        if (line.Equals("mode compact", StringComparison.OrdinalIgnoreCase))
        {
            document.Compact = true;
            foreach (var track in document.Tracks.Values) track.Compact = true;
            return true;
        }
        var scale = ScaleCommand().Match(line);
        if (scale.Success)
        {
            if (!TimeNumber(scale.Groups["ticks"].Value, out decimal ticks) || ticks <= 0 ||
                !int.TryParse(scale.Groups["pixels"].Value, out int pixels) || pixels is < 1 or > 16000) return false;
            decimal multiplier = scale.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "m" => 60, "h" => 3600, "d" => 86400, "y" => 31557600, _ => 1
            };
            document.ScaleTicks = ticks * multiplier;
            document.ScalePixels = pixels;
            return document.ScaleTicks <= 1_000_000_000_000;
        }
        var date = DateFormatCommand().Match(line);
        if (date.Success)
        {
            string pattern = Unquote(date.Groups["format"].Value);
            if (pattern.Length == 0) return false;
            try { PreprocessorDate.Format(DateTimeOffset.UnixEpoch, pattern, TimeZoneInfo.Utc, CultureInfo.GetCultureInfo("en-US")); }
            catch (FormatException) { return false; }
            document.DatePattern = pattern;
            return true;
        }
        var declaration = Declaration().Match(line);
        if (declaration.Success) return ReadDeclaration(document, declaration, sourceLine, offset);
        var definition = DefineStateCommand().Match(line);
        if (definition.Success) return ReadDefinition(document, definition, sourceLine, offset);
        var height = HeightCommand().Match(line);
        if (height.Success)
        {
            if (!document.Tracks.TryGetValue(height.Groups["track"].Value, out var track) ||
                !int.TryParse(height.Groups["height"].Value, out int pixels) || pixels is < 1 or > 8192) return false;
            track.RequestedHeight = pixels;
            return true;
        }
        var steps = TicksCommand().Match(line);
        if (steps.Success)
        {
            if (!document.Tracks.TryGetValue(steps.Groups["track"].Value, out var track) || track.Kind != "analog" ||
                !int.TryParse(steps.Groups["step"].Value, out int step) || step < 1) return false;
            track.TickStep = step;
            return true;
        }
        var constraint = ConstraintCommand().Match(line);
        if (constraint.Success) return ReadConstraint(document, constraint, sourceLine, offset);
        if (line[0] == '@')
        {
            var time = TimeCommand().Match(line);
            if (time.Success)
            {
                if (!ReadTime(document, time.Groups["time"].Value, document.Now, out var tick)) return false;
                document.AddTime(tick, sourceLine);
                if (time.Groups["anchor"].Success)
                {
                    if (document.Anchors.Count >= 4096) return false;
                    document.Anchors[time.Groups["anchor"].Value] = tick;
                }
                return true;
            }
            if (!document.Tracks.TryGetValue(line[1..], out var track)) return false;
            document.Selected = track;
            return true;
        }
        var highlight = HighlightCommand().Match(line);
        if (highlight.Success)
        {
            if (document.Highlights.Count >= 128 || !ReadTime(document, highlight.Groups["start"].Value, document.Now, out var start) ||
                !ReadTime(document, highlight.Groups["end"].Value, document.Now, out var end) || end.Value <= start.Value ||
                !TryPaint(highlight.Groups["color"].Value, out var paint) || !SvgText.IsSupported(highlight.Groups["label"].Value)) return false;
            document.Highlights.Add(new(start.Value, end.Value, highlight.Groups["label"].Value, sourceLine, Token(highlight.Groups["label"], offset), paint));
            document.AddTime(start, sourceLine, false);
            document.AddTime(end, sourceLine, false);
            return true;
        }
        var note = NoteCommand().Match(line);
        if (note.Success)
        {
            if (!document.Tracks.TryGetValue(note.Groups["track"].Value, out var track) || track.Kind is not ("concise" or "rectangle" or "binary") ||
                track.Notes.Count >= 128 || document.Now is not { } time || !SvgText.IsSupported(note.Groups["label"].Value)) return false;
            var item = new Note(note.Groups["position"].Value.Equals("top", StringComparison.OrdinalIgnoreCase),
                time.Value, sourceLine, note.Groups["stereotype"].Value);
            track.Notes.Add(item);
            if (note.Groups["label"].Success) item.Rows.Add(new(note.Groups["label"].Value, sourceLine, Token(note.Groups["label"], offset)));
            else openNote = item;
            return true;
        }
        var change = Transition().Match(line);
        if (change.Success) return ReadTransition(document, change, sourceLine, offset);
        var message = Message().Match(line);
        if (!message.Success || document.Links.Count >= 4096 ||
            !document.Tracks.ContainsKey(message.Groups["from"].Value) || !document.Tracks.ContainsKey(message.Groups["to"].Value) ||
            !ReadTime(document, message.Groups["start"].Value, document.Now, out var from) ||
            !ReadTime(document, message.Groups["end"].Value, document.Now, out var to) ||
            !SvgText.IsSupported(message.Groups["label"].Value) || !TryArrowPaint(message.Groups["arrow"].Value, out var arrow)) return false;
        document.Links.Add(new(message.Groups["from"].Value, message.Groups["to"].Value, from.Value, to.Value,
            message.Groups["label"].Value, sourceLine, Token(message.Groups["label"], offset), arrow));
        document.AddTime(from, sourceLine, false);
        document.AddTime(to, sourceLine, false);
        return true;
    }

    private static bool ReadDeclaration(TimingDocument document, Match match, int sourceLine, int offset)
    {
        string kind = match.Groups["kind"].Value.ToLowerInvariant(), name = match.Groups["name"].Value, alias = match.Groups["alias"].Value;
        if (kind == "analog" && alias.Length == 0) return false;
        string labelGroup = "name";
        if (alias.StartsWith('"') && !name.StartsWith('"')) { (alias, name) = (name, alias); labelGroup = "alias"; }
        var track = new Track(kind, alias.Length == 0 ? Unquote(name) : Unquote(alias), Unquote(name), sourceLine)
        {
            Stereotype = string.Join(">><<", match.Groups["stereotype"].Captures.Select(capture => capture.Value)),
            Compact = document.Compact || match.Groups["compact"].Success
        };
        if (name.StartsWith('"')) track.CaptionToken = Token(match.Groups[labelGroup], offset, true);
        if (!SvgText.IsSupported(track.Label) || document.Tracks.Count >= 128 || document.Tracks.ContainsKey(track.Id) ||
            !TryPaint(match.Groups["color"].Value, out track.Paint)) return false;
        if (kind == "clock")
        {
            if (!TimeNumber(match.Groups["period"].Value, out track.Period) || track.Period <= 0) return false;
            track.Pulse = track.Period / 2;
            if (track.Pulse == 0) return false;
            if (match.Groups["pulse"].Success && (!TimeNumber(match.Groups["pulse"].Value, out track.Pulse) || track.Pulse <= 0 || track.Pulse >= track.Period)) return false;
            if (match.Groups["offset"].Success && !TimeNumber(match.Groups["offset"].Value, out track.Offset)) return false;
        }
        else if (match.Groups["period"].Success) return false;
        if (match.Groups["low"].Success)
        {
            if (kind != "analog" || !Numeric(match.Groups["low"].Value, out track.Low) ||
                !Numeric(match.Groups["high"].Value, out track.High) || track.Low >= track.High) return false;
            track.HasBounds = true;
            int decimals = Math.Max(FractionDigits(match.Groups["low"].Value), FractionDigits(match.Groups["high"].Value));
            if (decimals > 20) return false;
            track.NumberFormat = decimals == 0 ? "0" : "0." + new string('0', decimals);
        }
        if (match.Groups["color"].Success && kind is not ("robust" or "concise" or "rectangle")) return false;
        document.Tracks.Add(track.Id, track);
        if (kind is "robust" or "concise" or "rectangle") document.Selected = track;
        return true;
    }

    private static bool ReadDefinition(TimingDocument document, Match match, int line, int offset)
    {
        if (!document.Tracks.TryGetValue(match.Groups["track"].Value, out var track) ||
            track.Kind is not ("robust" or "concise" or "rectangle")) return false;
        if (match.Groups["label"].Success)
        {
            string label = Unquote(match.Groups["label"].Value);
            if (label.Length == 0 || !SvgText.IsSupported(label)) return false;
            track.Definitions[match.Groups["alias"].Value] = new(label, line, Token(match.Groups["label"], offset, true));
        }
        else
        {
            foreach (var state in match.Groups["states"].Value.Split(','))
            {
                string code = state.Trim();
                track.Definitions[code] = new(code, line, null);
            }
        }
        return track.Definitions.Count <= 256;
    }

    private static bool ReadTransition(TimingDocument document, Match match, int line, int offset)
    {
        string subject = match.Groups["subject"].Value;
        TimeMark? time = document.Now;
        if (!document.Tracks.TryGetValue(subject, out var track))
        {
            track = document.Selected;
            if (track == null || !ReadTime(document, subject, document.Now, out var tick)) return false;
            time = tick;
            document.AddTime(tick, line);
        }
        if (track.Kind == "clock" || track.Changes.Count >= 4096 ||
            !TryPaint(match.Groups["color"].Value, out var paint)) return false;
        string token = match.Groups["state"].Value, state = Unquote(token), annotation = match.Groups["annotation"].Value;
        if (state.Length == 0 || !SvgText.IsSupported(state) || !SvgText.IsSupported(annotation)) return false;
        string[] states = token.StartsWith('{') && token.Contains(',') ? token[1..^1].Split(',').Select(value => value.Trim()).ToArray() : [state];
        TextToken? field = token.StartsWith('{') ? null : Token(match.Groups["state"], offset, true, "timing-state");
        if (field != null && track.Kind == "binary")
            field = new(offset + match.Groups["state"].Index, token.Length, "timing-binary");
        if (field != null && track.Kind == "analog")
        {
            int quotes = token.StartsWith('"') ? 1 : 0;
            field = new(offset + match.Groups["state"].Index + quotes, token.Length - quotes * 2, "number");
        }
        for (int i = 0; i < states.Length; i++)
        {
            if (track.Definitions.TryGetValue(states[i], out var definition))
            {
                states[i] = definition.Label;
                field = null;
            }
        }
        double value = 0;
        if (track.Kind == "analog")
        {
            if (states.Length != 1 || !Numeric(state, out value)) return false;
            track.FirstAnalogValue ??= value;
        }
        if (track.Kind == "binary")
        {
            value = BinaryValue(states[0]);
            // PlantUML treats down and other low-state names as zero.
            if (states.Length == 2 && BinaryValue(states[1]) == value) return false;
        }
        if (track.Ribbon && states.Length != 1) return false;
        var change = new Change(time?.Value ?? 0, states, value, annotation, line, field, Token(match.Groups["annotation"], offset), paint);
        if (time == null) track.Initial = change;
        else
        {
            if (track.Changes.Count > 0 && track.Changes[^1].Time == time.Value.Value) return false;
            track.Changes.Add(change);
            document.AddTime(time.Value, line, false);
        }
        if (track.Kind == "robust")
        {
            foreach (string label in states)
                if (label != "{hidden}") track.StateSources.TryAdd(label, new(label, line, field));
            if (track.StateSources.Count > 256) return false;
        }
        return true;
    }

    private static bool ReadConstraint(TimingDocument document, Match match, int line, int offset)
    {
        var track = document.Selected;
        if (match.Groups["track"].Success && !document.Tracks.TryGetValue(match.Groups["track"].Value, out track)) return false;
        if (track == null || track.Constraints.Count >= 128 || !ReadTime(document, match.Groups["start"].Value, document.Now, out var start) ||
            !ReadTime(document, match.Groups["end"].Value, start, out var end) || start.Value == end.Value ||
            !SvgText.IsSupported(match.Groups["label"].Value) || !TryArrowPaint(match.Groups["arrow"].Value, out var paint)) return false;
        track.Constraints.Add(new(start.Value, end.Value, match.Groups["label"].Value, line, Token(match.Groups["label"], offset), paint));
        document.AddTime(start, line, false);
        document.AddTime(end, line, false);
        return true;
    }

    private static bool TryPaint(string text, out Paint paint)
    {
        paint = default;
        if (text.Length == 0) return true;
        string[] parts = text.Split(';');
        string? background = PaintColor(parts[0]), stroke = null;
        if (background == null) return false;
        foreach (string part in parts.Skip(1))
        {
            int separator = part.IndexOf(':');
            string? color = separator < 0 ? null : PaintColor(part[(separator + 1)..]);
            if (color == null) return false;
            if (part[..separator].Equals("line", StringComparison.OrdinalIgnoreCase)) stroke = color;
            else if (part[..separator].Equals("back", StringComparison.OrdinalIgnoreCase)) background = color;
            else return false;
        }
        paint = new(background, stroke);
        return true;
    }

    private static bool TryArrowPaint(string arrow, out Paint paint)
    {
        paint = default;
        int start = arrow.IndexOf('[');
        if (start < 0) return true;
        int end = arrow.IndexOf(']', start);
        if (end < 0) return false;
        foreach (string item in arrow[(start + 1)..end].Split(','))
        {
            string token = item.Trim();
            switch (token.ToLowerInvariant())
            {
                case "dashed": paint = paint with { Dash = "6 4" }; break;
                case "dotted": paint = paint with { Dash = "2 3" }; break;
                case "bold": paint = paint with { Thickness = 3 }; break;
                case "plain": paint = paint with { Dash = "", Thickness = 1 }; break;
                default:
                    if (token.StartsWith("thickness=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!int.TryParse(token[10..], out int thickness) || thickness is < 1 or > 32) return false;
                        paint = paint with { Thickness = thickness };
                        break;
                    }
                    string? color = token.StartsWith('#') ? PaintColor(token) : null;
                    if (color == null || paint.Line != null) return false;
                    paint = paint with { Line = color };
                    break;
            }
        }
        return true;
    }

    private static string? PaintColor(string text) => ManagedSkin.Color(text.Replace("grey", "gray", StringComparison.OrdinalIgnoreCase));

    private static Legend? ParseLegend(string line, int index)
    {
        var tokens = line.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int i = 1;
        bool above = i < tokens.Length && tokens[i] == "top";
        if (above || (i < tokens.Length && tokens[i] == "bottom")) i++;
        string align = i < tokens.Length ? tokens[i++] : "center";
        return i == tokens.Length && align is ("left" or "center" or "right") ? new(above, align, index) : null;
    }

    private static double BinaryValue(string state) => state.Equals("high", StringComparison.OrdinalIgnoreCase) || state == "1" ? 1 : 0;
    private static int FractionDigits(string value) => value.IndexOf('.') is >= 0 and var index ? value.Length - index - 1 : 0;
    private static bool Numeric(string text, out double value) => double.TryParse(text,
        NumberStyles.Float, CultureInfo.InvariantCulture, out value) && Math.Abs(value) <= 1e12;
    private static string Unquote(string token) => token.StartsWith('"')
        ? token[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal) : token;
    private static TextToken Token(Group group, int offset, bool quoted = false, string unquotedKind = "text") =>
        new(offset + group.Index, group.Length, quoted && group.Value.StartsWith('"') ? "quoted" : unquotedKind);
}

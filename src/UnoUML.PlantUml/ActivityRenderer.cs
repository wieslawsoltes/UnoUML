using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UnoUML.PlantUml;

/// <summary>Structured activity flow. Child extents are measured bottom-up, then connectors are emitted top-down.</summary>
internal static partial class ActivityRenderer
{
    private enum Kind { Start, Stop, End, Action, Decision, Switch, Fork, While, Repeat, Partition }
    private sealed class Lane(string label = "")
    {
        internal string Label = label;
        internal List<Step> Steps = [];
        internal int Width, Height;
        internal StyleLabel Caption = null!;
        internal bool Continues => Steps.Count == 0 || Steps[^1].Continues;
    }
    private sealed class Step(Kind kind, int line, string label = "")
    {
        internal Kind Kind = kind;
        internal int Line = line, CloseLine = -1, Width, Height, ContentWidth, Swimlane = -1, HeaderHeight, HeaderWidth, Lead = BranchLead;
        internal StyleLabel Caption = null!, ExitCaption = null!;
        internal string Label = label, Exit = "";
        internal string Stereotype = "", Shape = "";
        internal string? Fill;
        internal List<Lane> Lanes = [];
        internal bool Continues = kind is not (Kind.Stop or Kind.End);
        internal bool JoinAsDecision;
        internal bool Debug;
        internal int LabelStart = -1, LabelLength;
    }

    [GeneratedRegex(@"^(if|while)\s+\((.*?)\)\s+(then|is)(?:\s+\((.*)\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Condition();
    [GeneratedRegex(@"^repeat while\s+\((.*?)\)(?:\s+is\s+\((.*?)\))?(?:\s+not\s+\((.*?)\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatTest();
    [GeneratedRegex(@"^(?:(?<color>#[A-Za-z0-9]+)\s*)?:(?<label>.*);\s*(?<tags>(?:<<[^<>]+>>\s*)*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ActionSyntax();
    [GeneratedRegex(@"<<([^<>]+)>>", RegexOptions.CultureInvariant)]
    private static partial Regex ActionTags();

    internal static string? TryRender(string source, IReadOnlyDictionary<string, string>? options = null)
    {
        bool debug = options != null && options.TryGetValue("debug", out string? debugValue) &&
            string.Equals(debugValue, "true", StringComparison.OrdinalIgnoreCase);
        var root = new Lane();
        Lane? current = root;
        var frames = new Stack<(Step Node, Lane Parent)>();
        bool started = false, ended = false;
        int line = -1, count = 0, titleLine = -1, offset = 0;
        string title = "";
        var swimlanes = new List<(string Name, int Line)>();
        var swimIds = new Dictionary<string, int>(StringComparer.Ordinal);
        int currentSwim = -1, legendLine = -1;
        var legend = new List<string>();
        string legendAlign = "center";
        bool inLegend = false;
        StringBuilder? pendingAction = null;
        int actionLine = -1, actionStart = -1;
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            line++;
            int lineStart = offset;
            offset += raw.Length;
            if (offset < source.Length && source[offset] == '\r') offset++;
            if (offset < source.Length && source[offset] == '\n') offset++;
            var span = raw.Trim();
            if (pendingAction != null)
            {
                if (span.StartsWith('@') || pendingAction.Length + raw.Length > 16384 || current == null) return null;
                pendingAction.Append('\n').Append(span);
                if (!span.Contains(';')) continue;
                int end = lineStart + raw.TrimEnd().Length;
                var action = ParseAction(source[actionStart..end], actionLine, currentSwim, debug, actionStart);
                if (action == null) return null;
                current.Steps.Add(action);
                pendingAction = null;
                continue;
            }
            if (inLegend)
            {
                if (span.SequenceEqual("endlegend") || span.SequenceEqual("end legend")) { inLegend = false; continue; }
                if (legend.Count >= 100 || span.Length > 2048) return null;
                var caption = span.ToString();
                if (!Plain(caption) || (caption.Length != 0 && caption[0] is '@' or '|' or '#')) return null;
                legend.Add(caption);
                continue;
            }
            if (span.IsEmpty || span[0] == '\'') continue;
            if (!started) { if (!span.SequenceEqual("@startuml")) return null; started = true; continue; }
            if (ended) return null;
            if (span.SequenceEqual("@enduml")) { ended = true; continue; }
            if (++count > 4096 || span.Length > 2048 || frames.Count > 48) return null;
            string s = span.ToString();
            if (s is "legend" or "legend right" or "legend left" or "legend center")
            {
                if (frames.Count != 0 || legendLine >= 0) return null;
                legendLine = line; legendAlign = s.Length > 7 ? s[7..] : "center"; inLegend = true;
                continue;
            }
            if (s[0] == '|')
            {
                // A transition between branches in different lanes requires a graph
                // layout. Keep such diagrams with the reference renderer.
                if (frames.Count != 0 || (swimlanes.Count == 0 && root.Steps.Count != 0) || s.Length < 3 || s[^1] != '|') return null;
                string name = s[1..^1].Trim();
                if (name.Length == 0 || name.Contains('|') || name[0] == '#' || !Plain(name)) return null;
                if (!swimIds.TryGetValue(name, out currentSwim))
                {
                    if (swimlanes.Count >= 128) return null;
                    currentSwim = swimlanes.Count; swimIds.Add(name, currentSwim); swimlanes.Add((name, line));
                }
                continue;
            }
            if (s.StartsWith("title ", StringComparison.Ordinal))
            {
                if (title.Length != 0 || root.Steps.Count != 0 || !Plain(title = s[6..].Trim())) return null;
                titleLine = line; continue;
            }
            var node = frames.Count != 0 ? frames.Peek().Node : null;
            if (s == "}")
            {
                if (node?.Kind != Kind.Partition || current == null || current.Steps.Count == 0) return null;
                node.CloseLine = line; current = frames.Pop().Parent; continue;
            }
            if (s == "else" || s.StartsWith("else ", StringComparison.Ordinal))
            {
                if (node?.Kind != Kind.Decision || node.Lanes.Count != 1 || !OptionalLabel(s, "else", out string label)) return null;
                node.Lanes.Add(current = new(label)); continue;
            }
            if (s.StartsWith("case ", StringComparison.Ordinal))
            {
                if (node?.Kind != Kind.Switch || !OptionalLabel(s, "case", out string label)) return null;
                node.Lanes.Add(current = new(label)); continue;
            }
            if (s == "fork again")
            {
                if (node?.Kind != Kind.Fork) return null;
                node.Lanes.Add(current = new()); continue;
            }
            if (s is "endif" or "endswitch" or "end fork" or "end merge" || s == "endwhile" || s.StartsWith("endwhile ", StringComparison.Ordinal) || s.StartsWith("repeat while ", StringComparison.Ordinal))
            {
                if (node == null) return null;
                switch (node.Kind)
                {
                    case Kind.Decision when s == "endif":
                        if (node.Lanes.Count == 1) node.Lanes.Add(new());
                        break;
                    case Kind.Switch when s == "endswitch" && node.Lanes.Count != 0: break;
                    case Kind.Fork when (s is "end fork" or "end merge") && node.Lanes.Count >= 2:
                        node.JoinAsDecision = s == "end merge"; break;
                    case Kind.While when OptionalLabel(s, "endwhile", out string label): node.Exit = label; break;
                    case Kind.Repeat when RepeatTest().Match(s) is { Success: true } m:
                        node.Label = m.Groups[1].Value; node.Lanes[0].Label = m.Groups[2].Value; node.Exit = m.Groups[3].Value;
                        if (!Plain(node.Label) || !Plain(node.Lanes[0].Label) || !Plain(node.Exit)) return null;
                        break;
                    default: return null;
                }
                node.CloseLine = line; current = frames.Pop().Parent; continue;
            }

            if (current == null) return null;
            if (s is "start" or "stop" or "end")
            {
                if (s == "start" && (frames.Count != 0 || current.Steps.Count != 0)) return null;
                current.Steps.Add(new(s switch { "start" => Kind.Start, "stop" => Kind.Stop, _ => Kind.End }, line) { Swimlane = currentSwim }); continue;
            }
            if (s.StartsWith(':') || s[0] == '#' && s.Contains(':'))
            {
                int start = lineStart + raw.Length - raw.TrimStart().Length;
                if (!s.Contains(';')) { pendingAction = new(s); actionLine = line; actionStart = start; continue; }
                var action = ParseAction(s, line, currentSwim, debug, start);
                if (action == null) return null;
                current.Steps.Add(action); continue;
            }

            Step next;
            if (s.StartsWith("partition ", StringComparison.Ordinal) && s.EndsWith('{'))
            {
                var name = s[10..^1].Trim();
                if (name.Length > 1 && name[0] == '"' && name[^1] == '"') name = name[1..^1];
                else if (name.Any(char.IsWhiteSpace)) return null;
                if (name.Length == 0 || !Plain(name) || name.IndexOfAny(['"', '#', '{', '}']) >= 0) return null;
                next = new(Kind.Partition, line, name);
            }
            else if (s == "fork" || s == "repeat") next = new(s == "fork" ? Kind.Fork : Kind.Repeat, line);
            else if (s.StartsWith("switch ", StringComparison.Ordinal) && OptionalLabel(s, "switch", out string subject)) next = new(Kind.Switch, line, subject);
            else
            {
                var match = Condition().Match(s);
                if (!match.Success) return null;
                bool decision = match.Groups[1].Value == "if";
                if (match.Groups[3].Value != (decision ? "then" : "is")) return null;
                string condition = match.Groups[2].Value, label = match.Groups[4].Value;
                if (!Plain(condition) || !Plain(label)) return null;
                next = new(decision ? Kind.Decision : Kind.While, line, condition);
                next.Lanes.Add(new(label));
            }
            next.Swimlane = currentSwim; current.Steps.Add(next); frames.Push((next, current));
            if (next.Kind == Kind.Switch) current = null;
            else
            {
                if (next.Lanes.Count == 0) next.Lanes.Add(new());
                current = next.Lanes[0];
            }
        }
        if (!ended || inLegend || pendingAction != null || (legendLine >= 0 && legend.Count == 0) || frames.Count != 0 || root.Steps.Count == 0 || !Measure(root)) return null;
        return DrawDocument(root, title, titleLine, swimlanes, legend, legendLine, legendAlign);
    }

    private static string? DrawDocument(Lane root, string title, int titleLine, List<(string Name, int Line)> swimlanes,
        List<string> legend, int legendLine, string legendAlign)
    {
        // Upstream elides the header and border when the document uses a single lane.
        int[]? columns = null;
        int bodyWidth = root.Width;
        var heading = StyleTitle.Measure(title, StyleDocument.Current, 18);
        var laneHeaders = swimlanes.Select(lane => StyleLabel.Measure(lane.Name, "swimlane", padding: "6 12")).ToArray();
        int headerHeight = laneHeaders.Select(label => label.Height).DefaultIfEmpty(30).Max();
        if (swimlanes.Count > 1)
        {
            columns = laneHeaders.Select(label => Math.Max(70, label.Width)).ToArray();
            foreach (var step in root.Steps) columns[step.Swimlane] = Math.Max(columns[step.Swimlane], step.Width + 34);
            bodyWidth = columns.Sum();
        }
        var legendLabel = legend.Count == 0 ? null : StyleLabel.Measure(string.Join("\n", legend), "legend", padding: "8 12");
        int legendWidth = legendLabel?.Width ?? 0;
        int legendHeight = legendLabel?.Height ?? 0;
        int width = Math.Max(Math.Max(bodyWidth, legendWidth), heading?.Width ?? 0) + 48;
        int y = heading == null ? 22 : 22 + heading.Height + 12;
        int bodyTop = y + (columns == null ? 0 : headerHeight + 22);
        int bottom = bodyTop + root.Height + (columns == null ? 0 : 14);
        int height = bottom + 22 + (legendHeight == 0 ? 0 : legendHeight + 16);
        if (width > 32768 || height > 32768) return null;
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");
        heading?.Append(svg, StyleDocument.Current, width / 2, 12, titleLine);
        if (columns != null)
        {
            int left = (width - bodyWidth) / 2;
            for (int i = 0; i < columns.Length; i++)
            {
                int w = columns[i];
                Open(svg, "Swimlane", swimlanes[i].Line);
                svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{left}\" y=\"{y}\" width=\"{w}\" height=\"{headerHeight}\" fill=\"#f1f1f1\" stroke=\"#494949\"/>");
                laneHeaders[i].Append(svg, left, y + (headerHeight - laneHeaders[i].Height) / 2, w, "center");
                svg.Append("</g>");
                Path(svg, $"M{left},{y + headerHeight}V{bottom}");
                if (i == columns.Length - 1) Path(svg, $"M{left + w},{y + headerHeight}V{bottom}");
                columns[i] = left + w / 2; left += w;
            }
        }
        DrawLane(svg, root, width / 2, bodyTop, columns);
        if (legendHeight > 0)
        {
            int x = legendAlign switch { "left" => 24, "right" => width - 24 - legendWidth, _ => (width - legendWidth) / 2 };
            int top = bottom + 16;
            Open(svg, "Legend", legendLine);
            svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{top}\" width=\"{legendWidth}\" height=\"{legendHeight}\" rx=\"3\" fill=\"#fafafa\" stroke=\"#777\"/>");
            legendLabel!.Append(svg, x, top, legendWidth);
            svg.Append("</g>");
        }
        return svg.Append("</svg>").ToString();
    }

    private static bool Plain(string s) => SvgText.IsSupported(s);

    private static Step? ParseAction(string source, int line, int swimlane, bool debug, int start)
    {
        var match = ActionSyntax().Match(source);
        if (!match.Success) return null;
        var token = match.Groups["label"];
        string literal = token.Value.Trim();
        string label = string.Join("\n", literal.ReplaceLineEndings("\n").Split('\n').Select(row => row.Trim()));
        if (label.Length == 0 || !Plain(label)) return null;
        string? color = match.Groups["color"].Success ? ManagedSkin.Color(match.Groups["color"].Value) : null;
        if (match.Groups["color"].Success && color == null) return null;
        var tags = ActionTags().Matches(match.Groups["tags"].Value).Select(tag => tag.Groups[1].Value.Trim()).ToArray();
        string shape = tags.FirstOrDefault(tag => tag.ToLowerInvariant() is "input" or "output" or "procedure" or "load" or "save" or "continuous" or "task" or "object" or "objectsignal" or "trigger" or "sendsignal" or "acceptevent")?.ToLowerInvariant() ?? "";
        return new(Kind.Action, line, label)
        {
            Swimlane = swimlane, Debug = debug, Fill = color, Shape = shape, Stereotype = string.Join(">><<", tags),
            LabelStart = start + token.Index + token.Length - token.Value.TrimStart().Length, LabelLength = literal.Length
        };
    }

    private static bool OptionalLabel(string source, string keyword, out string label)
    {
        label = "";
        if (source == keyword) return true;
        if (!source.StartsWith(keyword + " (", StringComparison.Ordinal) || !source.EndsWith(')')) return false;
        label = source[(keyword.Length + 2)..^1];
        return Plain(label);
    }

    private const int Gap = 27, LaneGap = 24, BranchLead = 32, MergeLead = 24, DiamondHeight = 40;
    private static bool Measure(Lane lane)
    {
        lane.Caption = StyleLabel.Measure(lane.Label, "arrow", 12);
        lane.Width = Math.Max(40, lane.Caption.Width + 18);
        bool reachable = true;
        foreach (var step in lane.Steps)
        {
            if (!reachable) return false;
            string category = Category(step.Kind.ToString());
            step.Caption = StyleLabel.Measure(step.Label, category, padding: step.Kind == Kind.Action ? "6 16" : "0", stereotype: step.Stereotype);
            step.ExitCaption = StyleLabel.Measure(step.Exit, "arrow", 12);
            step.HeaderHeight = Math.Max(DiamondHeight, step.Caption.Height + 12);
            step.HeaderWidth = Math.Max(44, step.Caption.Width + step.HeaderHeight + 8);
            if (step.Kind == Kind.Action)
            {
                step.Width = Math.Max(58, step.Caption.Width); step.Height = Math.Max(36, step.Caption.Height);
            }
            else if (step.Lanes.Count == 0)
            {
                step.Width = step.Height = 22;
            }
            else
            {
                int contentWidth = -LaneGap, bodyHeight = 0;
                foreach (var child in step.Lanes)
                {
                    if (!Measure(child)) return false;
                    contentWidth += child.Width + LaneGap;
                    bodyHeight = Math.Max(bodyHeight, child.Height);
                }
                int diamond = step.HeaderWidth;
                step.Lead = Math.Max(BranchLead, step.Lanes.Max(child => child.Caption.Height) + 14);
                step.ContentWidth = contentWidth;
                if (step.Kind == Kind.Partition)
                {
                    step.HeaderHeight = Math.Max(42, step.Caption.Height + 24);
                    step.Width = Math.Max(contentWidth + 32, 2 * (step.Caption.Width + 26) + 12);
                    step.Height = step.HeaderHeight + bodyHeight + 18;
                    step.Continues = step.Lanes[0].Continues;
                }
                else if (step.Kind is Kind.While or Kind.Repeat)
                {
                    if (!step.Lanes[0].Continues) return false;
                    step.Width = Math.Max(diamond, contentWidth) + 2 * (32 + Math.Max(step.ExitCaption.Width, step.Lanes[0].Caption.Width));
                    step.Height = step.HeaderHeight + step.Lead + bodyHeight + MergeLead + Math.Max(30, step.ExitCaption.Height + 12);
                }
                else
                {
                    step.Continues = step.Lanes.Any(l => l.Continues);
                    step.Width = Math.Max(diamond, contentWidth);
                    step.Height = (step.Kind == Kind.Fork ? 7 : step.HeaderHeight) + step.Lead + bodyHeight + (step.Continues ? MergeLead + 16 : 0);
                }
            }
            if (lane.Height != 0) lane.Height += Gap;
            lane.Height += step.Height; lane.Width = Math.Max(lane.Width, step.Width);
            if (lane.Width > 32720 || lane.Height > 32720) return false;
            reachable = step.Continues;
        }
        return true;
    }

    private static void DrawLane(StringBuilder svg, Lane lane, int x, int top, int[]? columns = null)
    {
        int y = top, previousX = x;
        foreach (var step in lane.Steps)
        {
            if (columns != null) x = columns[step.Swimlane];
            if (y != top)
            {
                if (x == previousX) Arrow(svg, x, y, x, y + Gap);
                else { Path(svg, $"M{previousX},{y}V{y + Gap / 2}H{x}V{y + Gap}"); Head(svg, x, y + Gap); }
                y += Gap;
            }
            previousX = x;
            if (step.Lanes.Count == 0) DrawSimple(svg, step, x, y);
            else if (step.Kind == Kind.Partition) DrawPartition(svg, step, x, y);
            else if (step.Kind is Kind.While or Kind.Repeat) DrawLoop(svg, step, x, y);
            else DrawBranches(svg, step, x, y);
            y += step.Height;
        }
    }

    private static void DrawSimple(StringBuilder svg, Step step, int x, int y)
    {
        Open(svg, step.Kind.ToString(), step.Line, step.Stereotype, step.Fill != null, step.LabelStart, step.LabelLength);
        switch (step.Kind)
        {
            case Kind.Action:
                if (step.Debug)
                    svg.Append(CultureInfo.InvariantCulture, $"<rect class=\"activity-debug\" x=\"{x - step.Width / 2}\" y=\"{y}\" width=\"{step.Width}\" height=\"{step.Height}\" fill=\"none\" stroke=\"#0000FF\" stroke-width=\"2\"/>");
                ActionShape(svg, step, x - step.Width / 2, y);
                step.Caption.Append(svg, x - step.Width / 2, y + (step.Height - step.Caption.Height) / 2, step.Width, "center"); break;
            case Kind.Start:
                svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{x}\" cy=\"{y + 11}\" r=\"10\" fill=\"#222\"/>"); break;
            case Kind.Stop:
                svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{x}\" cy=\"{y + 11}\" r=\"10\" fill=\"white\" stroke=\"#222\"/><circle cx=\"{x}\" cy=\"{y + 11}\" r=\"7\" fill=\"#222\"/>"); break;
            case Kind.End:
                svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{x}\" cy=\"{y + 11}\" r=\"10\" fill=\"white\" stroke=\"#222\"/><path d=\"M{x - 6},{y + 5}l12,12m0,-12l-12,12\" stroke=\"#222\"/>"); break;
        }
        svg.Append("</g>");
    }

    private static void ActionShape(StringBuilder svg, Step step, int x, int y)
    {
        int right = x + step.Width, bottom = y + step.Height, middle = y + step.Height / 2;
        string color = step.Fill ?? "#f1f1f1";
        string path = step.Shape switch
        {
            "input" or "trigger" => $"M{x},{y}H{right}L{right - 10},{middle}L{right},{bottom}H{x}Z",
            "output" or "sendsignal" => $"M{x},{y}H{right - 10}L{right},{middle}L{right - 10},{bottom}H{x}Z",
            "objectsignal" => $"M{x},{y}H{right - 10}L{right},{middle}L{right - 10},{bottom}H{x}L{x + 10},{middle}Z",
            "acceptevent" => $"M{x},{y}H{right}V{bottom}H{x}L{x + 10},{middle}Z",
            "load" => $"M{x},{y}H{right - 10}L{right},{bottom}H{x + 10}Z",
            "save" => $"M{x + 10},{y}H{right}L{right - 10},{bottom}H{x}Z",
            "continuous" => $"M{x + 10},{y}H{right - 10}L{right},{middle}L{right - 10},{bottom}H{x + 10}L{x},{middle}Z",
            _ => ""
        };
        if (path.Length > 0) svg.Append(CultureInfo.InvariantCulture, $"<path data-activity-shape=\"{step.Shape}\" d=\"{path}\" fill=\"{color}\" stroke=\"#242424\"/>");
        else
        {
            svg.Append(CultureInfo.InvariantCulture, $"<rect data-activity-shape=\"{step.Shape}\" x=\"{x}\" y=\"{y}\" width=\"{step.Width}\" height=\"{step.Height}\" rx=\"{(step.Shape.Length == 0 ? 11 : 0)}\" fill=\"{color}\" stroke=\"#242424\"/>");
            if (step.Shape == "procedure") svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{x + 8},{y}V{bottom}M{right - 8},{y}V{bottom}\" stroke=\"#242424\"/>");
        }
    }

    private static void DrawPartition(StringBuilder svg, Step step, int x, int top)
    {
        int left = x - step.Width / 2, tabWidth = step.Caption.Width + 24, tabHeight = step.HeaderHeight - 18;
        Open(svg, "Partition", step.Line);
        svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{left}\" y=\"{top}\" width=\"{step.Width}\" height=\"{step.Height}\" rx=\"4\" fill=\"#fff\" stroke=\"#555\"/>");
        Path(svg, $"M{left + tabWidth},{top}v{tabHeight - 10}l-10,10H{left}");
        step.Caption.Append(svg, left + 8, top + (tabHeight - step.Caption.Height) / 2, tabWidth - 18, "center");
        svg.Append("</g>");
        Arrow(svg, x, top, x, top + step.HeaderHeight);
        var body = step.Lanes[0];
        DrawLane(svg, body, x, top + step.HeaderHeight);
        if (body.Continues) Path(svg, $"M{x},{top + step.HeaderHeight + body.Height}V{top + step.Height}");
    }

    private static void DrawBranches(StringBuilder svg, Step step, int x, int y)
    {
        bool fork = step.Kind == Kind.Fork;
        int h = fork ? 7 : step.HeaderHeight, splitY = y + h + 9, bodyTop = y + h + step.Lead;
        int bottom = y + step.Height, joinY = bottom - 16;
        int left = x - step.ContentWidth / 2;
        Open(svg, step.Kind.ToString(), step.Line);
        if (fork) Bar(svg, left, y, step.ContentWidth, 7);
        else Diamond(svg, x, y, step.Label, step.HeaderHeight, step.Caption, step.HeaderWidth);
        svg.Append("</g>");
        foreach (var child in step.Lanes)
        {
            int bx = left + child.Width / 2;
            left += child.Width + LaneGap;
            if (fork) Arrow(svg, bx, y + h, bx, bodyTop);
            else { Path(svg, $"M{x},{y + h}V{splitY}H{bx}V{bodyTop}"); Head(svg, bx, bodyTop); }
            if (child.Label.Length > 0) Caption(svg, bx, bodyTop - 6 - child.Caption.Height, child.Caption, halo: true);
            DrawLane(svg, child, bx, bodyTop);
            if (child.Continues)
            {
                int exit = bodyTop + child.Height;
                if (fork && !step.JoinAsDecision) { Path(svg, $"M{bx},{exit}V{joinY + 5}"); Head(svg, bx, joinY + 5); }
                else Path(svg, $"M{bx},{exit}V{joinY - 9}H{x}V{joinY}");
            }
        }
        if (!step.Continues) return;
        Open(svg, "Join", step.CloseLine);
        if (fork && !step.JoinAsDecision)
        {
            Bar(svg, x - step.ContentWidth / 2, joinY + 5, step.ContentWidth, 6);
            Path(svg, $"M{x},{joinY + 11}V{bottom}");
        }
        else Diamond(svg, x, joinY, "", 16);
        svg.Append("</g>");
    }

    private static void DrawLoop(StringBuilder svg, Step step, int x, int y)
    {
        var lane = step.Lanes[0]; bool repeat = step.Kind == Kind.Repeat;
        int topH = repeat ? 16 : step.HeaderHeight;
        int bodyY = y + topH + step.Lead, bottom = y + step.Height;
        int testY = repeat ? bottom - step.HeaderHeight - Math.Max(18, step.ExitCaption.Height + 4) : y;
        int trunk = step.Width / 2 - 6;
        Open(svg, step.Kind.ToString(), step.Line); Diamond(svg, x, y, repeat ? "" : step.Label, topH, repeat ? null : step.Caption, repeat ? 28 : step.HeaderWidth); svg.Append("</g>");
        Arrow(svg, x, y + topH, x, bodyY);
        if (!repeat && lane.Label.Length > 0) Caption(svg, x, bodyY - 9 - lane.Caption.Height, lane.Caption, halo: true);
        DrawLane(svg, lane, x, bodyY);
        int exit = bodyY + lane.Height;
        if (repeat)
        {
            Arrow(svg, x, exit, x, testY);
            Open(svg, "LoopTest", step.CloseLine); Diamond(svg, x, testY, step.Label, step.HeaderHeight, step.Caption, step.HeaderWidth); svg.Append("</g>");
            int edge = step.HeaderWidth / 2;
            Path(svg, $"M{x - edge},{testY + step.HeaderHeight / 2}H{x - trunk}V{y + 8}H{x - 14}");
            LeftToRightHead(svg, x - 14, y + 8);
            Path(svg, $"M{x},{testY + step.HeaderHeight}V{bottom}");
            Caption(svg, x - trunk / 2, testY + step.HeaderHeight / 2 - 4 - lane.Caption.Height, lane.Caption, halo: true);
            Caption(svg, x, bottom - 3 - step.ExitCaption.Height, step.ExitCaption, halo: true);
        }
        else
        {
            int edge = step.HeaderWidth / 2;
            Path(svg, $"M{x},{exit}V{bottom - 18}H{x - trunk}V{y + step.HeaderHeight / 2}H{x - edge}");
            LeftToRightHead(svg, x - edge, y + step.HeaderHeight / 2);
            Path(svg, $"M{x + edge},{y + step.HeaderHeight / 2}H{x + trunk}V{bottom}H{x}");
            Caption(svg, x + (edge + trunk) / 2, y + step.HeaderHeight / 2 - 6 - step.ExitCaption.Height, step.ExitCaption, halo: true);
        }
    }

    private static void Diamond(StringBuilder svg, int x, int y, string label, int height, StyleLabel? caption = null, int width = 0)
    {
        int half = height == 16 ? 14 : Math.Max(width, Math.Max(44, (caption?.Width ?? SvgText.Measure(label)) + height + 8)) / 2;
        int corner = height == 16 ? half : height / 2;
        svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{x - half + corner},{y}H{x + half - corner}l{corner},{height / 2}l-{corner},{height / 2}H{x - half + corner}l-{corner},-{height / 2}z\" fill=\"#f1f1f1\" stroke=\"#242424\"/>");
        if (label.Length > 0) (caption ?? StyleLabel.Measure(label, "diamond")).Append(svg, x - half, y + (height - (caption?.Height ?? 14)) / 2, half * 2, "center");
    }
    private static void Bar(StringBuilder svg, int x, int y, int w, int h) => svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" rx=\"3\" fill=\"#333\"/>");
    private static void Path(StringBuilder svg, string d) => svg.Append(CultureInfo.InvariantCulture, $"<path d=\"{d}\" fill=\"none\" stroke=\"#333\"/>");
    private static void Head(StringBuilder svg, int x, int y) => svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{x - 4},{y - 7}L{x},{y}l4,-7z\" fill=\"#333\"/>");
    private static void LeftToRightHead(StringBuilder svg, int x, int y) => svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{x - 7},{y - 4}L{x},{y}l-7,4z\" fill=\"#333\"/>");
    private static void Arrow(StringBuilder svg, int x1, int y1, int x2, int y2) { Path(svg, $"M{x1},{y1}L{x2},{y2}"); Head(svg, x2, y2); }
    private static string Category(string kind) => kind.ToLowerInvariant() switch
    {
        "action" => "activity",
        "decision" or "switch" or "while" or "repeat" or "looptest" or "join" => "diamond",
        "partition" => "composite",
        "fork" => "activitybar",
        "start" => "circle start",
        "stop" => "circle stop",
        "end" => "circle end",
        "swimlane" => "swimlane",
        "legend" => "legend",
        _ => "arrow"
    };
    private static void Open(StringBuilder svg, string kind, int line, string stereotype = "", bool explicitFill = false, int labelStart = -1, int labelLength = 0)
    {
        svg.Append(CultureInfo.InvariantCulture, $"<g class=\"activity-{kind}\" data-source-line=\"{line}\"");
        if (kind is "Start" or "Stop" or "End" or "Fork")
            svg.Append(" data-style-name=\"").Append(Category(kind)).Append("\" data-style-metrics=\"fontsize\"");
        else StyleLabel.Scope(svg, Category(kind), stereotype);
        if (explicitFill) svg.Append(" data-style-fill=\"explicit\"");
        if (labelStart >= 0 && labelLength > 0)
            svg.Append(CultureInfo.InvariantCulture, $" data-edit-start=\"{labelStart}\" data-edit-length=\"{labelLength}\" data-edit-kind=\"activity-label\" data-edit-role=\"label\"");
        svg.Append('>');
    }
    private static void Caption(StringBuilder svg, int x, int top, StyleLabel label, bool halo = false)
    {
        if (label.Rows.All(row => row.Length == 0)) return;
        svg.Append("<g"); StyleLabel.Scope(svg, "arrow"); svg.Append('>');
        if (halo) svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x - label.Width / 2 - 3}\" y=\"{top - 1}\" width=\"{label.Width + 6}\" height=\"{label.Height + 2}\" rx=\"2\" fill=\"white\"/>");
        label.Append(svg, x - label.Width / 2, top, label.Width, "center");
        svg.Append("</g>");
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace UnoUML.PlantUml;

/// <summary>Structured activity flow. Child extents are measured bottom-up, then connectors are emitted top-down.</summary>
internal static partial class ActivityRenderer
{
    private enum Kind { Start, Stop, End, Action, Decision, Switch, Fork, While, Repeat }
    private sealed class Lane(string label = "")
    {
        internal string Label = label;
        internal List<Step> Steps = [];
        internal int Width, Height;
        internal bool Continues => Steps.Count == 0 || Steps[^1].Continues;
    }
    private sealed class Step(Kind kind, int line, string label = "")
    {
        internal Kind Kind = kind;
        internal int Line = line, CloseLine = -1, Width, Height, ContentWidth;
        internal string Label = label, Exit = "";
        internal List<Lane> Lanes = [];
        internal bool Continues = kind is not (Kind.Stop or Kind.End);
        internal bool JoinAsDecision;
    }

    [GeneratedRegex(@"^(if|while)\s+\((.*?)\)\s+(then|is)(?:\s+\((.*)\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Condition();
    [GeneratedRegex(@"^repeat while\s+\((.*?)\)(?:\s+is\s+\((.*?)\))?(?:\s+not\s+\((.*?)\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatTest();

    internal static string? TryRender(string source)
    {
        var root = new Lane();
        Lane? current = root;
        var frames = new Stack<(Step Node, Lane Parent)>();
        bool started = false, ended = false;
        int line = -1, count = 0;
        string title = "";
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            line++;
            var span = raw.Trim();
            if (span.IsEmpty || span[0] == '\'') continue;
            if (!started) { if (!span.SequenceEqual("@startuml")) return null; started = true; continue; }
            if (ended) return null;
            if (span.SequenceEqual("@enduml")) { ended = true; continue; }
            if (++count > 4096 || span.Length > 2048 || frames.Count > 48) return null;
            string s = span.ToString();
            if (s.StartsWith("title ", StringComparison.Ordinal))
            {
                if (title.Length != 0 || root.Steps.Count != 0 || !Plain(title = s[6..].Trim())) return null;
                continue;
            }
            var node = frames.Count != 0 ? frames.Peek().Node : null;
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
                current.Steps.Add(new(s switch { "start" => Kind.Start, "stop" => Kind.Stop, _ => Kind.End }, line)); continue;
            }
            if (s.StartsWith(':') && s.EndsWith(';'))
            {
                string action = s[1..^1].Trim();
                if (action.Length == 0 || action.Contains(';') || !Plain(action)) return null;
                current.Steps.Add(new(Kind.Action, line, action)); continue;
            }

            Step next;
            if (s == "fork" || s == "repeat") next = new(s == "fork" ? Kind.Fork : Kind.Repeat, line);
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
            current.Steps.Add(next); frames.Push((next, current));
            if (next.Kind == Kind.Switch) current = null;
            else
            {
                if (next.Lanes.Count == 0) next.Lanes.Add(new());
                current = next.Lanes[0];
            }
        }
        if (!ended || frames.Count != 0 || root.Steps.Count == 0 || !Measure(root)) return null;
        int width = Math.Max(root.Width, SvgText.Measure(title, 18)) + 48;
        int y = title.Length == 0 ? 22 : 54, height = y + root.Height + 22;
        if (width > 32768 || height > 32768) return null;
        var svg = new StringBuilder();
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");
        if (title.Length > 0) Text(svg, width / 2, 30, title, 18, true);
        DrawLane(svg, root, width / 2, y);
        return svg.Append("</svg>").ToString();
    }

    private static bool Plain(string s) => SvgText.IsPlain(s) && !s.Contains('!') && !s.Contains("->", StringComparison.Ordinal);

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
        lane.Width = Math.Max(40, SvgText.Measure(lane.Label, 12) + 18);
        bool reachable = true;
        foreach (var step in lane.Steps)
        {
            if (!reachable) return false;
            if (step.Kind == Kind.Action)
            {
                step.Width = Math.Max(58, SvgText.Measure(step.Label) + 32); step.Height = 36;
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
                int diamond = Math.Max(44, SvgText.Measure(step.Label) + 48);
                step.ContentWidth = contentWidth;
                if (step.Kind is Kind.While or Kind.Repeat)
                {
                    if (!step.Lanes[0].Continues) return false;
                    step.Width = Math.Max(diamond,contentWidth) + 2 * (32 + Math.Max(SvgText.Measure(step.Exit, 12), SvgText.Measure(step.Lanes[0].Label, 12)));
                    step.Height = DiamondHeight + BranchLead + bodyHeight + MergeLead + 30;
                }
                else
                {
                    step.Continues = step.Lanes.Any(l => l.Continues);
                    step.Width = Math.Max(diamond, contentWidth);
                    step.Height = (step.Kind == Kind.Fork ? 7 : DiamondHeight) + BranchLead + bodyHeight + (step.Continues ? MergeLead + 16 : 0);
                }
            }
            if (lane.Height != 0) lane.Height += Gap;
            lane.Height += step.Height; lane.Width = Math.Max(lane.Width, step.Width);
            if (lane.Width > 32720 || lane.Height > 32720) return false;
            reachable = step.Continues;
        }
        return true;
    }

    private static void DrawLane(StringBuilder svg, Lane lane, int x, int top)
    {
        int y = top;
        foreach (var step in lane.Steps)
        {
            if (y != top) { Arrow(svg, x, y, x, y + Gap); y += Gap; }
            if (step.Lanes.Count == 0) DrawSimple(svg, step, x, y);
            else if (step.Kind is Kind.While or Kind.Repeat) DrawLoop(svg, step, x, y);
            else DrawBranches(svg, step, x, y);
            y += step.Height;
        }
    }

    private static void DrawSimple(StringBuilder svg, Step step, int x, int y)
    {
        Open(svg, step.Kind.ToString(), step.Line);
        switch (step.Kind)
        {
            case Kind.Action:
                svg.Append($"<rect x=\"{x - step.Width / 2}\" y=\"{y}\" width=\"{step.Width}\" height=\"36\" rx=\"11\" fill=\"#f1f1f1\" stroke=\"#242424\"/>");
                Text(svg,x,y+23,step.Label); break;
            case Kind.Start:
                svg.Append($"<circle cx=\"{x}\" cy=\"{y+11}\" r=\"10\" fill=\"#222\"/>"); break;
            case Kind.Stop:
                svg.Append($"<circle cx=\"{x}\" cy=\"{y+11}\" r=\"10\" fill=\"white\" stroke=\"#222\"/><circle cx=\"{x}\" cy=\"{y+11}\" r=\"7\" fill=\"#222\"/>"); break;
            case Kind.End:
                svg.Append($"<circle cx=\"{x}\" cy=\"{y+11}\" r=\"10\" fill=\"white\" stroke=\"#222\"/><path d=\"M{x-6},{y+5}l12,12m0,-12l-12,12\" stroke=\"#222\"/>"); break;
        }
        svg.Append("</g>");
    }

    private static void DrawBranches(StringBuilder svg, Step step, int x, int y)
    {
        bool fork = step.Kind == Kind.Fork;
        int h = fork ? 7 : DiamondHeight, splitY = y + h + 9, bodyTop = y + h + BranchLead;
        int bottom = y + step.Height, joinY = bottom - 16;
        int left = x - step.ContentWidth / 2;
        Open(svg, step.Kind.ToString(), step.Line);
        if (fork) Bar(svg, left, y, step.ContentWidth, 7);
        else Diamond(svg, x, y, step.Label, DiamondHeight);
        svg.Append("</g>");
        foreach (var child in step.Lanes)
        {
            int bx = left + child.Width / 2;
            left += child.Width + LaneGap;
            if (fork) Arrow(svg,bx,y+h,bx,bodyTop);
            else { Path(svg,$"M{x},{y+h}V{splitY}H{bx}V{bodyTop}"); Head(svg,bx,bodyTop); }
            if (child.Label.Length > 0) Text(svg,bx,bodyTop-6,child.Label,12,halo:true);
            DrawLane(svg,child,bx,bodyTop);
            if (child.Continues)
            {
                int exit = bodyTop + child.Height;
                if (fork && !step.JoinAsDecision) { Path(svg,$"M{bx},{exit}V{joinY+5}"); Head(svg,bx,joinY+5); }
                else Path(svg,$"M{bx},{exit}V{joinY-9}H{x}V{joinY}");
            }
        }
        if (!step.Continues) return;
        Open(svg, "Join", step.CloseLine);
        if (fork && !step.JoinAsDecision)
        {
            Bar(svg,x-step.ContentWidth/2,joinY+5,step.ContentWidth,6);
            Path(svg,$"M{x},{joinY+11}V{bottom}");
        }
        else Diamond(svg,x,joinY,"",16);
        svg.Append("</g>");
    }

    private static void DrawLoop(StringBuilder svg, Step step, int x, int y)
    {
        var lane = step.Lanes[0]; bool repeat = step.Kind == Kind.Repeat;
        int topH = repeat ? 16 : DiamondHeight;
        int bodyY = y + topH + BranchLead, bottom = y + step.Height;
        int testY = repeat ? bottom - DiamondHeight - 18 : y;
        int trunk = step.Width / 2 - 6;
        Open(svg, step.Kind.ToString(), step.Line); Diamond(svg,x,y,repeat ? "" : step.Label,topH); svg.Append("</g>");
        Arrow(svg,x,y+topH,x,bodyY);
        if (!repeat && lane.Label.Length > 0) Text(svg,x,bodyY-9,lane.Label,12,halo:true);
        DrawLane(svg,lane,x,bodyY);
        int exit = bodyY + lane.Height;
        if (repeat)
        {
            Arrow(svg,x,exit,x,testY);
            Open(svg,"LoopTest",step.CloseLine); Diamond(svg,x,testY,step.Label,DiamondHeight); svg.Append("</g>");
            int edge = Math.Max(44,SvgText.Measure(step.Label)+48)/2;
            Path(svg,$"M{x-edge},{testY+DiamondHeight/2}H{x-trunk}V{y+8}H{x-14}");
            LeftToRightHead(svg,x-14,y+8);
            Path(svg,$"M{x},{testY+DiamondHeight}V{bottom}");
            Text(svg,x-trunk/2,testY+DiamondHeight/2-4,lane.Label,12,halo:true);
            Text(svg,x,bottom-3,step.Exit,12,halo:true);
        }
        else
        {
            int edge = Math.Max(44,SvgText.Measure(step.Label)+48)/2;
            Path(svg,$"M{x},{exit}V{bottom-18}H{x-trunk}V{y+DiamondHeight/2}H{x-edge}");
            LeftToRightHead(svg,x-edge,y+DiamondHeight/2);
            Path(svg,$"M{x+edge},{y+DiamondHeight/2}H{x+trunk}V{bottom}H{x}");
            Text(svg,x+(edge+trunk)/2,y+DiamondHeight/2-6,step.Exit,12,halo:true);
        }
    }

    private static void Diamond(StringBuilder svg, int x, int y, string label, int height)
    {
        int half = height == 16 ? 14 : Math.Max(44,SvgText.Measure(label)+48)/2;
        int corner = height == 16 ? half : height/2;
        svg.Append($"<path d=\"M{x-half+corner},{y}H{x+half-corner}l{corner},{height/2}l-{corner},{height/2}H{x-half+corner}l-{corner},-{height/2}z\" fill=\"#f1f1f1\" stroke=\"#242424\"/>");
        if (label.Length > 0) Text(svg,x,y+height/2+5,label);
    }
    private static void Bar(StringBuilder svg,int x,int y,int w,int h) => svg.Append($"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" rx=\"3\" fill=\"#333\"/>");
    private static void Path(StringBuilder svg,string d) => svg.Append($"<path d=\"{d}\" fill=\"none\" stroke=\"#333\"/>");
    private static void Head(StringBuilder svg,int x,int y) => svg.Append($"<path d=\"M{x-4},{y-7}L{x},{y}l4,-7z\" fill=\"#333\"/>");
    private static void LeftToRightHead(StringBuilder svg,int x,int y) => svg.Append($"<path d=\"M{x-7},{y-4}L{x},{y}l-7,4z\" fill=\"#333\"/>");
    private static void Arrow(StringBuilder svg,int x1,int y1,int x2,int y2) { Path(svg,$"M{x1},{y1}L{x2},{y2}"); Head(svg,x2,y2); }
    private static void Open(StringBuilder svg,string kind,int line) => svg.Append($"<g class=\"activity-{kind}\" data-source-line=\"{line}\">");
    private static void Text(StringBuilder svg,int x,int y,string value,int size=14,bool bold=false,bool halo=false)
    {
        if (value.Length == 0) return;
        // A white backing keeps a branch caption legible where it crosses its connector.
        if (halo)
        {
            int w=SvgText.Measure(value,size)+6;
            svg.Append($"<rect x=\"{x-w/2}\" y=\"{y-size}\" width=\"{w}\" height=\"{size+2}\" rx=\"2\" fill=\"white\"/>");
        }
        svg.Append($"<text x=\"{x}\" y=\"{y}\" text-anchor=\"middle\" font-family=\"sans-serif\" font-size=\"{size}\" fill=\"#161616\"");
        if (bold) svg.Append(" font-weight=\"bold\"");
        svg.Append('>').Append(SvgText.Escape(value)).Append("</text>");
    }
}

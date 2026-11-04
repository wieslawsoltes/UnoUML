using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

internal static partial class ActivityRenderer
{
    private static void AssignCanvasIdentities(Lane lane, string scope, string? parent,
        Dictionary<(Kind Kind, string Label), int> occurrences)
    {
        foreach (var step in lane.Steps)
        {
            var key = (step.Kind, step.Label);
            int occurrence = occurrences.GetValueOrDefault(key);
            occurrences[key] = occurrence + 1;
            step.CanvasId = CanvasIdentity.Create("activity", scope, step.Kind.ToString(), step.Label,
                occurrence.ToString(CultureInfo.InvariantCulture));
            step.CanvasParent = parent;
            var lanes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var child in step.Lanes)
            {
                int laneOccurrence = lanes.GetValueOrDefault(child.Label);
                lanes[child.Label] = laneOccurrence + 1;
                child.CanvasId = CanvasIdentity.Create("activity-lane", step.CanvasId, child.Label,
                    laneOccurrence.ToString(CultureInfo.InvariantCulture));
                // Only a partition is geometric containment. Branches and loops
                // are graph topology; adding unrelated statements cannot rename
                // the other activities in their enclosing partition.
                if (step.Kind == Kind.Partition) AssignCanvasIdentities(child, step.CanvasId, step.CanvasId, []);
                else AssignCanvasIdentities(child, scope, parent, occurrences);
            }
        }
    }

    /// <summary>
    /// A structured block can finish away from its last real node. Carry the
    /// continuation until its actual target is known, so the SVG has one complete
    /// route per graph edge rather than unrelated movable connector fragments.
    /// </summary>
    private sealed record FlowExit(string Id, int X, int Y, string Route,
        string Captions = "", string Role = "flow", bool HasRoute = false)
    {
        internal static FlowExit From(string id, int x, int y) =>
            new(id, x, y, FormattableString.Invariant($"M{x},{y}"));

        internal FlowExit Extend(int x, int y, string segment) =>
            this with { X = x, Y = y, Route = Route + segment, HasRoute = true };
    }

    private static void Connect(StringBuilder svg, FlowExit from, string target,
        bool head = true, bool horizontalHead = false, string? role = null)
    {
        string id = CanvasIdentity.Create("activity-edge", from.Id, target, role ?? from.Role);
        svg.Append("<g class=\"link\" data-style-name=\"arrow\" data-style-metrics=\"fontsize\" data-canvas-id=\"").Append(id)
            .Append("\" data-entity-1=\"").Append(from.Id).Append("\" data-entity-2=\"").Append(target).Append("\">");
        Path(svg, from.Route);
        if (head)
        {
            if (horizontalHead) LeftToRightHead(svg, from.X, from.Y);
            else Head(svg, from.X, from.Y);
        }
        svg.Append(from.Captions).Append("</g>");
    }

    private static string CaptionMarkup(int x, int top, StyleLabel label)
    {
        var svg = new StringBuilder();
        Caption(svg, x, top, label, halo: true);
        return svg.ToString();
    }
}

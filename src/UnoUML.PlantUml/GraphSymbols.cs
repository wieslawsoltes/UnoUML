using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

internal static partial class GraphRenderer
{
    private static (string Letter, string Color) TypeBadge(string kind) => kind switch
    {
        "abstract" or "abstract class" => ("A", "#a9dcdf"),
        "interface" => ("I", "#b4a7e5"),
        "enum" => ("E", "#eb937f"),
        "annotation" => ("@", "#e3664a"),
        "exception" => ("X", "#d94321"),
        "struct" => ("S", "#f1f1f1"),
        "protocol" => ("P", "#f1f1f1"),
        "metaclass" => ("M", "#cccccc"),
        "stereotype" => ("S", "#ff77ff"),
        "entity" => ("E", "#add1b2"),
        _ => ("C", "#add1b2")
    };

    private static bool DrawDeploymentSymbol(StringBuilder s, Node n, ref int labelY, string fill, string stroke)
    {
        int x = n.X, y = n.Y, w = n.Width, h = n.Height;
        int cx = x + w / 2, cy = y + h / 2, right = x + w, bottom = y + h;
        void Circle(int ox, int oy, int radius) =>
            s.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{ox}\" cy=\"{oy}\" r=\"{radius}\" fill=\"{fill}\" stroke=\"{stroke}\"/>");
        switch (n.Kind)
        {
            case "label":
                return true;
            case "agent":
            case "card":
                Rect(s, x, y, w, h, 3, fill);
                return true;
            case "action":
                Path(s, $"M{x} {y}H{right - 11}L{right} {cy}L{right - 11} {bottom}H{x}Z", fill);
                return true;
            case "process":
                Path(s, $"M{x} {y}H{right - 13}L{right} {cy}L{right - 13} {bottom}H{x}L{x + 12} {cy}Z", fill);
                return true;
            case "hexagon":
                Path(s, $"M{x} {cy}L{x + 18} {y}H{right - 18}L{right} {cy}L{right - 18} {bottom}H{x + 18}Z", fill);
                return true;
            case "circle":
            case "interface" when n.DeploymentGlyph:
                Circle(cx, y + 15, 8);
                labelY = y + 44;
                return true;
            case "boundary":
            case "control":
            case "entity" when n.DeploymentGlyph:
                int center = n.Kind == "boundary" ? cx + 9 : cx;
                Circle(center, y + 15, 12);
                if (n.Kind == "boundary") Path(s, $"M{center - 29} {y + 3}v24m0 -12h17");
                if (n.Kind == "control") Path(s, $"M{cx - 4} {y + 3}l7 -5 -2 5 2 5Z", stroke);
                if (n.Kind == "entity") Path(s, $"M{cx - 13} {y + 30}h26");
                labelY = y + 46;
                return true;
            case "person":
                Circle(cx, y + 10, 9);
                Rect(s, x, y + 20, w, h - 20, 10, fill);
                labelY = cy + 14;
                return true;
            case "collections":
                Rect(s, x + 5, y + 5, w - 5, h - 5, 3, fill);
                Rect(s, x, y, w - 5, h - 5, 3, fill);
                return true;
            case "stack":
                Rect(s, x + 12, y, w - 24, h, 3, fill);
                Path(s, $"M{x} {y}h12m{w - 24} 0h12");
                return true;
            case "storage":
                Rect(s, x, y, w, h, h / 2, fill);
                return true;
            case "queue":
                Path(s, $"M{x + 7} {y}H{right - 7}C{right + 2} {y} {right + 2} {bottom} {right - 7} {bottom}H{x + 7}C{x - 2} {bottom} {x - 2} {y} {x + 7} {y}Z", fill);
                Path(s, $"M{right - 7} {y}C{right - 16} {y} {right - 16} {bottom} {right - 7} {bottom}");
                return true;
            case "frame":
                Rect(s, x, y, w, h, 2, fill);
                Path(s, $"M{x + w / 3} {y}v7l-7 7H{x}");
                labelY = n.Children.Count == 0 ? cy + 11 : y + 32;
                return true;
            case "artifact":
                Rect(s, x, y, w, h, 2, fill);
                Path(s, $"M{right - 19} {y + 4}h7l6 6v8h-13ZM{right - 12} {y + 4}v6h6", fill);
                labelY = cy + 8;
                return true;
            case "cloud":
                // A symmetric scalloped contour scales with the label, keeping the cloud a single closed SVG path.
                Path(s, $"M{x + 11} {y + 10}Q{x + 16} {y - 2} {x + w / 4} {y + 7}Q{x + w * 3 / 8} {y - 3} {cx} {y + 6}Q{x + w * 5 / 8} {y - 3} {x + w * 3 / 4} {y + 7}Q{right - 12} {y - 2} {right - 10} {y + 12}Q{right + 2} {y + 12} {right - 5} {cy}Q{right + 1} {bottom - 8} {right - 13} {bottom - 9}Q{right - 15} {bottom + 2} {x + w * 3 / 4} {bottom - 6}Q{x + w * 5 / 8} {bottom + 3} {cx} {bottom - 5}Q{x + w * 3 / 8} {bottom + 2} {x + w / 4} {bottom - 6}Q{x + 9} {bottom + 2} {x + 10} {bottom - 11}Q{x - 2} {bottom - 12} {x + 5} {cy}Q{x - 2} {y + 10} {x + 11} {y + 10}Z", fill);
                return true;
            default:
                return false;
        }
    }
}

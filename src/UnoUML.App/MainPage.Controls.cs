using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace UnoUML.App;

public sealed partial class MainPage
{
    static readonly SolidColorBrush Ink = Brush(47, 45, 65);
    static readonly SolidColorBrush Muted = Brush(106, 103, 122);
    static readonly SolidColorBrush Accent = Brush(91, 72, 191);
    static readonly SolidColorBrush Tint = Brush(235, 232, 251);
    static readonly SolidColorBrush Clear = Brush(0, 0, 0, 0);
    static readonly SolidColorBrush White = Brush(255, 255, 255);

    static SolidColorBrush Brush(byte r, byte g, byte b, byte a = 255) => new(Color.FromArgb(a, r, g, b));

    // Each stroke uses a 20-unit design grid. Native vector paths stay sharp across DPI and theme changes.
    static readonly Dictionary<string, int[][]> Glyphs = new()
    {
        ["library"] = [[3,3,3,17,7,17,7,3,3,3],[10,3,10,17],[13,4,17,16]],
        ["new"] = [[4,2,12,2,16,6,16,17,4,17,4,2],[12,2,12,6,16,6],[7,11,13,11],[10,8,10,14]],
        ["file"] = [[4,2,12,2,16,6,16,17,4,17,4,2],[12,2,12,6,16,6]],
        ["open"] = [[2,15,2,5,8,5,10,7,16,7,16,9],[2,16,5,9,18,9,15,16,2,16]],
        ["save"] = [[3,3,14,3,17,6,17,17,3,17,3,3],[6,3,6,8,13,8,13,3],[6,17,6,12,14,12,14,17]],
        ["export"] = [[3,12,3,17,17,17,17,12],[10,13,10,2],[6,6,10,2,14,6]],
        ["canvas"] = [[2,4,18,4,18,16,2,16,2,4],[5,12,9,8,12,11,14,9,16,11]],
        ["split"] = [[2,4,18,4,18,16,2,16,2,4],[10,4,10,16]],
        ["source"] = [[7,5,2,10,7,15],[13,5,18,10,13,15]],
        ["undo"] = [[7,3,3,7,7,11],[4,7,12,7,15,8,17,11,16,14,13,16,9,16]],
        ["redo"] = [[13,3,17,7,13,11],[16,7,8,7,5,8,3,11,4,14,7,16,11,16]],
        ["marquee"] = [[2,7,2,3,6,3],[9,3,11,3],[14,3,18,3,18,7],[18,10,18,12],[18,14,18,17,14,17],[11,17,9,17],[6,17,2,17,2,14],[2,11,2,9]],
        ["select"] = [[4,2,16,11,10,12,7,17,4,2]],
        ["pan"] = [[3,11,6,15,9,17,13,17,16,14,16,8],[6,13,6,5,8,5,8,11],[8,6,8,3,10,3,10,10],[10,5,12,4,12,10],[12,7,14,6,14,11]],
        ["connect"] = [[2,2,7,2,7,7,2,7,2,2],[13,13,18,13,18,18,13,18,13,13],[7,4,11,4,11,15,13,15]],
        ["move"] = [[2,10,18,10],[5,7,2,10,5,13],[15,7,18,10,15,13],[10,2,10,18],[7,5,10,2,13,5],[7,15,10,18,13,15]],
        ["minus"] = [[4,10,16,10]], ["plus"] = [[4,10,16,10],[10,4,10,16]],
        ["fit"] = [[3,8,3,3,8,3],[12,3,17,3,17,8],[17,12,17,17,12,17],[8,17,3,17,3,12]],
        ["inspector"] = [[2,3,18,3,18,17,2,17,2,3],[12,3,12,17]],
        ["side"] = [[2,3,18,3,18,17,2,17,2,3],[8,3,8,17]],
        ["box"] = [[3,4,17,4,17,16,3,16,3,4],[3,8,17,8]],
        ["diamond"] = [[10,2,18,10,10,18,2,10,10,2]],
        ["actor"] = [[8,3,12,3,13,5,12,7,8,7,7,5,8,3],[10,8,10,12],[4,10,16,10],[5,17,10,12,15,17]],
        ["node"] = [[3,6,10,2,17,6,17,14,10,18,3,14,3,6],[3,6,10,10,17,6],[10,10,10,18]],
        ["layers"] = [[2,7,10,3,18,7,10,11,2,7],[2,11,10,15,18,11],[3,15,10,18,17,15]],
        ["message"] = [[2,10,17,10],[12,5,17,10,12,15]],
        ["note"] = [[3,3,17,3,17,11,11,17,3,17,3,3],[11,17,11,11,17,11]],
        ["list"] = [[3,5,4,5],[7,5,17,5],[3,10,4,10],[7,10,17,10],[3,15,4,15],[7,15,17,15]],
        ["copy"] = [[7,7,17,7,17,17,7,17,7,7],[4,13,3,13,3,3,13,3,13,4]],
        ["delete"] = [[3,5,17,5],[7,5,7,3,13,3,13,5],[5,5,6,17,14,17,15,5],[8,8,8,14],[12,8,12,14]],
        ["up"] = [[4,12,10,6,16,12]], ["down"] = [[4,8,10,14,16,8]],
        ["edit"] = [[3,17,4,12,13,3,17,7,8,16,3,17],[11,5,15,9]],
        ["more"] = [[3,10,4,10],[9,10,10,10],[15,10,16,10]],
        ["theme"] = [[2,4,18,4,18,16,2,16,2,4],[2,8,18,8],[7,8,7,16]],
        ["db"] = [[3,5,5,3,15,3,17,5,15,7,5,7,3,5,3,15,5,17,15,17,17,15,17,5],[3,10,5,12,15,12,17,10]]
    };

    static FrameworkElement Icon(string key, SolidColorBrush? color = null, double size = 16)
    {
        var canvas = new Canvas { Width = 20, Height = 20 };
        foreach (var stroke in Glyphs.GetValueOrDefault(key, Glyphs["box"]))
        {
            var line = new Polyline { Stroke = color ?? Ink, StrokeThickness = 1.65,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            for (var i = 0; i < stroke.Length; i += 2) line.Points.Add(new Point(stroke[i], stroke[i + 1]));
            canvas.Children.Add(line);
        }
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false };
    }

    static string ShapeIcon(string kind)
    {
        string name = kind.ToLowerInvariant();
        if (name.Contains("actor") || name.Contains("participant") || name.Contains("person")) return "actor";
        if (name.Contains("message") || name.Contains("return") || name.Contains("relation") || name.Contains("link") || name.Contains("arrow")) return "message";
        if (name.Contains("database") || name.Contains("storage")) return "db";
        if (name.Contains("note") || name.Contains("comment")) return "note";
        if (name.Contains("node") || name.Contains("component")) return "node";
        if (name.Contains("group") || name.Contains("package") || name.Contains("frame")) return "layers";
        if (name.Contains("decision") || name.Contains("choice") || name.Contains("state")) return "diamond";
        return "box";
    }

    static TextBlock Caption(string value) => new() { Text = value, FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap };

    static Button Command(string title, string icon, Action action, bool showLabel = false, bool primary = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Icon(icon, primary ? White : Ink));
        if (showLabel) row.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = primary ? White : Ink, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = row, Height = 32, MinWidth = 30, MinHeight = 0, Padding = new Thickness(showLabel ? 9 : 6, 3, showLabel ? 9 : 6, 3),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5), Background = primary ? Accent : Clear, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, title);
        ToolTipService.SetToolTip(button, title);
        button.Click += (_, _) => action();
        return button;
    }

    static Button Tab(string title, Action action)
    {
        var button = new Button { Content = title, MinWidth = 0, MinHeight = 0, Height = 28, FontSize = 12,
            Padding = new Thickness(8, 1, 8, 1), Foreground = Muted, Background = Clear, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(0) };
        button.Click += (_, _) => action();
        return button;
    }

    static void Highlight(Button button, bool active)
    {
        button.Background = active ? Tint : Clear;
        button.Foreground = active ? Accent : Muted;
    }

    static Button Item(string title, string icon, Action action, bool colored = false)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tile = new Border { Width = colored ? 25 : 18, Height = colored ? 25 : 18, Background = colored ? Tint : Clear,
            CornerRadius = new CornerRadius(5), Child = Icon(icon, colored ? Accent : Muted, 15), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(tile);
        var label = new TextBlock { Text = title, FontSize = 13, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(label, 1); row.Children.Add(label);
        var button = new Button { Content = row, MinHeight = colored ? 34 : 27, MinWidth = 0, Background = Clear, BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 2, 5, 2), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(button, title);
        ToolTipService.SetToolTip(button, title);
        button.Click += (_, _) => action();
        return button;
    }
}

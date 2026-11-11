using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace UnoUML.App.Rendering;

/// <summary>Assigns XAML names consumed by Uno 7's semantic accessibility tree.</summary>
internal static class BrowserAccessibility
{
    internal static void SetName(FrameworkElement element, string name) =>
        AutomationProperties.SetName(element, name);

    internal static void PrepareDialog(ContentDialog dialog)
    {
        SetName(dialog, dialog.Title?.ToString() ?? "Dialog");
        dialog.Opened += (_, _) => AttachTree(dialog);
    }

    private static void AttachTree(DependencyObject root)
    {
        if (root is FrameworkElement element && element is Button or TextBox or ComboBox or ToggleButton or ToggleSwitch)
        {
            string name = AutomationProperties.GetName(element);
            if (name.Length == 0)
                name = element switch
                {
                    TextBox text => text.Header?.ToString() ?? text.PlaceholderText,
                    ComboBox combo => combo.Header?.ToString() ?? "Choose a value",
                    ToggleSwitch toggle => toggle.Header?.ToString() ?? "",
                    ToggleButton toggle => toggle.Content as string ?? "",
                    Button button => button.Content as string ?? "",
                    _ => ""
                };
            if (name.Length != 0) SetName(element, name);
        }
        for (int i = 0, count = VisualTreeHelper.GetChildrenCount(root); i < count; i++)
            AttachTree(VisualTreeHelper.GetChild(root, i));
    }

}

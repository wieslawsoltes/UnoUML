using System.Runtime.InteropServices.JavaScript;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace UnoUML.App.Rendering;

/// <summary>Assigns XAML names consumed by Uno 7's semantic accessibility tree.</summary>
internal static partial class BrowserAccessibility
{
    private const string Module = "UnoUML.NativeAccessibility";
    private static Task<JSObject>? import;

    internal static async Task InitializeAsync()
    {
        if (!OperatingSystem.IsBrowser()) return;
        if (import is null)
        {
            using var document = JSHost.GlobalThis.GetPropertyAsJSObject("document")!;
            var url = new Uri(new Uri(document.GetPropertyAsString("baseURI")!), "uml/native-accessibility.js");
            import = JSHost.ImportAsync(Module, url.AbsoluteUri);
        }
        await import;
        InstallKeyboardBoundary();
    }

    internal static void SetName(FrameworkElement element, string name) =>
        AutomationProperties.SetName(element, name);

    internal static void PrepareDialog(ContentDialog dialog)
    {
        SetName(dialog, dialog.Title?.ToString() ?? "Dialog");
        string automationId = AutomationProperties.GetAutomationId(dialog);
        if (automationId.Length == 0)
        {
            automationId = "UnoUML.Dialog." + Guid.NewGuid().ToString("N");
            AutomationProperties.SetAutomationId(dialog, automationId);
        }

        int generation = 0;
        dialog.Closed += (_, _) =>
        {
            int closed = generation++;
            if (!OperatingSystem.IsBrowser()) return;
            // Native Closed restores its own snapshots first. A stale close
            // cannot release a newer opening of this same ContentDialog.
            dialog.DispatcherQueue.TryEnqueue(() =>
            {
                try { EndDialogScope(automationId, closed); }
                catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
            });
        };
        dialog.Opened += (_, _) =>
        {
            int current = ++generation;
            // Our handler was registered before the dialog entered the visual
            // tree, so capture before naming can rebuild semantic nodes and
            // before the native Opened handler installs its focus trap.
            if (OperatingSystem.IsBrowser())
            {
                try { BeginDialogScope(automationId, current); }
                catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
            }
            AttachTree(dialog);
            if (!OperatingSystem.IsBrowser()) return;
            // Activate after all native handlers. The scoped bridge then keeps
            // that public modal contract intact through semantic-node rebuilds.
            dialog.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (generation == current) ActivateDialogScope(automationId, current);
                }
                catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
            });
        };
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

    [JSImport("installAccessibilityKeyboardBoundary", Module)]
    private static partial void InstallKeyboardBoundary();

    [JSImport("beginDialogScope", Module)]
    private static partial void BeginDialogScope(string automationId, int generation);

    [JSImport("activateDialogScope", Module)]
    private static partial void ActivateDialogScope(string automationId, int generation);

    [JSImport("endDialogScope", Module)]
    private static partial void EndDialogScope(string automationId, int generation);
}

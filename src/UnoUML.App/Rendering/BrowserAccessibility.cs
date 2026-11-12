using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.JavaScript;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace UnoUML.App.Rendering;

/// <summary>Assigns XAML names consumed by Uno 7's semantic accessibility tree.</summary>
internal static partial class BrowserAccessibility
{
    private const string Module = "UnoUML.NativeAccessibility";
    private static Task<JSObject>? import;
    private static readonly ConditionalWeakTable<ComboBox, object> comboAttachments = new();

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

    internal static void AttachComboBox(ComboBox combo)
    {
        if (!OperatingSystem.IsBrowser() || comboAttachments.TryGetValue(combo, out _)) return;
        comboAttachments.Add(combo, new object());
        string automationId = AutomationProperties.GetAutomationId(combo);
        if (automationId.Length == 0)
        {
            automationId = "UnoUML.ComboBox." + Guid.NewGuid().ToString("N");
            AutomationProperties.SetAutomationId(combo, automationId);
        }

        int generation = 0, focusRevision = 0;
        bool open = false, publishingFocus = false;
        void QueueFocusedItem()
        {
            if (!open || publishingFocus) return;
            int current = generation, revision = ++focusRevision;
            // Public focus events are asynchronous. Read the current owner
            // after native OnOpen/keyboard focus work, rather than using an
            // event argument that may describe an already superseded target.
            combo.DispatcherQueue.TryEnqueue(() =>
            {
                if (!open || current != generation || revision != focusRevision) return;
                try
                {
                    int index = -1;
                    if (combo.XamlRoot is { } root &&
                        FocusManager.GetFocusedElement(root) is ComboBoxItem focused &&
                        ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(focused), combo))
                    {
                        int candidate = combo.IndexFromContainer(focused);
                        if (candidate >= 0 && candidate < combo.Items.Count &&
                            ReferenceEquals(combo.ContainerFromIndex(candidate), focused)) index = candidate;
                    }
                    publishingFocus = true;
                    FocusComboScope(automationId, current, revision, index);
                }
                catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
                finally { publishingFocus = false; }
            });
        }
        void OnNativeGotFocus(object? sender, FocusManagerGotFocusEventArgs args) => QueueFocusedItem();
        void OnNativeLostFocus(object? sender, FocusManagerLostFocusEventArgs args) => QueueFocusedItem();
        void Begin()
        {
            if (open) return;
            open = true;
            int current = ++generation;
            focusRevision = 0;
            FocusManager.GotFocus += OnNativeGotFocus;
            FocusManager.LostFocus += OnNativeLostFocus;
            // Uno's logical options are created after the selected item. Its
            // creation API omits aria-selected, so project the authoritative
            // public index without changing selection or native keyboard input.
            try { BeginComboScope(automationId, current, combo.SelectedIndex); }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
            QueueFocusedItem();
        }
        void End()
        {
            if (!open) return;
            open = false;
            focusRevision++;
            FocusManager.GotFocus -= OnNativeGotFocus;
            FocusManager.LostFocus -= OnNativeLostFocus;
            try { EndComboScope(automationId, generation); }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        }
        combo.DropDownOpened += (_, _) => Begin();
        combo.DropDownClosed += (_, _) => End();
        combo.Unloaded += (_, _) => End();
        combo.SelectionChanged += (_, _) =>
        {
            if (!open) return;
            try { UpdateComboScope(automationId, generation, combo.SelectedIndex); }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        };
        if (combo.IsDropDownOpen) Begin();
    }

    internal static void PrepareDialog(ContentDialog dialog)
    {
        SetName(dialog, dialog.Title?.ToString() ?? "Dialog");
        string automationId = AutomationProperties.GetAutomationId(dialog);
        if (automationId.Length == 0)
        {
            automationId = "UnoUML.Dialog." + Guid.NewGuid().ToString("N");
            AutomationProperties.SetAutomationId(dialog, automationId);
        }
        // SemanticElementFactory projects AutomationId only at creation. Walk
        // the public content before ShowAsync, while native heads do not exist.
        if (dialog.Content is DependencyObject content) AttachTree(content);

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

    private static void AttachTree(DependencyObject root) =>
        AttachTree(root, new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance));

    private static void AttachTree(DependencyObject root, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(root)) return;
        if (root is ComboBox picker) AttachComboBox(picker);
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
        // ContentControl templates need not exist before ShowAsync. Follow
        // their public content and panel children as well as realized visuals.
        if (root is ContentControl { Content: DependencyObject content }) AttachTree(content, visited);
        if (root is Border { Child: DependencyObject child }) AttachTree(child, visited);
        if (root is Panel panel)
            foreach (var item in panel.Children) AttachTree(item, visited);
        for (int i = 0, count = VisualTreeHelper.GetChildrenCount(root); i < count; i++)
            AttachTree(VisualTreeHelper.GetChild(root, i), visited);
    }

    [JSImport("installAccessibilityKeyboardBoundary", Module)]
    private static partial void InstallKeyboardBoundary();

    [JSImport("beginDialogScope", Module)]
    private static partial void BeginDialogScope(string automationId, int generation);

    [JSImport("activateDialogScope", Module)]
    private static partial void ActivateDialogScope(string automationId, int generation);

    [JSImport("endDialogScope", Module)]
    private static partial void EndDialogScope(string automationId, int generation);

    [JSImport("beginComboScope", Module)]
    private static partial void BeginComboScope(string automationId, int generation, int selectedIndex);

    [JSImport("updateComboScope", Module)]
    private static partial void UpdateComboScope(string automationId, int generation, int selectedIndex);

    [JSImport("endComboScope", Module)]
    private static partial void EndComboScope(string automationId, int generation);

    [JSImport("focusComboScope", Module)]
    private static partial void FocusComboScope(string automationId, int generation, int focusRevision, int focusedIndex);
}

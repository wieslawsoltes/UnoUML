using System.Globalization;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.JavaScript;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace UnoUML.App.Rendering;

/// <summary>Maps named XAML controls to the native DOM used by Uno 6.7's browser renderer.</summary>
internal static partial class BrowserAccessibility
{
    // Uno 6.7.135 publishes Name changes only through its Skia automation listener.
    // Keep the XAML metadata authoritative and map it to the actual native input,
    // including inputs replaced by a template. Registrations live with their controls.
    private static readonly ConditionalWeakTable<FrameworkElement, Registration> Registrations = new();

    internal static void SetName(FrameworkElement element, string name)
    {
        AutomationProperties.SetName(element, name);
        Attach(element);
    }

    internal static void Attach(FrameworkElement element)
    {
        if (OperatingSystem.IsBrowser()) Registrations.GetValue(element, static owner => new(owner));
    }

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

    private sealed class Registration
    {
        private readonly FrameworkElement owner;
        private bool mounted;
        private bool updateQueued;
        private int generation;
        private Panel? popupPanel;
        private string Id => owner.Handle.ToInt64().ToString(CultureInfo.InvariantCulture);

        internal Registration(FrameworkElement owner)
        {
            this.owner = owner;
            owner.Loaded += (_, _) => Update();
            owner.Unloaded += (_, _) =>
            {
                generation++;
                DisconnectPopup();
                if (mounted) { Remove(Id); mounted = false; }
            };
            owner.RegisterPropertyChangedCallback(AutomationProperties.NameProperty, (_, _) => Update());
            if (owner is Control control) control.IsEnabledChanged += (_, _) => Update();
            if (owner is TextBox text)
                text.RegisterPropertyChangedCallback(TextBox.IsReadOnlyProperty, (_, _) => Update());
            if (owner is ComboBox combo)
            {
                // Uno raises DropDownOpened before its layout pass materializes the
                // popup containers. A single dispatcher turn observes the finished
                // panel; its collection notifications handle later virtualization.
                combo.DropDownOpened += (_, _) => QueueUpdate();
                combo.DropDownClosed += (_, _) => QueueUpdate();
                combo.SelectionChanged += (_, _) => QueueUpdate();
                combo.Items.VectorChanged += (_, _) => QueueUpdate();
                combo.RegisterPropertyChangedCallback(Control.TemplateProperty, (_, _) => QueueUpdate());
            }
            if (owner is ComboBoxItem item)
            {
                item.RegisterPropertyChangedCallback(SelectorItem.IsSelectedProperty, (_, _) => Update());
                item.RegisterPropertyChangedCallback(ContentControl.ContentProperty, (_, _) => QueueUpdate());
            }
            if (owner is ToggleSwitch toggleSwitch)
                toggleSwitch.Toggled += (_, _) => Update();
            if (owner is ToggleButton toggleButton)
                toggleButton.RegisterPropertyChangedCallback(ToggleButton.IsCheckedProperty, (_, _) => Update());
            if (owner.IsLoaded) Update();
        }

        private void QueueUpdate()
        {
            if (updateQueued || !owner.IsLoaded) return;
            updateQueued = true;
            if (!owner.DispatcherQueue.TryEnqueue(() => { updateQueued = false; Update(); }))
                updateQueued = false;
        }

        private void PopupChildrenChanged(object? sender, NotifyCollectionChangedEventArgs args) => QueueUpdate();
        private void PopupLoaded(object sender, RoutedEventArgs args) => QueueUpdate();

        private void DisconnectPopup()
        {
            if (popupPanel is null) return;
            popupPanel.Children.CollectionChanged -= PopupChildrenChanged;
            popupPanel.Loaded -= PopupLoaded;
            popupPanel = null;
        }

        private void UpdatePopup(ComboBox combo)
        {
            var panel = combo.IsDropDownOpen ? combo.ItemsPanelRoot : null;
            if (!ReferenceEquals(popupPanel, panel))
            {
                DisconnectPopup();
                popupPanel = panel;
                if (panel is not null)
                {
                    panel.Children.CollectionChanged += PopupChildrenChanged;
                    panel.Loaded += PopupLoaded;
                }
            }
            string panelId = panel is { IsLoaded: true } ? panel.Handle.ToInt64().ToString(CultureInfo.InvariantCulture) : "";
            ConfigurePopup(Id, panelId);
            if (panel is null) return;
            foreach (var child in panel.Children)
                if (child is ComboBoxItem item)
                    Registrations.GetValue(item, static element => new(element)).QueueUpdate();
        }

        private async void Update()
        {
            if (!owner.IsLoaded) return;
            int current = ++generation;
            try
            {
                await BrowserBridge.ReadyAsync();
                if (!owner.IsLoaded || generation != current) return;
                string role = owner switch
                {
                    TextBox => "textbox", ComboBox => "combobox", Button => "button",
                    ComboBoxItem => "option", CheckBox => "checkbox", RadioButton => "radio",
                    ToggleSwitch => "switch", ToggleButton => "button",
                    ContentDialog => "dialog", _ => "group"
                };
                string name = AutomationProperties.GetName(owner);
                string state = owner switch
                {
                    ComboBoxItem item => item.IsSelected ? "true" : "false",
                    ToggleSwitch toggle => toggle.IsOn ? "true" : "false",
                    ToggleButton toggle => toggle.IsChecked switch { true => "true", false => "false", null => "mixed" },
                    _ => ""
                };
                int position = 0, count = 0;
                if (owner is ComboBoxItem option)
                {
                    if (name.Length == 0) name = option.Content?.ToString() ?? "";
                    if (ItemsControl.ItemsControlFromItemContainer(option) is ComboBox parent)
                    {
                        position = parent.IndexFromContainer(option) + 1;
                        count = parent.Items.Count;
                    }
                }
                Configure(Id, role, name, owner is not Control { IsEnabled: false },
                    owner is TextBox { IsReadOnly: true }, owner is ComboBox { IsDropDownOpen: true }, state, position, count);
                mounted = true;
                if (owner is ComboBox combo) UpdatePopup(combo);
            }
            catch (Exception error)
            {
                // Rendering reports module-load failures. Accessibility must not
                // turn a failed or superseded startup into an async-void crash.
                System.Diagnostics.Debug.WriteLine(error);
            }
        }
    }

    [JSImport("configureAccessibility", "UnoUML.Viewport")]
    private static partial void Configure(string id, string role, string name, bool enabled, bool readOnly, bool expanded,
        string state, int position, int count);

    [JSImport("configureAccessibilityPopup", "UnoUML.Viewport")]
    private static partial void ConfigurePopup(string id, string panelId);

    [JSImport("removeAccessibility", "UnoUML.Viewport")]
    private static partial void Remove(string id);
}

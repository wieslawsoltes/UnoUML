using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.JavaScript;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
        if (root is FrameworkElement element && element is Button or TextBox or ComboBox)
        {
            string name = AutomationProperties.GetName(element);
            if (name.Length == 0)
                name = element switch
                {
                    TextBox text => text.Header?.ToString() ?? text.PlaceholderText,
                    ComboBox combo => combo.Header?.ToString() ?? "Choose a value",
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
        private int generation;
        private string Id => owner.Handle.ToInt64().ToString(CultureInfo.InvariantCulture);

        internal Registration(FrameworkElement owner)
        {
            this.owner = owner;
            owner.Loaded += (_, _) => Update();
            owner.Unloaded += (_, _) =>
            {
                generation++;
                if (mounted) { Remove(Id); mounted = false; }
            };
            owner.RegisterPropertyChangedCallback(AutomationProperties.NameProperty, (_, _) => Update());
            if (owner is Control control) control.IsEnabledChanged += (_, _) => Update();
            if (owner is TextBox text)
                text.RegisterPropertyChangedCallback(TextBox.IsReadOnlyProperty, (_, _) => Update());
            if (owner is ComboBox combo)
            {
                combo.DropDownOpened += (_, _) => Update();
                combo.DropDownClosed += (_, _) => Update();
            }
            if (owner.IsLoaded) Update();
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
                    ContentDialog => "dialog", _ => "group"
                };
                Configure(Id, role, AutomationProperties.GetName(owner), owner is not Control { IsEnabled: false },
                    owner is TextBox { IsReadOnly: true }, owner is ComboBox { IsDropDownOpen: true });
                mounted = true;
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
    private static partial void Configure(string id, string role, string name, bool enabled, bool readOnly, bool expanded);

    [JSImport("removeAccessibility", "UnoUML.Viewport")]
    private static partial void Remove(string id);
}

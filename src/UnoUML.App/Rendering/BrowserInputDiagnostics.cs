using System.Buffers;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace UnoUML.App.Rendering;

/// <summary>Opt-in read-only snapshots for native input failure diagnostics.</summary>
internal static partial class BrowserInputDiagnostics
{
    internal static void Attach(TextBox target, string automationId)
    {
        int generation = 0;
        bool loaded = false;
        void Begin()
        {
            if (loaded) return;
            loaded = true;
            int current = ++generation;
            try
            {
                Register(automationId, current,
                    () => loaded && current == generation ? Snapshot(target) : "null");
            }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        }
        target.Loaded += (_, _) => Begin();
        target.Unloaded += (_, _) =>
        {
            if (!loaded) return;
            loaded = false;
            try { Unregister(automationId, generation); }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        };
        if (target.IsLoaded) Begin();
    }

    private static string Snapshot(TextBox target)
    {
        object? focused = target.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
        TextBox? focusedTextBox = TextBoxAncestor(focused as DependencyObject);
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteBoolean("hasXamlRoot", target.XamlRoot != null);
            json.WriteBoolean("focusIsTarget", ReferenceEquals(focused, target));
            json.WriteBoolean("focusOwnerIsTarget", ReferenceEquals(focusedTextBox, target));
            json.WritePropertyName("target");
            WriteElement(json, target);
            json.WritePropertyName("focused");
            WriteElement(json, focused);
            json.WritePropertyName("focusedTextBox");
            WriteElement(json, focusedTextBox);
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static TextBox? TextBoxAncestor(DependencyObject? element)
    {
        for (int depth = 0; element != null && depth < 128; depth++)
        {
            if (element is TextBox text) return text;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private static void WriteElement(Utf8JsonWriter json, object? value)
    {
        if (value is not DependencyObject element) { json.WriteNullValue(); return; }
        json.WriteStartObject();
        json.WriteString("type", element.GetType().FullName);
        json.WriteString("name", AutomationProperties.GetName(element));
        json.WriteString("automationId", AutomationProperties.GetAutomationId(element));
        if (element is FrameworkElement framework) json.WriteBoolean("loaded", framework.IsLoaded);
        if (element is Control control)
        {
            json.WriteString("focusState", control.FocusState.ToString());
            json.WriteBoolean("enabled", control.IsEnabled);
        }
        if (element is TextBox text)
        {
            json.WriteNumber("length", text.Text.Length);
            json.WriteBoolean("bom", text.Text.StartsWith('\uFEFF'));
            json.WriteNumber("selectionStart", text.SelectionStart);
            json.WriteNumber("selectionLength", text.SelectionLength);
            json.WriteBoolean("readOnly", text.IsReadOnly);
        }
        json.WriteEndObject();
    }

    [JSImport("registerTextBoxDiagnostics", "UnoUML.NativeAccessibility")]
    private static partial void Register(string automationId, int generation,
        [JSMarshalAs<JSType.Function<JSType.String>>] Func<string> read);

    [JSImport("unregisterTextBoxDiagnostics", "UnoUML.NativeAccessibility")]
    private static partial void Unregister(string automationId, int generation);
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace UnoUML.App.Rendering;

/// <summary>Preserves a source BOM in the browser's semantic and IME text projections.</summary>
internal static partial class BrowserSourceText
{
    private static readonly ConditionalWeakTable<TextBox, Registration> Registrations = new();

    internal static void Attach(TextBox owner)
    {
        if (OperatingSystem.IsBrowser())
            Registrations.GetValue(owner, static control => new(control));
    }

    private sealed class Registration
    {
        private readonly TextBox owner;
        private readonly string automationId;
        private int generation;
        private bool mounted;

        internal Registration(TextBox owner)
        {
            this.owner = owner;
            automationId = AutomationProperties.GetAutomationId(owner).Trim();
            if (automationId.Length == 0)
            {
                automationId = "UnoUML.SourceText." + Guid.NewGuid().ToString("N");
                AutomationProperties.SetAutomationId(owner, automationId);
            }

            owner.Loaded += (_, _) => Update();
            owner.GotFocus += (_, _) => Update();
            owner.LostFocus += (_, _) => Update();
            owner.Unloaded += (_, _) =>
            {
                generation++;
                if (mounted) { Remove(automationId); mounted = false; }
            };
            owner.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) => Update());
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
                string source = owner.Text;
                if (!source.StartsWith('\uFEFF'))
                {
                    if (mounted) { Remove(automationId); mounted = false; }
                    return;
                }

                // JSON starts with a quote, so string marshalling cannot consume
                // the document's leading U+FEFF. AutomationId is a public XAML
                // contract; UIElement.Handle no longer represents a DOM element.
                Sync(automationId, JsonSerializer.Serialize(source),
                    owner.FocusState != FocusState.Unfocused, owner.SelectionStart, owner.SelectionLength);
                mounted = true;
            }
            catch (Exception error)
            {
                // The rendering path reports module startup errors. This async
                // lifecycle callback must not crash an unloaded/replaced editor.
                System.Diagnostics.Debug.WriteLine(error);
            }
        }
    }

    [JSImport("syncSourceText", "UnoUML.Viewport")]
    private static partial void Sync(string automationId, string json, bool focused, int selectionStart, int selectionLength);

    [JSImport("removeSourceText", "UnoUML.Viewport")]
    private static partial void Remove(string automationId);
}

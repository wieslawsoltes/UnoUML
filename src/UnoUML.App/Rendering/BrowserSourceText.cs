using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace UnoUML.App.Rendering;

/// <summary>Preserves the source editor's leading U+FEFF across the Mono string decoder.</summary>
internal static class BrowserSourceText
{
    private static readonly ConditionalWeakTable<TextBox, Registration> Registrations = new();

    internal static void Attach(TextBox owner)
    {
        if (OperatingSystem.IsBrowser()) Registrations.GetValue(owner, static control => new(control));
    }

    private sealed class Registration
    {
        private readonly TextBox owner;
        private int generation;
        private bool mounted;
        private string Id => owner.Handle.ToInt64().ToString(CultureInfo.InvariantCulture);

        internal Registration(TextBox owner)
        {
            this.owner = owner;
            owner.Loaded += (_, _) => Update();
            owner.Unloaded += (_, _) =>
            {
                generation++;
                if (mounted) { BrowserBridge.RemoveSourceText(Id); mounted = false; }
            };
            owner.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) => Update());
            owner.RegisterPropertyChangedCallback(Control.TemplateProperty, (_, _) => Update());
            Update();
        }

        private async void Update()
        {
            int current = ++generation;
            if (!owner.IsLoaded) return;
            // Ordinary text needs no DOM intervention. Removing a BOM, including
            // a deliberate native edit, also removes the previous expectation.
            if (!owner.Text.StartsWith('\uFEFF'))
            {
                if (mounted) { BrowserBridge.RemoveSourceText(Id); mounted = false; }
                return;
            }
            try
            {
                await BrowserBridge.ReadyAsync();
                if (!owner.IsLoaded || generation != current) return;
                BrowserBridge.SyncSourceText(Id, JsonSerializer.Serialize(owner.Text, BrowserTextJson.Default.String));
                mounted = true;
            }
            catch (Exception error)
            {
                // The viewport reports module-load failures. A superseded or
                // failed startup must not become an async-void exception.
                System.Diagnostics.Debug.WriteLine(error);
            }
        }
    }
}

internal static partial class BrowserBridge
{
    [JSImport("syncSourceText", Module)] internal static partial void SyncSourceText(string id, string json);
    [JSImport("removeSourceText", Module)] internal static partial void RemoveSourceText(string id);
}

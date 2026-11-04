using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.JavaScript;
using Microsoft.UI.Xaml.Controls;

namespace UnoUML.App.Rendering;

/// <summary>Keeps the inspector's managed pointer coordinates aligned with its native browser scroll position.</summary>
internal static partial class BrowserScrollSynchronization
{
    private static readonly ConditionalWeakTable<ScrollViewer, Registration> Registrations = new();

    internal static void Attach(ScrollViewer owner)
    {
        if (OperatingSystem.IsBrowser()) Registrations.GetValue(owner, static control => new(control));
    }

    private sealed class Registration
    {
        private readonly ScrollViewer owner;
        private bool mounted;
        private bool synchronizing;
        private int generation;
        private string Id => owner.Handle.ToInt64().ToString(CultureInfo.InvariantCulture);

        internal Registration(ScrollViewer owner)
        {
            this.owner = owner;
            owner.Loaded += (_, _) => Mount();
            owner.Unloaded += (_, _) =>
            {
                generation++;
                if (mounted) { Remove(Id); mounted = false; }
            };
            if (owner.IsLoaded) Mount();
        }

        private async void Mount()
        {
            if (mounted || !owner.IsLoaded) return;
            int current = ++generation;
            try
            {
                await BrowserBridge.ReadyAsync();
                if (!owner.IsLoaded || generation != current) return;
                Configure(Id, (horizontal, vertical) =>
                {
                    if (generation == current) Synchronize(horizontal, vertical);
                });
                mounted = true;
            }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        }

        private void Synchronize(double horizontal, double vertical)
        {
            if (!mounted || !owner.IsLoaded || synchronizing ||
                !double.IsFinite(horizontal) || !double.IsFinite(vertical) || horizontal < 0 || vertical < 0 ||
                owner.HorizontalOffset == horizontal && owner.VerticalOffset == vertical) return;

            // Uno 6.7.135 can keep CancelNextNativeScroll set after a handled
            // keyboard command. A later native scroll/clamp then leaves its
            // managed ScrollOffsets stale, so coordinate hit testing misses
            // the visible button. ChangeView without animation synchronously
            // reads the native offsets into ScrollOffsets. Use the browser's
            // actual position before the same real pointer event reaches Uno;
            // this neither resets scrolling nor clears Uno's internal latch.
            synchronizing = true;
            try { owner.ChangeView(horizontal, vertical, null, disableAnimation: true); }
            finally { synchronizing = false; }
        }
    }

    [JSImport("configureScrollSynchronization", "UnoUML.Viewport")]
    private static partial void Configure(string id,
        [JSMarshalAs<JSType.Function<JSType.Number, JSType.Number>>] Action<double, double> synchronize);

    [JSImport("removeScrollSynchronization", "UnoUML.Viewport")]
    private static partial void Remove(string id);
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace UnoUML.App;

public sealed partial class MainPage
{
    private MenuFlyout? activeAppMenu;
    private (FrameworkElement Target, Func<MenuFlyout> Create)? pendingAppMenu;
    private bool appMenuOpenQueued, appMenuOwnerObserved;
    private long appMenuLifetime;

    private void ShowAppMenu(FrameworkElement target, Func<MenuFlyout> create)
    {
        if (!IsLoaded || !target.IsLoaded) return;
        if (!appMenuOwnerObserved)
        {
            Unloaded += OnAppMenuOwnerUnloaded;
            appMenuOwnerObserved = true;
        }

        // Keep one request and construct its contents only when it can open.
        // A later activation replaces an earlier request during the same close.
        pendingAppMenu = (target, create);
        if (activeAppMenu is { } current)
        {
            if (current.IsOpen)
            {
                current.Hide();
                // Honor a future Closing cancellation without retaining a
                // surprising delayed request. A later activation can try again.
                if (current.IsOpen) pendingAppMenu = null;
            }
            return;
        }
        QueueAppMenuOpen(null);
    }

    private void OnAppMenuClosed(object? sender, object e)
    {
        if (sender is not MenuFlyout closed) return;
        closed.Closed -= OnAppMenuClosed;
        if (ReferenceEquals(activeAppMenu, closed)) QueueAppMenuOpen(closed);
    }

    private void QueueAppMenuOpen(MenuFlyout? closed)
    {
        if (appMenuOpenQueued) return;
        appMenuOpenQueued = true;
        long lifetime = appMenuLifetime;

        // Uno 7.0.32 FlyoutBase.RemoveFromOpenFlyouts raises Closed and then
        // hides the next open flyout in that same dispatcher callback. Opening
        // inside Closed (or before it) lets that tail dismiss the new menu.
        // Keep the old owner until this continuation runs after the whole close.
        if (DispatcherQueue.TryEnqueue(() =>
        {
            if (lifetime != appMenuLifetime) return;
            appMenuOpenQueued = false;
            if (closed != null)
            {
                if (!ReferenceEquals(activeAppMenu, closed)) return;
                activeAppMenu = null;
            }
            if (activeAppMenu != null || pendingAppMenu is not { } request) return;
            pendingAppMenu = null;
            if (!IsLoaded || !request.Target.IsLoaded) return;

            var menu = request.Create();
            activeAppMenu = menu;
            menu.Closed += OnAppMenuClosed;
            try
            {
                menu.ShowAt(request.Target);
                if (menu.IsOpen) return;
                // An Opening cancellation must not leave a permanent owner.
                menu.Closed -= OnAppMenuClosed;
                activeAppMenu = null;
            }
            catch
            {
                menu.Closed -= OnAppMenuClosed;
                activeAppMenu = null;
                throw;
            }
        })) return;

        appMenuOpenQueued = false;
        pendingAppMenu = null;
        if (ReferenceEquals(activeAppMenu, closed)) activeAppMenu = null;
    }

    private void OnAppMenuOwnerUnloaded(object sender, RoutedEventArgs e)
    {
        appMenuLifetime++;
        appMenuOpenQueued = false;
        pendingAppMenu = null;
        Unloaded -= OnAppMenuOwnerUnloaded;
        appMenuOwnerObserved = false;
        var menu = activeAppMenu;
        activeAppMenu = null;
        if (menu == null) return;
        menu.Closed -= OnAppMenuClosed;
        menu.Hide();
    }
}

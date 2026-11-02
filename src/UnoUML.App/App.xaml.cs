using Microsoft.UI.Xaml;

namespace UnoUML.App;

public sealed partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new Window { Title = "UnoUML Studio", Content = new MainPage() };
        _window.Activate();
    }
}

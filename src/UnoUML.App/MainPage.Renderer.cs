using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UnoUML.App.Rendering;

namespace UnoUML.App;

public sealed partial class MainPage
{
    private string serverEndpoint = "";

    private async void ShowRendererSettings()
    {
        var useServer = new ToggleSwitch { Header = "Use a PlantUML server for this document", IsOn = serverEndpoint.Length > 0 };
        var address = new TextBox { Header = "Server SVG endpoint", PlaceholderText = "https://your-server.example/plantuml/svg", Text = serverEndpoint, IsEnabled = useServer.IsOn };
        useServer.Toggled += (_, _) => address.IsEnabled = useServer.IsOn;
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(Caption("The bundled engine renders diagrams on this device. A JVM server can render additional dialects such as Chen, Ditaa and LaTeX, depending on its configuration."));
        content.Children.Add(useServer);
        content.Children.Add(address);
        content.Children.Add(Caption("When enabled, each preview sends this page’s PlantUML source to the endpoint you enter. Imported include files stay on this device. Opening a different document switches back to local rendering."));
        if (await AskAsync("Rendering engine", content, "Apply") != ContentDialogResult.Primary) return;

        string endpoint = useServer.IsOn ? address.Text.Trim() : "";
        if (useServer.IsOn && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            uri.UserInfo.Length > 0 || uri.Fragment.Length > 0))
        {
            Status("Enter an HTTPS SVG endpoint, or use HTTP on localhost.", true);
            return;
        }
        try
        {
            await BrowserBridge.ReadyAsync();
            BrowserBridge.SetServerEndpoint(endpoint);
            serverEndpoint = endpoint;
            revision++;
            await RenderPageAsync();
        }
        catch (Exception ex) { Status(ex.Message, true); }
    }

    private void ResetRenderer()
    {
        if (serverEndpoint.Length == 0) return;
        BrowserBridge.SetServerEndpoint("");
        serverEndpoint = "";
    }
}

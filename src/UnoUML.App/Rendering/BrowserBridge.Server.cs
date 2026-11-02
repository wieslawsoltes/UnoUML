using System.Runtime.InteropServices.JavaScript;

namespace UnoUML.App.Rendering;

internal static partial class BrowserBridge
{
    [JSImport("setServerEndpoint", Module)]
    internal static partial void SetServerEndpoint(string endpoint, bool encodedUrl);
}

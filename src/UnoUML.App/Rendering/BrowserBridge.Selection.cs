using System.Runtime.InteropServices.JavaScript;

namespace UnoUML.App.Rendering;

internal static partial class BrowserBridge
{
    [JSImport("setAdditionalSelection", Module)]
    internal static partial void SetAdditionalSelection(string host, string lines, string clipboard);

    [JSImport("setAdditionalDotSelection", Module)]
    internal static partial void SetAdditionalDotSelection(string host, string names, string clipboard);
}

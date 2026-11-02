using UnoUML.PlantUml;

namespace UnoUML.App.Rendering;

public sealed partial class DiagramViewport
{
    public string ActiveRenderer { get; private set; } = "";
    public bool NativeEnabled { get; private set; } = true;

    public void SetNativeEnabled(bool enabled)
    {
        NativeEnabled = enabled;
        if (mounted) BrowserBridge.SetLocalMode(HostId, enabled);
    }

    private static string RenderManaged(string source) =>
        PlantUmlRenderer.TryRender(source, out var diagram) ? diagram.Svg : "";
}

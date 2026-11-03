using UnoUML.PlantUml;

namespace UnoUML.App.Rendering;

public sealed partial class DiagramViewport
{
    public string ActiveRenderer { get; private set; } = "";
    public bool NativeEnabled { get; private set; } = true;
    public string? SourceFileName { get; set; }

    public void SetNativeEnabled(bool enabled)
    {
        NativeEnabled = enabled;
        if (mounted) BrowserBridge.SetLocalMode(HostId, enabled);
    }

    private string RenderManaged(string source) =>
        PlantUmlRenderer.TryRender(source, out var diagram, new PlantUmlRenderOptions
        {
            ResolveInclude = BrowserBridge.ReadResource,
            ImportLibrary = BrowserIncludes.ImportLibrary,
            SourceFileName = SourceFileName
        }) ? diagram.Svg : "";
}

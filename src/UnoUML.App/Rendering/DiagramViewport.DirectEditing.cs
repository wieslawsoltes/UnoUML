using System.Runtime.InteropServices.JavaScript;

namespace UnoUML.App.Rendering;

public sealed partial class DiagramViewport
{
    public event Action<int, string>? LabelCommitted;
    public event Action<DiagramHit[], bool>? RegionSelected;

    public void SetConnectable(bool value)
    {
        if (mounted) BrowserBridge.SetConnectable(HostId, value);
    }

    public bool BeginLabelEdit(string label, int revision) =>
        mounted && BrowserBridge.BeginLabelEdit(HostId, label, revision);
}

internal static partial class BrowserBridge
{
    [JSImport("beginLabelEdit", Module)]
    internal static partial bool BeginLabelEdit(string id, string label, int revision);

    [JSImport("setConnectable", Module)]
    internal static partial void SetConnectable(string id, bool value);
}

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

    public void SetEndpoints(string from, string to)
    {
        if (mounted) BrowserBridge.SetEndpoints(HostId, from, to);
    }

    public void SelectJsonToken(int start) { if (mounted) BrowserBridge.SelectJsonToken(HostId, start); }

    public bool BeginLabelEdit(string label, int revision, bool multiline = false, string language = "text") =>
        mounted && BrowserBridge.BeginLabelEdit(HostId, label, revision, multiline, language);
}

internal static partial class BrowserBridge
{
    [JSImport("beginLabelEdit", Module)]
    internal static partial bool BeginLabelEdit(string id, string label, int revision, bool multiline, string language);

    [JSImport("selectJsonToken", Module)]
    internal static partial void SelectJsonToken(string host, int start);

    [JSImport("setEndpoints", Module)]
    internal static partial void SetEndpoints(string id, string from, string to);

    [JSImport("setConnectable", Module)]
    internal static partial void SetConnectable(string id, bool value);
}

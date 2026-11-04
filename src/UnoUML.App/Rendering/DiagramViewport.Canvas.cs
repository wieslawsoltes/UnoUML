using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using UnoUML.Core;

namespace UnoUML.App.Rendering;

public sealed record CanvasViewportInfo(CanvasLayoutState State, string[] Nodes, string[] Edges);
public sealed record CanvasToolDrop(string Text, double X, double Y, int Revision);

public sealed partial class DiagramViewport
{
    public event Action<int, string, bool>? CanvasLayoutCommitted;
    public event Action<CanvasToolDrop>? CanvasToolDropped;
    public event Action<string, bool>? CanvasStatus;
    public void CompleteCanvasLayoutCommit(bool accepted) { if (mounted) BrowserBridge.CanvasCommitResult(HostId, accepted); }

    public void SetCanvasLayout(CanvasLayoutState? layout, int revision)
    {
        if (mounted) BrowserBridge.SetCanvasState(HostId, layout == null ? "" : CanvasLayoutJson.Serialize(layout), revision);
    }

    public CanvasViewportInfo? GetCanvasInfo()
    {
        if (!mounted) return null;
        using var json = JsonDocument.Parse(BrowserBridge.GetCanvasState(HostId));
        var root = json.RootElement;
        if (!root.TryGetProperty("state", out var state) || state.ValueKind == JsonValueKind.Null ||
            !CanvasLayoutJson.TryParse(state.GetRawText(), out var layout, out _)) return null;
        string[] Read(string name) => root.TryGetProperty(name, out var value) ? value.EnumerateArray().Select(e => e.GetString()!).ToArray() : [];
        return new(layout!, Read("nodes"), Read("edges"));
    }

    public void ArrangeCanvas(string action, string options = "{}")
    {
        if (mounted) BrowserBridge.CanvasAction(HostId, action, options);
    }

    // The browser previews pointer movement; the same bounded C# geometry engine
    // handles committed gestures, keyboard operations and automatic layout.
    private static string ProcessCanvasLayout(string request)
    {
        try
        {
            if (request.Length > CanvasLayoutLimits.MaxMetadataCharacters * 2 + 4096)
                throw new ArgumentException("Canvas request exceeds its size limit.");
            using var json = JsonDocument.Parse(request, new JsonDocumentOptions { MaxDepth = 36 });
            var root = json.RootElement;
            if (!CanvasLayoutJson.TryParse(root.GetProperty("state").GetRawText(), out var state, out var error))
                throw new ArgumentException(error);
            string[]? selection = root.TryGetProperty("selection", out var rawSelection) && rawSelection.ValueKind != JsonValueKind.Null ?
                rawSelection.EnumerateArray().Select(e => e.GetString()!).ToArray() : null;
            string value = root.TryGetProperty("value", out var rawValue) ? rawValue.GetString() ?? "" : "";
            string? reference = root.TryGetProperty("reference", out var rawReference) ? rawReference.GetString() : null;
            T Parse<T>() where T : struct, Enum => Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed) ? parsed :
                throw new ArgumentException("Unknown arrangement option.");
            CanvasLayoutState Geometry()
            {
                if (!CanvasLayoutJson.TryParse(root.GetProperty("before").GetRawText(), out var before, out var reason)) throw new ArgumentException(reason);
                return CanvasLayoutEngine.UpdateGeometry(before!, state!, allowOverlapFallback: true);
            }
            var result = root.GetProperty("action").GetString() switch
            {
                "validate" => state!,
                "layout" => CanvasLayoutEngine.Layout(state!, selection: selection),
                "align" => CanvasLayoutEngine.Align(state!, Parse<CanvasAlignment>(), selection ?? [], reference),
                "distribute" => CanvasLayoutEngine.Distribute(state!, Parse<CanvasAxis>(), selection ?? []),
                "matchSize" => CanvasLayoutEngine.MatchSize(state!, Parse<CanvasSize>(), selection ?? [], reference),
                "route" => CanvasLayoutEngine.RouteEdges(state!, selection),
                "resetRoutes" => CanvasLayoutEngine.ResetRoutes(state!, selection),
                "geometry" => Geometry(),
                _ => throw new ArgumentException("Unknown canvas operation.")
            };
            return CanvasLayoutJson.Serialize(result);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException or FormatException or OverflowException)
        {
            return "{\"error\":\"" + JsonEncodedText.Encode(ex.Message) + "\"}";
        }
    }
}

internal static partial class BrowserBridge
{
    [JSImport("setCanvasState", Module)] internal static partial void SetCanvasState(string id, string json, int revision);
    [JSImport("getCanvasState", Module)] internal static partial string GetCanvasState(string id);
    [JSImport("canvasAction", Module)] internal static partial void CanvasAction(string id, string action, string options);
    [JSImport("canvasCommitResult", Module)] internal static partial void CanvasCommitResult(string id, bool accepted);
}

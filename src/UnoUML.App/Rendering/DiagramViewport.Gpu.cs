using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UnoUML.App.Rendering.Gpu;

namespace UnoUML.App.Rendering;

public sealed partial class DiagramViewport
{
    private readonly DiagramGpuCanvas gpuCanvas = new() { IsHitTestVisible = false };

    /// <summary>The presentation backend, independent from the PlantUML layout engine.</summary>
    public string PresentationBackend { get; private set; } = "SVG";
    public string PresentationFallbackReason { get; private set; } = "";
    public event Action<string>? PresentationChanged;

    private Grid CreatePresentationLayers()
    {
        var layers = new Grid();
        layers.Children.Add(gpuCanvas);
        // BrowserHtmlElement is native content. Keep its presenter above the shared-device
        // GPU canvas so its transparent semantic projection receives native pointer/IME input.
        layers.Children.Add(new ContentControl
        {
            Content = element,
            IsTabStop = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        });
        gpuCanvas.Available += OnGpuAvailable;
        gpuCanvas.Unavailable += OnGpuUnavailable;
        gpuCanvas.SceneRejected += OnGpuSceneRejected;
        gpuCanvas.FramePresented += OnGpuFramePresented;
        return layers;
    }

    private void SyncGpuAvailability()
    {
        if (mounted)
            BrowserBridge.SetGpuAvailability(HostId, gpuCanvas.IsAvailable, PresentationFallbackReason);
    }

    private void OnGpuAvailable()
    {
        PresentationFallbackReason = "";
        SyncGpuAvailability();
    }

    private void OnGpuUnavailable(string reason)
    {
        PresentationFallbackReason = reason;
        if (mounted) BrowserBridge.SetGpuAvailability(HostId, false, reason);
        SetPresentation("SVG", reason);
    }

    private void OnGpuSceneRejected(string reason)
    {
        if (mounted) BrowserBridge.RejectGpuScene(HostId, reason);
        SetPresentation("SVG", reason);
    }

    private void OnGpuFramePresented(long revision)
    {
        if (!mounted || !gpuCanvas.IsAvailable) return;
        // The browser switches off SVG paint only if this exact transaction still owns
        // the installed source/geometry. A delayed GPU frame must never hide newer edits.
        BrowserBridge.PresentGpuFrame(HostId, (double)revision,
            JsonSerializer.Serialize(gpuCanvas.Statistics, DiagramGpuJson.Default.DiagramGpuStatistics));
    }

    private void SetPresentation(string backend, string reason)
    {
        var displayName = backend.Equals("webgpu", StringComparison.OrdinalIgnoreCase) ? "WebGPU" : "SVG";
        bool changed = PresentationBackend != displayName || PresentationFallbackReason != reason;
        PresentationBackend = displayName;
        PresentationFallbackReason = reason;
        if (changed) PresentationChanged?.Invoke(displayName);
    }
}

internal static partial class BrowserBridge
{
    [JSImport("setGpuAvailability", Module)]
    internal static partial void SetGpuAvailability(string id, bool available, string reason);

    [JSImport("rejectGpuScene", Module)]
    internal static partial void RejectGpuScene(string id, string reason);

    [JSImport("presentGpuFrame", Module)]
    internal static partial void PresentGpuFrame(string id, double revision, string statisticsJson);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DiagramGpuStatistics))]
internal partial class DiagramGpuJson : JsonSerializerContext { }

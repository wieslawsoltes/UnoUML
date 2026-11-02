using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Uno.UI.NativeElementHosting;

namespace UnoUML.App.Rendering;

public sealed record DiagramHit(string Kind, string Id, int Line, string Label);
public sealed record DiagramGesture(string Kind, DiagramHit From, DiagramHit To, string Direction = "");

public sealed partial class DiagramViewport : ContentControl
{
    private readonly BrowserHtmlElement element;
    private TaskCompletionSource ready = new();
    private string tool = "select";
    private bool mounted;
    private (string Id, int Line) selection = ("", -1);
    private string HostId => element.GetHtmlId();

    public DiagramViewport()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        IsTabStop = false;
        element = BrowserHtmlElement.CreateHtmlElement("div");
        element.SetCssStyle(("width", "100%"), ("height", "100%"), ("overflow", "hidden"));
        Content = element;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event Action<string, string>? CanvasCommand;
    public void SetAdditionalSelection(string lines, string clipboard)
    {
        if (mounted) BrowserBridge.SetAdditionalSelection(HostId, lines, clipboard);
    }

    public event EventHandler<DiagramHit>? Hit;
    public event EventHandler<DiagramGesture>? Gesture;
    public event EventHandler<string>? DropSnippet;
    public event EventHandler<double>? ZoomChanged;
    public double Zoom { get; private set; } = 1;

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        var generation = ready;
        try
        {
            await BrowserBridge.ReadyAsync();
            if (generation != ready || mounted) return;
            BrowserBridge.Mount(HostId, Receive);
            mounted = true;
            BrowserBridge.SetTool(HostId, tool);
            generation.TrySetResult();
        }
        catch (Exception error) { generation.TrySetException(error); }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (mounted) BrowserBridge.Unmount(HostId);
        mounted = false;
        ready.TrySetCanceled();
        ready = new();
    }

    public async Task RenderAsync(string source)
    {
        await ready.Task;
        await BrowserBridge.Render(HostId, source);
        if (mounted) BrowserBridge.SetSelection(HostId, selection.Id, selection.Line);
    }

    public void SetSelection(string id, int line = -1)
    {
        selection = (id, line);
        if (mounted) BrowserBridge.SetSelection(HostId, id, line);
    }
    public void SetTool(string value)
    {
        tool = value;
        if (mounted) BrowserBridge.SetTool(HostId, value);
    }
    public void ZoomBy(double factor) { if (mounted) BrowserBridge.Zoom(HostId, factor); }
    public void Fit() { if (mounted) BrowserBridge.Fit(HostId); }
    public async Task ExportAsync(string format, string fileName)
    {
        await ready.Task;
        await BrowserBridge.Export(HostId, format, fileName);
    }

    private void Receive(string json)
    {
        using var payload = JsonDocument.Parse(json);
        var data = payload.RootElement;
        switch (data.GetProperty("kind").GetString())
        {
            case "command":
                CanvasCommand?.Invoke(data.GetProperty("action").GetString()!, data.GetProperty("text").GetString() ?? "");
                break;
            case "zoom":
                Zoom = data.GetProperty("zoom").GetDouble();
                ZoomChanged?.Invoke(this, Zoom);
                break;
            case "hit":
                Hit?.Invoke(this, ReadHit(data, data.GetProperty("action").GetString()!));
                break;
            case "gesture":
                Gesture?.Invoke(this, new(data.GetProperty("action").GetString()!,
                    ReadHit(data.GetProperty("from"), "select"), ReadHit(data.GetProperty("to"), "select"),
                    data.TryGetProperty("direction", out var direction) ? direction.GetString() ?? "" : ""));
                break;
            case "drop":
                DropSnippet?.Invoke(this, data.GetProperty("text").GetString()!);
                break;
        }
    }

    private static DiagramHit ReadHit(JsonElement el, string kind) =>
        new(kind, el.GetProperty("id").GetString() ?? "", el.GetProperty("line").GetInt32(), el.GetProperty("label").GetString() ?? "");
}

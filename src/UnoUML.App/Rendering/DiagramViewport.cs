using System.Text.Json;
using UnoUML.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Uno.UI.NativeElementHosting;

namespace UnoUML.App.Rendering;

public sealed record JsonHit(int Start, int Length, string Kind);
public sealed record SourceFieldHit(int Start, int Length, string Kind, string Role);
public sealed record DiagramHit(string Kind, string Id, int Line, string Label, bool Participant = false, JsonHit? Token = null, bool Generated = false,
    string? SourceFile = null, int FileLine = -1, string? SourceArchive = null, SourceFieldHit? Field = null, DotSourceLocation? Dot = null, bool VisualOnly = false);
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
        Content = CreatePresentationLayers();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event Action<string, string>? CanvasCommand;
    public void SetAdditionalSelection(string lines, string clipboard)
    {
        if (mounted) BrowserBridge.SetAdditionalSelection(HostId, lines, clipboard);
    }

    public void SetAdditionalDotSelection(string names, string clipboard)
    {
        if (mounted) BrowserBridge.SetAdditionalDotSelection(HostId, names, clipboard);
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
            BrowserBridge.Mount(HostId, Receive, RenderManaged, ProcessCanvasLayout);
            BrowserBridge.SetLocalMode(HostId, NativeEnabled);
            mounted = true;
            SyncGpuAvailability();
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
        await BrowserBridge.Render(HostId, source, SourceFileName);
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
        if (json.Length > Gpu.GpuSceneModel.MaxJsonCharacters)
        {
            OnGpuSceneRejected("The GPU bridge payload exceeds the bounded transport budget.");
            return;
        }
        using var payload = JsonDocument.Parse(json);
        var data = payload.RootElement;
        switch (data.GetProperty("kind").GetString())
        {
            case "gpuScene":
                if (mounted) gpuCanvas.ApplyUpdate(data.GetProperty("update"));
                break;
            case "presentation":
                SetPresentation(data.GetProperty("backend").GetString() ?? "svg",
                    data.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "" : "");
                break;
            case "canvasLayout":
                CanvasLayoutCommitted?.Invoke(data.GetProperty("revision").GetInt32(), data.GetProperty("json").GetString() ?? "null",
                    data.TryGetProperty("merge", out var merge) && merge.GetBoolean());
                break;
            case "canvasStatus":
                CanvasStatus?.Invoke(data.GetProperty("text").GetString() ?? "", data.GetProperty("error").GetBoolean());
                break;
            case "label":
                LabelCommitted?.Invoke(data.GetProperty("revision").GetInt32(), data.GetProperty("text").GetString() ?? "");
                break;
            case "region":
                var hits = data.GetProperty("hits").EnumerateArray().Select(el => ReadHit(el, "select")).ToArray();
                RegionSelected?.Invoke(hits, data.GetProperty("add").GetBoolean());
                break;
            case "command":
                CanvasCommand?.Invoke(data.GetProperty("action").GetString()!, data.GetProperty("text").GetString() ?? "");
                break;
            case "renderer":
                ActiveRenderer = data.GetProperty("backend").GetString() switch
                {
                    "managed" => "C#",
                    "jvm" => "JVM",
                    _ => "PlantUML JS"
                };
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
                if (data.TryGetProperty("x", out var x) && data.TryGetProperty("y", out var y))
                    CanvasToolDropped?.Invoke(new(data.GetProperty("text").GetString()!, x.GetDouble(), y.GetDouble(), data.GetProperty("revision").GetInt32()));
                else DropSnippet?.Invoke(this, data.GetProperty("text").GetString()!);
                break;
        }
    }

    private static DiagramHit ReadHit(JsonElement el, string kind) =>
        new(kind, el.GetProperty("id").GetString() ?? "", el.GetProperty("line").GetInt32(), el.GetProperty("label").GetString() ?? "",
            el.TryGetProperty("participant", out var participant) && participant.GetBoolean(),
            el.TryGetProperty("token", out var t) ? new JsonHit(t.GetProperty("start").GetInt32(), t.GetProperty("length").GetInt32(), t.GetProperty("kind").GetString()!) : null,
            el.TryGetProperty("generated", out var generated) && generated.GetBoolean(),
            el.TryGetProperty("sourceFile", out var file) ? file.GetString() : null,
            el.TryGetProperty("fileLine", out var line) && line.TryGetInt32(out int fileLine) ? fileLine : -1,
            el.TryGetProperty("sourceArchive", out var archive) ? archive.GetString() : null,
            el.TryGetProperty("field", out var field) ? new SourceFieldHit(field.GetProperty("start").GetInt32(), field.GetProperty("length").GetInt32(),
                field.GetProperty("kind").GetString()!, field.GetProperty("role").GetString()!) : null,
            el.TryGetProperty("dot", out var dot) ? ReadDot(dot) : null,
            el.TryGetProperty("visualOnly", out var visualOnly) && visualOnly.GetBoolean());

    private static DotSourceLocation ReadDot(JsonElement value)
    {
        string Text(string name) => value.TryGetProperty(name, out var property) ? property.GetString() ?? "" : "";
        int Number(string name) => value.TryGetProperty(name, out var property) && property.TryGetInt32(out int number) ? number : -1;
        return new()
        {
            Id = Text("id"), Kind = Text("kind"), Name = Text("name"), Text = Text("text"), Format = Text("format"),
            Start = Number("start"), Length = Number("length"), NameStart = Number("nameStart"), NameLength = Number("nameLength"),
            LabelStart = Number("labelStart"), LabelLength = Number("labelLength"), LabelInsert = Number("labelInsert"), LabelKind = Text("labelKind"),
            Explicit = value.TryGetProperty("explicit", out var declared) && declared.GetBoolean(),
            Directed = value.TryGetProperty("directed", out var directed) && directed.GetBoolean(),
            Merged = value.TryGetProperty("merged", out var merged) && merged.GetBoolean(),
            FromStart = Number("fromStart"), FromLength = Number("fromLength"), FromId = Text("fromId"),
            ToStart = Number("toStart"), ToLength = Number("toLength"), ToId = Text("toId"),
            ArrowStart = Number("arrowStart"), ArrowLength = Number("arrowLength")
        };
    }
}

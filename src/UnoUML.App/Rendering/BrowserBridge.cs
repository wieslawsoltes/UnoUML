using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace UnoUML.App.Rendering;

internal static partial class BrowserBridge
{
    private const string Module = "UnoUML.Viewport";
    private static Task<JSObject>? import;

    internal static async Task ReadyAsync()
    {
        if (!OperatingSystem.IsBrowser()) throw new PlatformNotSupportedException("The diagram viewport requires a browser.");
        if (import is not null) { await import; return; }
        using var doc = JSHost.GlobalThis.GetPropertyAsJSObject("document")!;
        var url = new Uri(new Uri(doc.GetPropertyAsString("baseURI")!), "uml/viewport.js");
        var attempt = import ??= JSHost.ImportAsync(Module, url.AbsoluteUri);
        try { await attempt; }
        catch { if (ReferenceEquals(import, attempt)) import = null; throw; }
    }

    [JSImport("readInclude", Module)] internal static partial string? ReadInclude(string path);
    [JSImport("readResource", Module)] internal static partial string? ReadResource(string path);
    [JSImport("mount", Module)]
    internal static partial void Mount(string id, [JSMarshalAs<JSType.Function<JSType.String>>] Action<string> receive,
        [JSMarshalAs<JSType.Function<JSType.String, JSType.String>>] Func<string, string> renderManaged,
        [JSMarshalAs<JSType.Function<JSType.String, JSType.String>>] Func<string, string> layoutManaged);
    [JSImport("setLocalMode", Module)] internal static partial void SetLocalMode(string id, bool nativeEnabled);
    [JSImport("unmount", Module)] internal static partial void Unmount(string id);
    [JSImport("render", Module)] internal static partial Task Render(string id, string source, string? fileName);
    [JSImport("setTool", Module)] internal static partial void SetTool(string id, string tool);
    [JSImport("setSelection", Module)] internal static partial void SetSelection(string id, string name, int line);
    [JSImport("zoom", Module)] internal static partial void Zoom(string id, double factor);
    [JSImport("fit", Module)] internal static partial void Fit(string id);
    [JSImport("exportDiagram", Module)] internal static partial Task Export(string id, string format, string fileName);
    [JSImport("openFile", Module)] internal static partial Task<string?> Open(string accept);
    [JSImport("saveFile", Module)] internal static partial void Save(string fileName, string text, string mime);
    [JSImport("importIncludes", Module)] internal static partial Task<int> ImportIncludes(bool folder);

    [JSImport("globalThis.localStorage.getItem")] internal static partial string? StorageGet(string key);
    [JSImport("globalThis.localStorage.setItem")] internal static partial void StorageSet(string key, string value);
    [JSImport("globalThis.localStorage.removeItem")] internal static partial void StorageRemove(string key);
}

public sealed record OpenedFile(string Name, string Text);

public static class BrowserFiles
{
    public static async Task<OpenedFile?> OpenAsync(string accept = ".puml,.plantuml,.pu,.uml,.iuml,.txt")
    {
        await BrowserBridge.ReadyAsync();
        var json = await BrowserBridge.Open(accept);
        if (json is null) return null;
        using var data = JsonDocument.Parse(json);
        return new(data.RootElement.GetProperty("name").GetString()!, data.RootElement.GetProperty("text").GetString()!);
    }

    public static async Task SaveAsync(string fileName, string text, string mime = "text/plain;charset=utf-8")
    {
        await BrowserBridge.ReadyAsync();
        BrowserBridge.Save(fileName, text, mime);
    }
    public static string? ReadDraft(string key) => BrowserBridge.StorageGet("UnoUML:" + key);
    public static void WriteDraft(string key, string value) => BrowserBridge.StorageSet("UnoUML:" + key, value);
    public static void RemoveDraft(string key) => BrowserBridge.StorageRemove("UnoUML:" + key);
    public static Task<int> ImportIncludesAsync() => ImportAsync(false);
    public static Task<int> ImportIncludeFolderAsync() => ImportAsync(true);
    private static async Task<int> ImportAsync(bool folder)
    {
        await BrowserBridge.ReadyAsync();
        return await BrowserBridge.ImportIncludes(folder);
    }
}

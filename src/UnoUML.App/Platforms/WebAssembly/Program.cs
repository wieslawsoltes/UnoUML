using Microsoft.Extensions.Logging;
using Uno.UI.Composition.Skia;
using Uno.UI.Composition.WebGpu;
using Uno.UI.Hosting;

namespace UnoUML.App;

internal static class Program
{
    // The factory lives for the browser application's lifetime. RunAsync may
    // complete after startup, so disposing it there would suppress later failures.
    private static readonly ILoggerFactory Logging = LoggerFactory.Create(builder =>
    {
        builder.AddProvider(new Uno.Extensions.Logging.WebAssembly.WebAssemblyConsoleLoggerProvider());
        builder.SetMinimumLevel(LogLevel.Warning);
        builder.AddFilter("Uno.UI.Composition.Drawing", LogLevel.Information);
        builder.AddFilter("UnoUML.Rendering", LogLevel.Information);
    });

    public static Task Main()
    {
        Uno.Extensions.LogExtensionPoint.AmbientLoggerFactory = Logging;
        Uno.UI.Adapter.Microsoft.Extensions.Logging.LoggingAdapter.Initialize();

        // Uno 7 renders XAML through composition. Its semantic DOM supplies
        // accessible controls and automation without the removed native renderer.
        Uno.UI.FeatureConfiguration.AutomationPeer.AutoEnableAccessibility = true;
        Logging.CreateLogger("UnoUML.Rendering").LogInformation(
            "Graphics preference: WebGPU, then Skia. Uno.UI.Composition.Drawing reports the selected backend.");

        return UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .GraphicsBackend(WebGpuBackend.CreateGraphicsProvider())
            .GraphicsBackend(SkiaBackend.CreateGraphicsProvider())
            .UseWebAssembly()
            .Build()
            .RunAsync();
    }
}

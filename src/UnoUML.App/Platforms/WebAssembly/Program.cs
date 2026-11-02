using Uno.UI.Hosting;

namespace UnoUML.App;

internal static class Program
{
    public static Task Main() => UnoPlatformHostBuilder.Create()
        .App(() => new App())
        .UseWebAssembly()
        .Build()
        .RunAsync();
}

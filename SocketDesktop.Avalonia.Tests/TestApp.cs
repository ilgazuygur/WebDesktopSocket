using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using SocketDesktop.Avalonia.Tests;

// Registers the Avalonia headless test application. Avalonia.Headless.XUnit
// uses this to spin up a windowless Avalonia runtime so [AvaloniaFact]
// tests can create and inspect real controls without a display.
[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace SocketDesktop.Avalonia.Tests;

// A minimal Application for tests - it loads the Fluent theme (so controls
// render) but deliberately does NOT run the real app's composition root
// (which would open a real WebSocket connection). Tests construct the
// MainWindow with their own view model instead.
public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Hutu.Avalonia.Tests.TestAppBuilder))]

namespace Hutu.Avalonia.Tests;

/// <summary>The application the headless sessions run under, themed so the real templates are used.</summary>
public sealed class TestApp : Application
{
    public override void Initialize()
    {
        // ScrollViewer gets its template - and with it the extent and the scrollbars these tests
        // assert on - from a theme, so the theme is part of the harness rather than a detail.
        Styles.Add(new FluentTheme());
    }
}

/// <summary>
/// The headless entry point, and the only place the rendering stack is configured.
/// </summary>
/// <remarks>
/// It is deliberately a separate type from <see cref="TestApp"/>: <c>AppBuilder.Configure(Type)</c>
/// resolves either a static <c>BuildAvaloniaApp</c> method or an <see cref="Application"/> subclass,
/// and handing it a type that is both would leave the intent ambiguous.
/// </remarks>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<TestApp>()
            // Real pixels. Without UseSkia and UseHeadlessDrawing = false the headless platform
            // substitutes a stub render interface: nothing is drawn, Paint produces no commands, and
            // CaptureRenderedFrame has no frame to hand back. The session keeps these options only
            // because they are set here - it forces its own defaults onto an entry point that has
            // not already named the "Headless" windowing subsystem.
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false
            });
    }
}

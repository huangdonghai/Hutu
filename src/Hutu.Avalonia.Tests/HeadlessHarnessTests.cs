using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NUnit.Framework;
using System.Runtime.InteropServices;

namespace Hutu.Avalonia.Tests;

/// <summary>
/// Guards the harness itself rather than the product.
/// </summary>
/// <remarks>
/// Real pixels exist only because the entry point asks for them - <c>UseSkia()</c> plus
/// <c>UseHeadlessDrawing = false</c>. Without those the headless platform substitutes a stub render
/// interface that draws nothing and reports no frame, which would leave any drawing assertion either
/// vacuously true or failing for a reason that has nothing to do with what it is testing. Losing
/// that configuration is a silent, easy mistake, so it gets a test of its own.
/// </remarks>
[TestFixture]
public sealed class HeadlessHarnessTests
{
    private const int FrameSize = 100;

    [AvaloniaTest]
    public void TheHarnessRendersRealPixels()
    {
        var window = new Window
        {
            Width = FrameSize,
            Height = FrameSize,
            Content = new Border { Background = Brushes.Red }
        };

        try
        {
            window.Show();

            // This drains the dispatcher and ticks the render timer, so the frame it hands back is
            // the one the content was drawn into.
            var frame = window.CaptureRenderedFrame();

            Assert.That(
                frame,
                Is.Not.Null,
                "no frame was produced: the platform is drawing through the stub render interface, " +
                "which means UseSkia() or UseHeadlessDrawing = false was lost from BuildAvaloniaApp");

            Assert.That(frame!.PixelSize.Width, Is.GreaterThan(0));
            Assert.That(frame.PixelSize.Height, Is.GreaterThan(0));

            Assert.That(
                CenterPixelAlpha(frame),
                Is.EqualTo(255),
                "a frame came back but the window's own content is transparent in it");
        }
        finally
        {
            window.Close();
        }
    }

    private static byte CenterPixelAlpha(WriteableBitmap frame)
    {
        using var buffer = frame.Lock();

        var x = buffer.Size.Width / 2;
        var y = buffer.Size.Height / 2;
        var pixel = buffer.Address + (y * buffer.RowBytes) + (x * 4);

        // Alpha sits in the same byte for both BGRA8888 and RGBA8888, so this reads the same either
        // way round and the test does not pin down a channel order it does not care about.
        return Marshal.ReadByte(pixel, 3);
    }
}

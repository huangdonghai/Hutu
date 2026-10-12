using AngleSharp.Dom;
using Hutu.Rendering;
using Hutu.Rendering.Media;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Regression coverage for background values. A page whose background layers omit
/// <c>position</c>/<c>size</c>/<c>repeat</c> used to make the style computation itself throw,
/// which took the whole layout pass down with it.
/// </summary>
[TestFixture]
public sealed class BackgroundLayerTests
{
    [Test]
    public async Task MultiLayerBackgroundWithImageAndGradientDoesNotThrow()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:50px;background:linear-gradient(90deg,rgba(15,23,42,.72),rgba(15,23,42,.08)),url(Images/landscape.svg) center / cover no-repeat'></div>" +
            "</body></html>");

        Assert.That(view.Layout, Is.Not.Null);
    }

    [Test]
    public async Task BackgroundImageWithoutPositionSizeOrRepeatDoesNotThrow()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:50px;background-image:url(Images/landscape.svg)'></div>" +
            "</body></html>");

        Assert.That(view.Layout, Is.Not.Null);
    }

    [Test]
    public async Task FirstDeclaredLayerIsPaintedLastSoItSitsOnTop()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:50px;background:linear-gradient(90deg,rgba(15,23,42,.72),rgba(15,23,42,.08)),url(Images/landscape.svg) center / cover no-repeat'></div>" +
            "</body></html>");

        var commands = view.Paint!.Commands;
        var image = commands.FindIndex(command => command is BackgroundImageCommand);
        var gradient = commands.FindIndex(command => command is LinearGradientCommand);

        Assert.That(image, Is.GreaterThanOrEqualTo(0), "the image layer should be painted");
        Assert.That(gradient, Is.GreaterThan(image), "the gradient is declared first, so it paints last (on top)");
    }

    [Test]
    public async Task UnsupportedGradientDegradesToSkippingTheLayer()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:50px;background:radial-gradient(circle,#ffffff,#000000)'></div>" +
            "</body></html>");

        Assert.That(view.Paint!.Commands.OfType<LinearGradientCommand>(), Is.Empty);
    }

    [Test]
    public async Task BackgroundColourOnlyStillWorks()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:50px;background:#f8fafc'></div>" +
            "</body></html>");

        Assert.That(view.Layout, Is.Not.Null);
    }
}

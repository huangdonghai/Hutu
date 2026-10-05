using AngleSharp.Css.RenderTree;
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

        Assert.That(view.RenderTree, Is.Not.Null, "the render tree has to survive a layered background");
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

        Assert.That(view.RenderTree, Is.Not.Null);
    }

    [Test]
    public async Task LayeredBackgroundParsesIntoOrderedLayers()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:50px;background:linear-gradient(90deg,rgba(15,23,42,.72),rgba(15,23,42,.08)),url(Images/landscape.svg) center / cover no-repeat'></div>" +
            "</body></html>");

        var element = view.Document!.QuerySelector("div")!;
        var node = FindRenderNode(view.RenderTree, element)!;
        var style = new ComputedStyle(view.GetComputedStyle(element), new HutuRenderDevice(400, 300), node.SpecifiedStyle);
        var layers = style.Backgrounds;

        Assert.That(layers, Has.Count.EqualTo(2));

        var gradient = layers[0].Gradient;

        Assert.That(gradient, Is.Not.Null, "the first declared layer is the gradient");
        Assert.That(gradient!.AngleDegrees, Is.EqualTo(90).Within(0.1));
        Assert.That(gradient.Stops, Has.Count.EqualTo(2));

        var first = gradient.Stops[0].Color;
        Assert.That((first >> 16) & 0xFF, Is.EqualTo(15));
        Assert.That((first >> 8) & 0xFF, Is.EqualTo(23));
        Assert.That(first & 0xFF, Is.EqualTo(42));
        Assert.That(first >> 24, Is.EqualTo(184).Within(1), "rgba(...,.72) should carry alpha");

        Assert.That(layers[1].Gradient, Is.Null);
        Assert.That(layers[1].Url, Does.EndWith("Images/landscape.svg"));
        Assert.That(layers[1].SizeKind, Is.EqualTo(BackgroundLayerSizeKind.Cover));
        Assert.That(layers[1].PositionX.Value, Is.EqualTo(0.5).Within(0.01));
        Assert.That(layers[1].PositionY.Value, Is.EqualTo(0.5).Within(0.01));
        Assert.That(layers[1].Repeat, Is.False, "no-repeat was declared for this layer");
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
        Assert.That(view.RenderTree, Is.Not.Null, "an unsupported gradient must not fail the page");
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

        Assert.That(view.RenderTree, Is.Not.Null);
    }

    private static ElementRenderNode? FindRenderNode(IRenderNode? node, IElement element)
    {
        if (node is not ElementRenderNode current)
        {
            return null;
        }

        if (ReferenceEquals(current.Ref, element))
        {
            return current;
        }

        foreach (var child in current.Children)
        {
            var found = FindRenderNode(child, element);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}

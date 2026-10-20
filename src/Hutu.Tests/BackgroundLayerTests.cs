using AngleSharp.Dom;
using Hutu.Rendering;
using Hutu.Rendering.Layout;
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

    /// <summary>
    /// A gradient layer takes the size, the place and the repeat its declaration states. A gradient
    /// carries no intrinsic size, so an axis it leaves <c>auto</c> is filled by the positioning area
    /// (CSS Images 3 §4.1.3, read with CSS Backgrounds 3 §2.4).
    /// </summary>
    [Test]
    [TestCase("0 0/20px 20px no-repeat", 0, 0, 20, 20)]
    [TestCase("10px 5px/20px 30px no-repeat", 10, 5, 20, 30)]
    [TestCase("0 0/20px no-repeat", 0, 0, 20, 50)]
    [TestCase("0 0", 0, 0, 100, 50)]
    [TestCase("right bottom/20px 20px no-repeat", 80, 30, 20, 20)]
    [TestCase("50% 25%/25% 50% no-repeat", 37.5, 6.25, 25, 25)]
    public async Task AGradientLayerTakesTheSizeItStates(
        String declaration,
        Double x,
        Double y,
        Double width,
        Double height)
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            $"<div style='width:100px;height:50px;background:linear-gradient(red,red) {declaration}'></div>" +
            "</body></html>");

        var layer = SingleGradient(view);

        AssertTile(layer.Tile, x, y, width, height, declaration);
        Assert.That(layer.Bounds, Is.EqualTo(new Rect(0, 0, 100, 50)),
            "the layer is clipped to the positioning area wherever its tile stands");
    }

    [Test]
    public async Task AGradientLayerRepeatsAcrossTheAreaItFills()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:100px;height:40px;background:linear-gradient(red,red) 0 0/20px 20px repeat'></div>" +
            "</body></html>");

        var layer = SingleGradient(view);

        Assert.That(layer.RepeatX, Is.True, "the tile repeats along the inline axis");
        Assert.That(layer.RepeatY, Is.True, "and along the block axis");
        AssertTile(layer.Tile, 0, 0, 20, 20, "a repeated tile");
    }

    [Test]
    public async Task ARepeatXGradientKeepsOneRowOfItsTiles()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:100px;height:40px;background:linear-gradient(red,red) 0 0/20px 20px repeat-x'></div>" +
            "</body></html>");

        var layer = SingleGradient(view);

        Assert.That(layer.RepeatX, Is.True, "repeat-x tiles across");
        Assert.That(layer.RepeatY, Is.False, "and leaves one row of it where the position puts it");
    }

    [Test]
    public async Task ACentredGradientTileStandsInTheMiddleOfTheArea()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:100px;height:40px;background:linear-gradient(red,red) center/20px 20px no-repeat'></div>" +
            "</body></html>");

        AssertTile(SingleGradient(view).Tile, 40, 10, 20, 20, "a centred tile");
    }

    [Test]
    public async Task AGrowingGradientLayerKeepsTheAreaToItself()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:100px;height:40px;background:linear-gradient(red,red) 0 0/200% 50% no-repeat'></div>" +
            "</body></html>");

        var layer = SingleGradient(view);

        AssertTile(layer.Tile, 0, 0, 200, 20, "a tile sized by percentage");
        Assert.That(layer.RepeatX, Is.False);
    }

    private static void AssertTile(Rect tile, Double x, Double y, Double width, Double height, String where)
    {
        Assert.Multiple(() =>
        {
            Assert.That(tile.X, Is.EqualTo(x).Within(0.01), $"the inline start of {where}");
            Assert.That(tile.Y, Is.EqualTo(y).Within(0.01), $"the block start of {where}");
            Assert.That(tile.Width, Is.EqualTo(width).Within(0.01), $"the inline size of {where}");
            Assert.That(tile.Height, Is.EqualTo(height).Within(0.01), $"the block size of {where}");
        });
    }

    private static LinearGradientCommand SingleGradient(HutuViewInternal view)
    {
        var layers = view.Paint!.Commands.OfType<LinearGradientCommand>().ToList();

        Assert.That(layers, Has.Count.EqualTo(1), "one gradient layer should be painted");

        return layers[0];
    }
}

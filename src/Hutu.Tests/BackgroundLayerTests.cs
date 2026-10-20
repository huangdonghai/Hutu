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

    /// <summary>
    /// A gradient stands each colour where its position puts it along the gradient line, and settles
    /// the positions its author left out: the first and the last stop at the ends of the line, an
    /// interior run spaced equally between its neighbours, and any stop that would stand behind the
    /// largest position before it pulled up to that position (CSS Images 3 §3.4.1 and §3.4.3). A box
    /// 100px square with the default <c>to bottom</c> line runs from 0 to 100px, so a stop written in
    /// pixels stands that far along it, and a window whose stops fall outside it opens and closes on
    /// the colour the line has come to at each end.
    /// </summary>
    [Test]
    [TestCase("black, black 50px, white 50px, white", "ff000000@0 ff000000@0.5 ffffffff@0.5 ffffffff@1")]
    [TestCase("black, white 50px, black", "ff000000@0 ffffffff@0.5 ff000000@1")]
    [TestCase("black 0px, gray, gray, white 100px", "ff000000@0 ff808080@0.333 ff808080@0.667 ffffffff@1")]
    [TestCase("yellow 100px, blue 50%", "ffffff00@0 ff0000ff@1")]
    [TestCase("blue -5%, yellow -5%", "ffffff00@0 ffffff00@1")]
    [TestCase("blue 150%, yellow 150%", "ff0000ff@0 ff0000ff@1")]
    [TestCase("black -100px, white 100px", "ff808080@0 ffffffff@1")]
    [TestCase("#0000ff 50%", "ff0000ff@0 ff0000ff@0.5 ff0000ff@1")]
    [TestCase("blue, lime 25%", "ff0000ff@0 ff00ff00@0.25 ff00ff00@1")]
    public async Task AGradientStopStandsWhereItsPositionPutsIt(String stops, String expected)
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            $"<div style='width:100px;height:100px;background:linear-gradient({stops})'></div>" +
            "</body></html>");

        Assert.That(Write(SingleGradient(view)), Is.EqualTo(expected));
    }

    /// <summary>
    /// A stop written in pixels is measured along the gradient line of the tile it paints, which for a
    /// corner-pointed line is the projection of the box onto that direction and not the box's own
    /// diagonal (CSS Images 3 §3.1.1).
    /// </summary>
    [Test]
    public async Task AGradientLineRunsTheLengthTheBoxProjectsOntoItsDirection()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:400px;height:300px;" +
            "background:linear-gradient(to bottom right, lime 0px, teal 250px, black 100%)'></div>" +
            "</body></html>");

        // The line this box projects is 494.97px, so 250px of it lands at 0.505.
        Assert.That(Write(SingleGradient(view)), Is.EqualTo("ff00ff00@0 ff008080@0.505 ff000000@1"));
    }

    /// <summary>
    /// A stop position written as a <c>calc()</c> is added up as the distance and the share of the line
    /// it comes to, so a sum that mixes the two still waits for the box (CSS Values 4 §10.5 read with
    /// CSS Images 3 §3.4.1). The box here is 100px square with a 10px font, so its line runs 0 to 100px.
    /// </summary>
    [Test]
    [TestCase("lime calc(100% - 10px), blue", "ff00ff00@0 ff00ff00@0.9 ff0000ff@1")]
    [TestCase("blue calc(2em), yellow", "ff0000ff@0 ff0000ff@0.2 ffffff00@1")]
    [TestCase("red calc(30% + 30px)", "ffff0000@0 ffff0000@0.6 ffff0000@1")]
    [TestCase("red calc(200% / 2 - 40px), blue 60px", "ffff0000@0 ffff0000@0.6 ff0000ff@0.6 ff0000ff@1")]
    [TestCase("red calc(0px), blue 50px", "ffff0000@0 ff0000ff@0.5 ff0000ff@1")]
    [TestCase("red calc(20% + 0px), blue", "ffff0000@0 ffff0000@0.2 ff0000ff@1")]
    public async Task AGradientStopPositionAddsItsCalcUp(String stops, String expected)
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            $"<div style='width:100px;height:100px;font-size:10px;background:linear-gradient({stops})'></div>" +
            "</body></html>");

        Assert.That(Write(SingleGradient(view)), Is.EqualTo(expected));
    }

    /// <summary>Writes the stops a gradient paints as the colour and the fraction each was resolved to.</summary>
    private static String Write(LinearGradientCommand gradient) =>
        String.Join(" ", gradient.Stops.Select(stop =>
            $"{stop.Color:x8}@{stop.Offset:0.###}"));

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

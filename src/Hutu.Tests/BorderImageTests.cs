using Hutu.Rendering;
using Hutu.Rendering.Media;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The border image a box paints its border area with (CSS Backgrounds 3 §6): the picture is cut at the
/// slices and drawn into the room the widths give it, across a border image area the outsets may grow
/// past the box without moving it.
/// </summary>
[TestFixture]
public sealed class BorderImageTests
{
    [Test]
    public async Task ABorderImageIsDrawnOverTheBorderBoxItFrames()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='a' style='width:100px;height:80px;border:10px solid transparent;" +
            "border-image-source:linear-gradient(#4a90d9,#f0a);" +
            "border-image-slice:2;border-image-width:10px'></div>" +
            "</body></html>");

        var image = SingleBorderImage(view);
        var area = image.Area;

        Assert.Multiple(() =>
        {
            Assert.That(area.X, Is.EqualTo(0).Within(0.01), "the border box, no outset asked for");
            Assert.That(area.Y, Is.EqualTo(0).Within(0.01));
            Assert.That(area.Width, Is.EqualTo(120).Within(0.01), "the box is 100 wide plus a 10px border either side");
            Assert.That(area.Height, Is.EqualTo(100).Within(0.01));
            Assert.That(image.Widths.Top, Is.EqualTo(10).Within(0.01), "each edge drawn 10px wide");
            Assert.That(image.Widths.Left, Is.EqualTo(10).Within(0.01));
            Assert.That(image.Image.Slices[0].Value, Is.EqualTo(2), "the picture is cut 2px in on every side");
            Assert.That(image.Image.Fill, Is.False, "the middle is not painted unless the page says fill");
            Assert.That(image.Gradient, Is.Not.Null, "a gradient is an image like any other");
        });
    }

    [Test]
    public async Task TheComputedSliceKeepsTheSidesThePageWrote()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='a' style='border-image-slice:2'></div>" +
            "<div id='b' style='border-image-slice:2 fill'></div>" +
            "<div id='c' style='border-image-slice:10% 4'></div>" +
            "</body></html>");

        String Slice(String id) =>
            view.GetComputedStyle(view.Document!.GetElementById(id)!).GetPropertyValue("border-image-slice");

        Assert.That(Slice("a"), Is.EqualTo("2"), "the sides the page did not write are not lengths to resolve");
        Assert.That(Slice("b"), Is.EqualTo("2 fill"));
        Assert.That(Slice("c"), Is.EqualTo("10% 4"), "a percentage slices the picture, it does not measure the box");
    }

    [Test]
    public async Task AnOutsetGrowsTheAreaAndLeavesTheBoxWhereItWas()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='padding:40px'>" +
            "<div id='a' style='width:80px;height:40px;border:10px solid transparent;" +
            "border-image-source:linear-gradient(red,blue);border-image-slice:1;" +
            "border-image-width:10px;border-image-outset:8px'></div>" +
            "</div>" +
            "</body></html>");

        var image = SingleBorderImage(view);
        var area = image.Area;

        Assert.Multiple(() =>
        {
            Assert.That(area.X, Is.EqualTo(40 - 8).Within(0.01), "the area starts 8px left of the border box");
            Assert.That(area.Y, Is.EqualTo(40 - 8).Within(0.01));
            Assert.That(area.Width, Is.EqualTo(100 + 16).Within(0.01), "and reaches 8px past it on both sides");
            Assert.That(area.Height, Is.EqualTo(60 + 16).Within(0.01));
        });
    }

    [Test]
    public async Task ASliceOfFillIsPaintedAndARepeatIsReadForEachAxis()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='a' style='width:100px;height:80px;border:10px solid transparent;" +
            "border-image-source:conic-gradient(red,blue);border-image-slice:2 fill;" +
            "border-image-width:10px;border-image-repeat:round space'></div>" +
            "</body></html>");

        var image = SingleBorderImage(view);

        Assert.Multiple(() =>
        {
            Assert.That(image.Image.Fill, Is.True, "fill paints the middle of the picture too");
            Assert.That(image.Image.RepeatX, Is.EqualTo(BorderImageRepeat.Round), "the first keyword runs across");
            Assert.That(image.Image.RepeatY, Is.EqualTo(BorderImageRepeat.Space), "the second down the box");
        });
    }

    [Test]
    public async Task ABorderWithNoImagePaintsNoBorderImage()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:100px;height:80px;border:10px solid red'></div>" +
            "</body></html>");

        Assert.That(view.Paint!.Commands.OfType<BorderImageCommand>(), Is.Empty);
    }

    private static BorderImageCommand SingleBorderImage(HutuViewInternal view)
    {
        var images = view.Paint!.Commands.OfType<BorderImageCommand>().ToList();

        Assert.That(images, Has.Count.EqualTo(1), "one border image should be painted");

        return images[0];
    }
}

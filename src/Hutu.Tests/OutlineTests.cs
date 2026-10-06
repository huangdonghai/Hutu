using Hutu.Rendering;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using Hutu.Skia;
using NUnit.Framework;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// An outline is a stroke drawn around a box that takes no part in the layout, which is what sets it
/// apart from a border both in the commands it produces and in the pixels it leaves behind.
/// </summary>
[TestFixture]
public sealed class OutlineTests
{
    [Test]
    public async Task ABoxThatWasGivenNoOutlineDrawsNothing()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:60px;height:40px;background:#00ff00'></div>" +
            "</body></html>");

        Assert.That(view.Paint!.Commands.OfType<OutlineCommand>(), Is.Empty, "nothing asked for an outline");
    }

    [Test]
    public async Task AnOutlineStyleOfNoneDrawsNothing()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:60px;height:40px;outline:4px solid #ff0000;outline-style:none'></div>" +
            "</body></html>");

        Assert.That(view.Paint!.Commands.OfType<OutlineCommand>(), Is.Empty, "a later `none` takes the outline away");
    }

    [Test]
    public async Task AnOutlineWidthOfZeroDrawsNothing()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:60px;height:40px;outline:0 solid #ff0000'></div>" +
            "</body></html>");

        Assert.That(view.Paint!.Commands.OfType<OutlineCommand>(), Is.Empty);
    }

    [Test]
    public async Task AnOutlineIsEmittedAroundTheBorderBoxItSurrounds()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:20px'>" +
            "<div style='width:60px;height:40px;background:#00ff00;outline:4px solid #ff0000;outline-offset:2px'></div>" +
            "</body></html>");

        var outline = view.Paint!.Commands.OfType<OutlineCommand>().Single();
        var box = BoxOf(view, "div");

        Assert.That(outline.Width, Is.EqualTo(4).Within(0.01));
        Assert.That(outline.Offset, Is.EqualTo(2).Within(0.01));
        Assert.That(outline.Color, Is.EqualTo(0xFFFF0000u));
        Assert.That(outline.Style, Is.EqualTo(BorderLineStyle.Solid));

        // The bounds are the border box and not the outline's own rectangle: the stroke reaches outside
        // it, so it is the backend that inflates the rectangle it draws.
        Assert.That(outline.Bounds.X, Is.EqualTo(box.BorderBox.X).Within(0.01));
        Assert.That(outline.Bounds.Y, Is.EqualTo(box.BorderBox.Y).Within(0.01));
        Assert.That(outline.Bounds.Width, Is.EqualTo(box.BorderBox.Width).Within(0.01));
        Assert.That(outline.Bounds.Height, Is.EqualTo(box.BorderBox.Height).Within(0.01));
    }

    [Test]
    public async Task TheOutlineStyleReachesTheBackendAsItsOwnStroke()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:60px;height:40px;outline:3px dashed #0000ff'></div>" +
            "<div style='width:60px;height:40px;outline:3px dotted #0000ff'></div>" +
            "<div style='width:60px;height:40px;outline:3px groove #0000ff'></div>" +
            "</body></html>");

        var strokes = view.Paint!.Commands.OfType<OutlineCommand>().Select(outline => outline.Style).ToList();

        Assert.That(
            strokes,
            Is.EqualTo(new[] { BorderLineStyle.Dashed, BorderLineStyle.Dotted, BorderLineStyle.Solid }),
            "a keyword Hutu cannot draw is drawn solid rather than dropped");
    }

    [Test]
    public async Task AnOutlineGivenNoWidthIsDrawnAtTheMediumWidth()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:60px;height:40px;outline-style:solid;outline-color:#ff0000'></div>" +
            "</body></html>");

        var outline = view.Paint!.Commands.OfType<OutlineCommand>().Single();

        // `outline-width` is initialised to `medium`, which the spec pins at three pixels.
        Assert.That(outline.Width, Is.EqualTo(3).Within(0.01));
    }

    [Test]
    public async Task AnOutlineWidthNamedByAKeywordIsReadAsThePixelsItStandsFor()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:60px;height:40px;outline:thin solid #ff0000'></div>" +
            "<div style='width:60px;height:40px;outline:medium solid #ff0000'></div>" +
            "<div style='width:60px;height:40px;outline:thick solid #ff0000'></div>" +
            "</body></html>");

        // The three keywords are carried as the lengths they stand for rather than as lengths
        // themselves, so reading them has to look inside what the declaration holds.
        var widths = view.Paint!.Commands
            .OfType<OutlineCommand>()
            .Select(outline => outline.Width)
            .ToList();

        Assert.That(widths, Has.Count.EqualTo(3));
        Assert.That(widths[0], Is.GreaterThan(0), "`thin` is a pixel or more");
        Assert.That(widths[0], Is.LessThan(widths[1]), "`thin` is thinner than `medium`");
        Assert.That(widths[1], Is.LessThan(widths[2]), "`medium` is thinner than `thick`");
    }

    [Test]
    public async Task AnOutlineGivenNoColourIsDrawnInTheColourOfItsOwnText()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:60px;height:40px;color:#112233;outline:3px solid'></div>" +
            "</body></html>");

        var outline = view.Paint!.Commands.OfType<OutlineCommand>().Single();

        // The initial colour is `invert`, a system colour Hutu cannot resolve, so the element's own
        // colour stands in rather than the outline being dropped.
        Assert.That(outline.Color, Is.EqualTo(0xFF112233u));
    }

    [Test]
    public async Task AnExplicitOutlineColourBeatsTheColourOfTheText()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:60px;height:40px;color:#112233;outline:3px solid #ff0000'></div>" +
            "</body></html>");

        var outline = view.Paint!.Commands.OfType<OutlineCommand>().Single();

        Assert.That(outline.Color, Is.EqualTo(0xFFFF0000u));
    }

    [Test]
    public async Task TheOutlineCarriesTheRadiiOfTheBorderBox()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:60px;height:40px;border-radius:10px;outline:3px solid #ff0000;outline-offset:4px'></div>" +
            "</body></html>");

        var outline = view.Paint!.Commands.OfType<OutlineCommand>().Single();

        Assert.That(
            outline.Radii.TopLeft.Horizontal,
            Is.EqualTo(10).Within(0.01),
            "the command carries the radii of the border box; the backend grows them by the offset");
    }

    [Test]
    public async Task AnOutlineIsNotCutOffByTheOverflowOfTheBoxItSurrounds()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:60px;height:40px;overflow:hidden;outline:3px solid #ff0000'>" +
            "<div style='width:10px;height:10px'></div>" +
            "</div>" +
            "</body></html>");

        var commands = view.Paint!.Commands;

        Assert.That(commands.OfType<PushClipCommand>(), Is.Not.Empty, "the box hides its overflow");

        var outline = commands.IndexOf(commands.OfType<OutlineCommand>().Single());
        var pop = commands.IndexOf(commands.OfType<PopClipCommand>().Last());

        // The clip the box opened for its children is closed before the outline is emitted, so the box's
        // own overflow cannot eat the stroke - while an ancestor's clip still holds it in.
        Assert.That(outline, Is.GreaterThan(pop), "the outline is drawn after the children's clip is closed");
    }

    [Test]
    public async Task ABlockOfSeveralLinesIsOutlinedOnce()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='margin:0;width:40px;outline:3px solid #ff0000'>one two three four five</p>" +
            "</body></html>");

        // A line box and a run of text borrow the style of what they sit in, so an outline emitted for
        // them would draw the paragraph's outline once per line.
        Assert.That(
            view.Paint!.Commands.OfType<TextCommand>().Select(text => text.Text).Distinct().Count(),
            Is.GreaterThan(1),
            "the paragraph wraps onto more than one line");
        Assert.That(view.Paint!.Commands.OfType<OutlineCommand>(), Has.Exactly(1).Items);
    }

    [Test]
    public async Task TheStrokeIsPaintedOutsideTheBoxAndNotOverIt()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:20px'>" +
            "<div style='width:60px;height:40px;background:#00ff00;outline:4px solid #ff0000;outline-offset:2px'></div>" +
            "</body></html>");

        using var bitmap = Render(view);

        var box = BoxOf(view, "div");
        var middle = (Int32)(box.BorderBox.Y + (box.BorderBox.Height / 2));
        var left = (Int32)box.BorderBox.X;

        // The band runs from two pixels outside the border box to six pixels outside it, so a sample at
        // four lands in the middle of it and one at one lands in the gap the offset leaves. The box
        // keeps its own colour either way: the stroke never reaches the border box.
        Assert.That(bitmap.GetPixel(left - 4, middle).Red, Is.GreaterThan(200), "the stroke is four pixels outside the edge");
        Assert.That(bitmap.GetPixel(left - 4, middle).Green, Is.LessThan(80));
        Assert.That(bitmap.GetPixel(left - 1, middle), Is.EqualTo(SKColors.White), "the offset leaves a gap");
        Assert.That(
            bitmap.GetPixel(left + 5, middle).Green,
            Is.GreaterThan(200),
            "the box keeps its own colour under the stroke");
    }

    [Test]
    public async Task ARepeatedOffsetPullsTheStrokeIntoTheBox()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:20px'>" +
            "<div style='width:60px;height:40px;background:#00ff00;outline:4px solid #ff0000;outline-offset:-4px'></div>" +
            "</body></html>");

        using var bitmap = Render(view);

        var box = BoxOf(view, "div");
        var middle = (Int32)(box.BorderBox.Y + (box.BorderBox.Height / 2));
        var left = (Int32)box.BorderBox.X;

        // A negative offset shifts the stroke inwards, so the band covers the first four pixels of the
        // box and nothing at all is left outside it.
        Assert.That(bitmap.GetPixel(left + 2, middle).Red, Is.GreaterThan(200), "the stroke sits inside the edge");
        Assert.That(bitmap.GetPixel(left - 2, middle), Is.EqualTo(SKColors.White), "nothing reaches outside");
    }

    [Test]
    public async Task ARoundedOutlineFollowsTheCurveInsteadOfSquaringTheCorner()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:40px'>" +
            "<div style='width:60px;height:40px;background:#00ff00;outline:4px solid #ff0000;outline-offset:4px'></div>" +
            "<div style='margin-top:40px;width:60px;height:40px;background:#00ff00;border-radius:10px;" +
            "outline:4px solid #ff0000;outline-offset:4px'></div>" +
            "</body></html>");

        using var bitmap = Render(view);

        var boxes = view.Paint!.Commands.OfType<OutlineCommand>().OrderBy(outline => outline.Bounds.Y).ToList();

        Assert.That(boxes, Has.Count.EqualTo(2));

        // A point six pixels out along each axis from a corner lies within the band of a square outline,
        // which reaches from four to eight pixels out. The rounded one's curve has already turned away
        // by then - its outer radius is the ten pixel corner grown by the offset and the stroke - so the
        // same point is left untouched.
        var square = boxes[0].Bounds;
        var rounded = boxes[1].Bounds;

        Assert.That(
            bitmap.GetPixel((Int32)square.X - 6, (Int32)square.Y - 6).Red,
            Is.GreaterThan(200),
            "a square outline reaches into its own corner");
        Assert.That(
            bitmap.GetPixel((Int32)rounded.X - 6, (Int32)rounded.Y - 6),
            Is.EqualTo(SKColors.White),
            "a rounded outline has curved away from its corner");
    }

    [Test]
    public async Task ADashedOutlineIsDrawnAsDashesRatherThanAsABand()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:20px'>" +
            "<div style='width:60px;height:40px;background:#00ff00;outline:4px dashed #ff0000;outline-offset:2px'></div>" +
            "</body></html>");

        using var bitmap = Render(view);

        var box = BoxOf(view, "div");
        var top = (Int32)box.BorderBox.Y;
        var left = (Int32)box.BorderBox.X;

        // The pattern is stroked along the middle of the band, four pixels outside the border box, so
        // walking the whole top edge has to find both a dash and a gap: a solid band would have no gap
        // in it at all.
        var painted = 0;
        var blank = 0;

        for (var x = left - 6; x <= left + (Int32)box.BorderBox.Width + 6; x++)
        {
            var pixel = bitmap.GetPixel(x, top - 4);

            if (pixel.Red > 200 && pixel.Green < 80)
            {
                painted++;
            }
            else
            {
                blank++;
            }
        }

        Assert.That(painted, Is.GreaterThan(0), "the dashes are drawn");
        Assert.That(blank, Is.GreaterThan(0), "the gaps between them are not");
    }

    private static SKBitmap Render(HutuViewInternal view)
    {
        var paint = view.Paint!;

        using var surface = SKSurface.Create(new SKImageInfo(
            Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Width)),
            Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Height))));

        using var measurer = new SkiaTextMeasurer();
        new SkiaPaintExecutor(measurer).Execute(paint, surface.Canvas);

        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    /// <summary>Finds the box an element of a given name was laid out as.</summary>
    private static LayoutBox BoxOf(HutuViewInternal view, String name)
    {
        foreach (var box in Walk(view.Layout!.Root))
        {
            if (box.Kind == LayoutKind.Block && box.Source.Element?.LocalName == name)
            {
                return box;
            }
        }

        Assert.Fail($"no {name} box was laid out");
        return null!;
    }

    private static IEnumerable<LayoutBox> Walk(LayoutBox node)
    {
        yield return node;

        foreach (var child in node.Children)
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(200, 200);
        return view;
    }
}

using Hutu.Rendering;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using Hutu.Skia;
using NUnit.Framework;
using SkiaSharp;
using System;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The executor draws only what the canvas it is handed can show - the area is the canvas' own clip -
/// and what lands inside that area is the picture the list would have made with no area at all.
/// </summary>
/// <remarks>
/// A command that is skipped is invisible by definition: the clip would have hidden it anyway, so no
/// picture can tell a repaint that skipped one from a repaint that did not, and what the skipping saves
/// is the probe's to report. What these tests watch instead is the rule, which is pure and is asked
/// directly about the three translations and the two exemptions, and the area the rule is applied to,
/// which the canvas answers and which is asked about in place.
/// </remarks>
[TestFixture]
public sealed class PaintCullingTests
{
    private const UInt32 Red = 0xFFFF0000;
    private const UInt32 Green = 0xFF00FF00;
    private const UInt32 Yellow = 0xFFFFFF00;

    // --- the rule -------------------------------------------------------

    [Test]
    public void ACommandTheAreaReachesIsDrawn()
    {
        Assert.That(IsVisible(Fill(10, 10, 20, 20), new Rect(0, 0, 100, 100)), Is.True);
    }

    [Test]
    public void ACommandTheAreaDoesNotReachIsNotDrawn()
    {
        Assert.That(IsVisible(Fill(0, 200, 100, 100), new Rect(0, 0, 100, 100)), Is.False);
    }

    [Test]
    public void AnEdgeThatOnlyTouchesTheAreaIsOutsideIt()
    {
        // Half open: the area is everything below and right of its edges, so a rectangle that ends
        // where the area begins paints nothing a reader can see.
        Assert.That(IsVisible(Fill(100, 0, 20, 100), new Rect(0, 0, 100, 100)), Is.False);
        Assert.That(IsVisible(Fill(99, 0, 20, 100), new Rect(0, 0, 100, 100)), Is.True);
    }

    [Test]
    public void AFixedBoxIsJudgedWhereTheScrollPutsIt()
    {
        // A fixed box is in viewport coordinates and is drawn shifted by the scroll, so the same
        // rectangle is on screen at one offset and off it at another.
        var command = Fill(0, 0, 100, 20);

        Assert.That(IsVisible(command, new Rect(0, 500, 100, 100), offset: new Offset(0, 500)), Is.True);
        Assert.That(IsVisible(command, new Rect(0, 500, 100, 100), offset: new Offset(0, 0)), Is.False);
    }

    [Test]
    public void AClipIsDrawnWhereverItIs()
    {
        // The two are a save and a restore around everything between them. Turning either away would
        // leave the canvas stack unbalanced for every command after it.
        var bounds = new Rect(0, 5000, 10, 10);

        Assert.That(SkiaPaintExecutor.IsVisible(new PushClipCommand(bounds, CornerRadii.None), default, new Rect(0, 0, 100, 100)), Is.True);
        Assert.That(SkiaPaintExecutor.IsVisible(new PopClipCommand(bounds), default, new Rect(0, 0, 100, 100)), Is.True);
    }

    [Test]
    public void TheAreaIsWhatTheCanvasClipSays()
    {
        using var surface = SKSurface.Create(new SKImageInfo(100, 300));

        // Nothing clipped: the whole surface is visible, so nothing is culled. Skia reports the clip a
        // pixel wide of the edges it will actually keep - it rounds the bounds outwards so that a
        // drawing that only touches an edge is not lost - which is the direction this wants to be out
        // by as well.
        var whole = SkiaPaintExecutor.VisibleArea(surface.Canvas);

        Assert.That(whole.X, Is.LessThanOrEqualTo(0), "a canvas that clips nothing is not narrowed on the left");
        Assert.That(whole.Y, Is.LessThanOrEqualTo(0));
        Assert.That(whole.Right, Is.GreaterThanOrEqualTo(100), $"all of it is visible, not the {whole.Width}px reported");
        Assert.That(whole.Bottom, Is.GreaterThanOrEqualTo(300));

        // A control draws into a clip, and a scroller moves the canvas rather than arranging the
        // control again - so the clip is the window onto the document, and what it says is in the
        // coordinates the commands are drawn in. That is what makes it a fact about this frame rather
        // than a belief about the one the host believes it is scrolling in.
        surface.Canvas.Save();
        surface.Canvas.ClipRect(new SKRect(0, 0, 100, 100));
        surface.Canvas.Translate(0, -500);

        var window = SkiaPaintExecutor.VisibleArea(surface.Canvas);

        Assert.That(window.Y, Is.EqualTo(500).Within(2), "the canvas was moved up by 500, so the window shows from there");
        Assert.That(window.Height, Is.EqualTo(100).Within(2), "and it is a window's worth tall, wherever it moved to");
        Assert.That(window.X, Is.EqualTo(0).Within(2));
        Assert.That(window.Width, Is.EqualTo(100).Within(2));

        surface.Canvas.Restore();
    }

    [Test]
    public void AnOutlineIsJudgedByTheBandItPaintsOutsideItsBox()
    {
        // The box ends at y = 10 and the band reaches 6 further out from an offset of 4, so it is on
        // screen for an area that begins at 12 and off it for one that begins past 20.
        var outline = new OutlineCommand(new Rect(0, 0, 100, 10), CornerRadii.None, BorderLineStyle.Solid, 6, 4, 0xFF000000);

        Assert.That(IsVisible(outline, new Rect(0, 12, 100, 100)), Is.True);
        Assert.That(IsVisible(outline, new Rect(0, 21, 100, 100)), Is.False);
    }

    [Test]
    public void AnOutlineDrawnInwardIsJudgedByItsBox()
    {
        // A negative offset pulls the band inside the box, where the box's own rectangle already
        // covers it - so an outline whose box is off screen is off screen.
        var outline = new OutlineCommand(new Rect(0, 0, 100, 10), CornerRadii.None, BorderLineStyle.Solid, 6, -8, 0xFF000000);

        Assert.That(IsVisible(outline, new Rect(0, 21, 100, 100)), Is.False);
    }

    // --- where a command is judged --------------------------------------

    [Test]
    public void AClipPairSurvivesEvenWhereItIsOffScreen()
    {
        // The pair's own rectangle is below the window, so what it holds is invisible either way - the
        // question is whether it still held. Both halves have to be applied: skip the push and the band
        // before it shows through, skip the pop and the clip it pushed is still on for the band after.
        var list = new PaintList { Bounds = new Rect(0, 0, 100, 300) };
        list.Commands.Add(new PushClipCommand(new Rect(0, 200, 100, 50), CornerRadii.None));
        list.Commands.Add(Fill(0, 0, 100, 50, Red));
        list.Commands.Add(new PopClipCommand(new Rect(0, 200, 100, 50)));
        list.Commands.Add(Fill(0, 60, 100, 40, Green));

        using var bitmap = Raster(list, 100, 300, clip: new Rect(0, 0, 100, 100));

        Assert.That(bitmap.GetPixel(50, 25), Is.EqualTo(SKColors.White), "the first band is inside the clip");
        Assert.That(bitmap.GetPixel(50, 80), Is.EqualTo(new SKColor(Green)), "the second is after it");
    }

    [Test]
    public void AStickyLayerIsDrawnWhereItsConstraintPutsIt()
    {
        // The box sits at y = 300 and the window begins at 320, which is where a `top: 0` pins it to at
        // this scroll. A box judged at the offset of the list it came from, and not at the layer's own,
        // would be off the window and would not be drawn at all.
        var constraint = new StickyConstraint(
            new Rect(0, 0, 100, 100),
            new Rect(0, 0, 100, 400),
            new Rect(0, 300, 100, 20),
            top: 0,
            right: Double.NaN,
            bottom: Double.NaN,
            left: Double.NaN);

        var layer = new StickyLayer(constraint);
        layer.Commands.Add(Fill(0, 300, 100, 20, Yellow));

        var list = new PaintList { Bounds = new Rect(0, 0, 100, 400) };
        list.Sticky.Add(layer);

        using var bitmap = Raster(list, 100, 400, new Offset(0, 320), clip: new Rect(0, 320, 100, 100));

        Assert.That(bitmap.GetPixel(50, 330), Is.EqualTo(new SKColor(Yellow)), "pinned to the top of the scrollport");
    }

    [Test]
    public void AFixedBoxIsDrawnWhereTheScrollPutsIt()
    {
        var list = new PaintList { Bounds = new Rect(0, 0, 100, 600) };
        list.FixedCommands.Add(Fill(0, 0, 100, 20, Yellow));

        // Viewport coordinates: drawn at the scroll, so it lands in the window the reader is over. The
        // other half of this - the same box at a scroll of zero being off that window - is what
        // AFixedBoxIsJudgedWhereTheScrollPutsIt asks of the rule, since a picture cannot show the
        // difference between a command that was skipped and one the clip hid.
        using var scrolled = Raster(list, 100, 600, new Offset(0, 500), clip: new Rect(0, 500, 100, 100));

        Assert.That(scrolled.GetPixel(50, 510), Is.EqualTo(new SKColor(Yellow)), "kept in place by the scroll");
    }

    [Test]
    public void TheCanvasClipIsReadBeforeTheFixedListIsShifted()
    {
        // The canvas is moved the way a scroller moves it and the fixed list is drawn under that same
        // scroll a second time, so the area has to be the clip as the call arrived: read after the
        // shift it would be a rectangle that had moved twice, and a box in plain sight would be judged
        // off screen.
        var list = new PaintList { Bounds = new Rect(0, 0, 100, 600) };
        list.FixedCommands.Add(Fill(0, 0, 100, 20, Yellow));

        using var bitmap = Raster(
            list,
            100,
            100,
            new Offset(0, 500),
            canvasShift: new Offset(0, -500));

        Assert.That(bitmap.GetPixel(50, 10), Is.EqualTo(new SKColor(Yellow)), "the fixed box is on screen at this scroll");
    }

    // --- the picture a window holds -------------------------------------

    [Test]
    public async Task TheWindowHoldsWhatTheDocumentHasUnderIt()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(400, 300);

        // Solid shapes only: the two drawings below are compared pixel for pixel, and a fill lands in
        // the same pixels wherever the canvas stands while a glyph need not.
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:120px;background:#ff0000'></div>" +
            "<div style='height:600px;background:#0000ff'></div>" +
            "</body></html>");

        var paint = view.Paint!;
        var document = (Int32)Math.Ceiling(paint.Bounds.Height);

        // The whole document into a surface the size of it: the clip is all of it, so nothing is
        // skipped and this is what the page is.
        using var all = Raster(paint, 400, document);

        // The same list with the canvas where a scroller would have put it and a surface the size of
        // the window: the clip now reports the part of the document the reader is over, and what lands
        // in the picture has to be what the document holds there.
        const Int32 Scrolled = 100;

        using var window = Raster(
            paint,
            400,
            300,
            new Offset(0, Scrolled),
            canvasShift: new Offset(0, -Scrolled));

        for (var y = 0; y < 300; y += 15)
        {
            for (var x = 0; x < 400; x += 50)
            {
                Assert.That(
                    window.GetPixel(x, y),
                    Is.EqualTo(all.GetPixel(x, y + Scrolled)),
                    $"the window at ({x}, {y}) holds what the document has at ({x}, {y + Scrolled})");
            }
        }
    }

    // --- helpers --------------------------------------------------------

    /// <summary>The rule, as the executor applies it, with the area first for readability here.</summary>
    private static Boolean IsVisible(PaintCommand command, Rect visible, Offset offset = default) =>
        SkiaPaintExecutor.IsVisible(command, offset, visible);

    private static FillCommand Fill(Double x, Double y, Double width, Double height, UInt32 color = Red) =>
        new(new Rect(x, y, width, height), color, CornerRadii.None);

    /// <summary>
    /// Draws the list into a surface of the given size, with whatever the caller has arranged for the
    /// canvas: how far it is moved and what it is clipped to. Those two are the whole of what decides
    /// the area the executor culls by, since the area is the canvas' answer and not a parameter.
    /// </summary>
    /// <param name="canvasShift">
    /// How far the canvas is moved before the list is drawn, which is how a scroller brings content
    /// into a window it clips to rather than arranging it again. It is not the scroll: the scroll is
    /// what the executor is told, and this is what the canvas was actually moved by.
    /// </param>
    /// <param name="clip">
    /// What the canvas is clipped to, in the coordinates it stands in at that point - the window onto
    /// the document, for a test that wants one narrower than the surface.
    /// </param>
    private static SKBitmap Raster(
        PaintList list,
        Int32 width,
        Int32 height,
        Offset scroll = default,
        Offset? canvasShift = null,
        Rect? clip = null)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var shift = canvasShift ?? default;

        // Painted before the clip, and by hand: a clear reaches no further than the clip either, so a
        // window would otherwise keep whatever the surface was allocated with outside it.
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.Save();
        surface.Canvas.Translate((Single)shift.X, (Single)shift.Y);

        if (clip is { } area)
        {
            surface.Canvas.ClipRect(new SKRect((Single)area.X, (Single)area.Y, (Single)area.Right, (Single)area.Bottom));
        }

        new SkiaPaintExecutor(new SkiaTextMeasurer()).Execute(list, surface.Canvas, scroll);

        surface.Canvas.Restore();

        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }
}

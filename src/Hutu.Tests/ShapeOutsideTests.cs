using AngleSharp.Css;
using Hutu.Rendering;
using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers <c>shape-outside</c>, <c>shape-margin</c> and the area a float's lines stand clear of.
/// </summary>
/// <remarks>
/// The lines are made of inline-blocks of a stated height under <c>line-height: 0</c>, which is how the
/// corpus builds its own expectations: each box is one line box, so where the line begins is the box's own
/// left edge, and no font metric stands between the shape and the answer.
/// <para>
/// The band rule - a line clears the shape's furthest point across the whole height the line takes - was
/// measured against Chrome on a 100x100 left float shaped <c>polygon(0 0, 0 100px, 100px 100px)</c> over a
/// block of 20-pixel lines: the line at 0 began at x=20, the one at 20 at x=40, up to x=100, then back to 0.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ShapeOutsideTests
{
    [Test]
    public async Task AShapeWideningDownwardHoldsEveryLineToItsWidestPoint()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:200px;line-height:0'>" +
            "<div id='f' style='float:left;width:100px;height:100px;shape-outside:polygon(0 0, 0 100px, 100px 100px)'></div>" +
            Boxes(6) +
            "</div>"));

        // The edge of the shape runs from (0,0) to (100,100), so a band is as wide at its further edge as
        // it is far down - and a line is held at the whole of the band, not at its own top row.
        Assert.That(Bounds(view, "b1").X, Is.EqualTo(20).Within(0.5), "the line at 0..20 clears x=20");
        Assert.That(Bounds(view, "b2").X, Is.EqualTo(40).Within(0.5));
        Assert.That(Bounds(view, "b3").X, Is.EqualTo(60).Within(0.5));
        Assert.That(Bounds(view, "b4").X, Is.EqualTo(80).Within(0.5));
        Assert.That(Bounds(view, "b5").X, Is.EqualTo(100).Within(0.5));

        // Below the shape there is nothing left to avoid, and the line runs through the float's own box.
        Assert.That(Bounds(view, "b6").X, Is.EqualTo(0).Within(0.5), "a band past the shape takes the width");
    }

    [Test]
    public async Task AShapeNarrowingDownwardHoldsTheLineAtItsOwnTopEdge()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:200px;line-height:0'>" +
            "<div id='f' style='float:left;width:100px;height:100px;shape-outside:polygon(0 0, 100px 0, 0 100px)'></div>" +
            Boxes(2) +
            "</div>"));

        // The other half of the same rule: where the shape only shrinks across the band, its widest point of
        // the band is the line's own top edge, and the room grows as the lines go down.
        Assert.That(Bounds(view, "b1").X, Is.EqualTo(100).Within(0.5));
        Assert.That(Bounds(view, "b2").X, Is.EqualTo(80).Within(0.5));
    }

    [Test]
    public async Task AShapeBesideTheLineLeavesTheLineAboveItAlone()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:200px;line-height:0'>" +
            "<div id='f' style='float:left;width:100px;height:100px;shape-outside:inset(50% 0 0 0)'></div>" +
            Boxes(3) +
            "</div>"));

        // Only the lower half of the box is the area, so the two lines standing over it are untouched: the
        // content outside the shape overlays the rest of the float's margin box (CSS Shapes 1 §2).
        Assert.That(Bounds(view, "b1").X, Is.EqualTo(0).Within(0.5), "a band above the area takes the width");
        Assert.That(Bounds(view, "b2").X, Is.EqualTo(0).Within(0.5), "and so does one that ends above it");
        Assert.That(Bounds(view, "b3").X, Is.EqualTo(100).Within(0.5), "the line across the area clears it");
    }

    [Test]
    public async Task AShapeCannotReachPastTheFloatsOwnMarginBox()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:300px;line-height:0'>" +
            "<div id='f' style='float:left;width:100px;height:100px;shape-outside:circle(60%)'></div>" +
            "<span id='b' style='display:inline-block;width:60px;height:60px'></span>" +
            "</div>"));

        // A circle of 60% of a 100x100 box is a disc from x=-10 to x=110, and the area is cut to the margin
        // box it was drawn on: a shape can only ever reduce a float area, never increase it (§2).
        Assert.That(Bounds(view, "b").X, Is.EqualTo(100).Within(0.5), "the disc is cut to the margin box");
    }

    [Test]
    public async Task ABoxValueStandsTheAreaAtTheEdgeThePageNamed()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='one' style='width:400px;line-height:0'>" +
            "<div id='f1' style='float:left;width:40px;height:100px;padding:20px;border:20px solid gray;margin:20px;shape-outside:border-box'></div>" +
            Boxes(2, "a") +
            "</div>" +
            "<div id='two' style='width:400px;line-height:0'>" +
            "<div id='f2' style='float:left;width:40px;height:100px;padding:20px;border:20px solid gray;margin:20px'></div>" +
            Boxes(2, "b") +
            "</div>"));

        // The border box is 120 across standing 20 in from the edge, and the margin box 160: naming the
        // border edge lets the lines into the margin the float stands in, and over the band the margin box
        // covers but the border box does not, it lets them through the whole way.
        Assert.That(Bounds(view, "a1").X, Is.EqualTo(0).Within(0.5), "the top of the area is only margin");
        Assert.That(Bounds(view, "a2").X, Is.EqualTo(140).Within(0.5), "the border edge, insets and all");
        Assert.That(Bounds(view, "b1").X, Is.EqualTo(160).Within(0.5), "a float with no shape keeps its margin box");
        Assert.That(Bounds(view, "b2").X, Is.EqualTo(160).Within(0.5));
    }

    [Test]
    public async Task ABoxValueFollowsTheCurveItWasGiven()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:400px;line-height:0'>" +
            "<div id='f' style='float:left;width:120px;height:120px;border-radius:60px;shape-outside:border-box'></div>" +
            Boxes(2) +
            "</div>"));

        // The shape is the disc the radius turns the box into, so the first line - standing across the top
        // slice of it - is held at that slice's chord rather than at the corner of the box.
        Assert.That(Bounds(view, "b1").X, Is.EqualTo(60 + Math.Sqrt(3600 - 1600)).Within(0.5), "the curve, not the corner");
        Assert.That(Bounds(view, "b2").X, Is.EqualTo(60 + Math.Sqrt(3600 - 400)).Within(0.5));
    }

    [Test]
    public async Task AStepIsLeftBehindWhenTheLineBeginsAtItsCorner()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:200px;line-height:0'>" +
            "<div id='f' style='float:left;width:200px;height:200px;shape-outside:polygon(" +
            "0px 40px, 120px 40px, 120px 80px, 80px 80px, 80px 120px, 160px 120px, 160px 160px, 0px 160px)'></div>" +
            string.Concat(Enumerable.Range(1, 8).Select(index =>
                $"<div style='height:20px'><span id='b{index}' " +
                "style='display:inline-block;width:40px;height:20px'></span></div>")) +
            "</div>"));

        // The shape steps back at 40 and again at 80. A line that begins exactly on the corner of the
        // outer step belongs to the step it opens, so the line at 80 stands beside the 80-wide part and
        // not beside the corner of the 120-wide one the line above ends on (CSS Shapes 1 §3.1).
        Assert.That(Bounds(view, "b3").X, Is.EqualTo(120).Within(0.5), "the line over the first step");
        Assert.That(Bounds(view, "b5").X, Is.EqualTo(80).Within(0.5), "the line that begins on the corner");
        Assert.That(Bounds(view, "b7").X, Is.EqualTo(160).Within(0.5), "the line over the next step");
    }

    [Test]
    public async Task ALineTooWideForAShapeStopsWhereItFirstFits()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:300px;line-height:0'>" +
            "<div id='f' style='float:left;width:100px;height:100px;shape-outside:circle(70.710678px at 0px 0px)'></div>" +
            "<span id='w' style='display:inline-block;width:250px;height:20px'></span>" +
            "</div>"));

        // The circle stands on the float's top-left corner, so its edge is 70.7 across at the top and has
        // reached 50 by the time the line is 50 down: the 250-wide line does not fit above that, and comes
        // to rest in the first place it does - not under the whole float (CSS 2.1 §9.5, Shapes 1 §1.3).
        Assert.That(Bounds(view, "w").X, Is.EqualTo(50).Within(0.5), "the shape's edge where the line fits");
        Assert.That(Bounds(view, "w").Y, Is.EqualTo(50).Within(0.5), "and the height it came to rest at");
    }

    [Test]
    public async Task ABoxTurnedAwayFromTheLineDoesNotCountTowardsItsBand()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:200px;line-height:0'>" +
            "<div id='f' style='float:left;width:120px;height:120px;border-radius:60px;shape-outside:border-box'></div>" +
            "<span id='b1' style='display:inline-block;width:40px;height:20px'></span>" +
            "<span id='b2' style='display:inline-block;width:60px;height:24px'></span>" +
            "</div>"));

        // The second box does not fit beside the curve and stands on the next line, so the first line is
        // only the 20 pixels tall one: a shape's answer is asked of the band the built line takes, not of
        // the taller box that was turned away from it (CSS Shapes 1 §1.3).
        Assert.That(Bounds(view, "b1").X, Is.EqualTo(60 + Math.Sqrt(3600 - 1600)).Within(0.5), "the band of the line itself");
    }

    [Test]
    public async Task TheMarginBoxValueGrowsItsCornersOutOfTheMargin()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:400px;line-height:0'>" +
            "<div id='f' style='float:left;width:100px;height:100px;margin:20px;border-radius:20px;shape-outside:margin-box'></div>" +
            Boxes(2) +
            "</div>"));

        // A radius does not simply grow by the margin around it: with a ratio of one the corner is
        // `border-radius + margin`, 40 of it, on the 140 the margin box comes to (CSS Shapes 1 §5). The
        // first line stands across the top of that corner, at 100 + sqrt(1600 - 400).
        Assert.That(Bounds(view, "b1").X, Is.EqualTo(100 + Math.Sqrt(1600 - 400)).Within(0.5), "the corner the formula gives");
        Assert.That(Bounds(view, "b2").X, Is.EqualTo(140).Within(0.5), "and the straight edge below it");
    }

    [Test]
    public async Task AShapeMarginStandsTheShapeFurtherOut()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:300px;line-height:0'>" +
            "<div id='f' style='float:left;width:100px;height:100px;margin-right:40px;shape-outside:polygon(0 0, 50px 0, 50px 100px, 0 100px);shape-margin:10px'></div>" +
            "<span id='b' style='display:inline-block;width:60px;height:20px'></span>" +
            "</div>"));

        // The outline is the left half of the box and the margin stands it ten further out; the float's own
        // margin leaves room for the area to reach, because the added margin does not let the area grow past
        // the margin box (§6.3).
        Assert.That(Bounds(view, "b").X, Is.EqualTo(60).Within(0.5), "the shape, ten pixels further out");
    }

    [Test]
    public async Task AVerticalLineShapeStillHoldsTheLineAtItsPlace()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:200px;line-height:0'>" +
            "<div id='f' style='float:left;width:100px;height:100px;shape-outside:inset(0% 50% 0% 50%)'></div>" +
            "<span id='b' style='display:inline-block;width:40px;height:20px'></span>" +
            "</div>"));

        // A shape that bounds nothing still has edges that count, and this one is the midline of the box.
        Assert.That(Bounds(view, "b").X, Is.EqualTo(50).Within(0.5), "the line's own place is the edge");
    }

    [Test]
    public async Task AHorizontalLineShapeHoldsNothing()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:200px;line-height:0'>" +
            "<div id='f' style='float:left;width:100px;height:100px;shape-outside:inset(50% 0% 0% 50%)'></div>" +
            "<span id='b' style='display:inline-block;width:40px;height:20px'></span>" +
            "</div>"));

        // The same shape turned the other way is an empty float area and has no effect on wrapping (§2).
        Assert.That(Bounds(view, "b").X, Is.EqualTo(0).Within(0.5), "a horizontal line leaves the line alone");
    }

    [Test]
    public async Task AFloatIsStillPlacedByItsMarginBoxBesideAShape()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:300px'>" +
            "<div id='f1' style='float:left;width:100px;height:60px;shape-outside:inset(0 100% 0 0)'></div>" +
            "<div id='f2' style='float:left;width:80px;height:60px'></div>" +
            "</div>"));

        // The first float's shape bounds nothing at all, and yet the second stands at its margin box's
        // edge: floats are positioned using their margin boxes, not the float area (CSS Shapes 1 §2).
        Assert.That(Bounds(view, "f1").X, Is.EqualTo(0));
        Assert.That(Bounds(view, "f2").X, Is.EqualTo(100), "a shape narrows the lines, never the row");
    }

    [Test]
    public async Task AnUnreadableShapeLeavesTheFloatOnItsMarginBox()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:200px;line-height:0'>" +
            "<div id='f' style='float:left;width:100px;height:100px;shape-outside:linear-gradient(red, blue)'></div>" +
            "<span id='b' style='display:inline-block;width:40px;height:20px'></span>" +
            "</div>"));

        // A shape drawn out of an image's alpha channel is not read here, and a value that cannot be read is
        // the keyword none: the float area unaffected rather than vanished.
        Assert.That(Bounds(view, "b").X, Is.EqualTo(100).Within(0.5), "no shape means the whole box holds");
    }

    [Test]
    public async Task AFloatThatInheritsTheShapeAboveItMeasuresItFromItsOwnBoxes()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:200px;line-height:0'>" +
            "<div id='host' style='shape-outside:inset(0 50% 0 0)'>" +
            "<div id='f' style='float:left;width:60px;height:60px;shape-outside:inherit'></div>" +
            "<span id='b' style='display:inline-block;width:20px;height:20px'></span>" +
            "</div>" +
            "</div>"));

        // The shape reaches down, but the half is a half of the float that wears it: 30 across, where the
        // box it was inherited from was 50.
        Assert.That(Bounds(view, "b").X, Is.EqualTo(30).Within(0.5), "a share of its own margin box");
    }

    [Test]
    public async Task AnEllipseHoldsTheLineAtItsWidestPointOfTheBand()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:300px;line-height:0'>" +
            "<div id='f' style='float:left;width:100px;height:100px;shape-outside:ellipse(50% 50% at 50% 100%)'></div>" +
            "<div style='height:60px'><span id='b1' style='display:inline-block;width:40px;height:60px'></span></div>" +
            "<div style='height:40px'><span id='b2' style='display:inline-block;width:40px;height:40px'></span></div>" +
            "</div>"));

        // The half of an ellipse stood on the box's bottom edge is nowhere near the top of it, so a line
        // standing over the first 60 pixels is held only as far as the ellipse has reached by y=60 - and the
        // next one, which spans the waist of it, is held to the whole box.
        Assert.That(Bounds(view, "b1").X, Is.EqualTo(80).Within(0.5), "the ellipse's reach by the band's further edge");
        Assert.That(Bounds(view, "b2").X, Is.EqualTo(100).Within(0.5));
    }

    [Test]
    public void ASvgBoxIsNoShapeForAFloat()
    {
        // `<shape-box>` is the four CSS boxes and nothing else, so a value naming one of the boxes an SVG
        // element answers is a value the grammar refuses - and a refused declaration behaves as none.
        Assert.That(
            ClipPathShape.ReadForFloat("fill-box", Dimensions, Font),
            Is.Null,
            "fill-box is not a shape box");

        Assert.That(
            ClipPathShape.ReadForFloat("circle(20px) stroke-box", Dimensions, Font),
            Is.Null,
            "and a shape beside one is refused with it");

        Assert.That(ClipPathShape.Read("fill-box", Dimensions, Font)!.Form, Is.EqualTo(ClipForm.Box));
    }

    [Test]
    public void AFloatShapeMeasuresItselfFromTheMarginBoxItDefaultedTo()
    {
        var shape = ClipPathShape.ReadForFloat("inset(10px 20px)", Dimensions, Font);

        Assert.That(shape, Is.Not.Null);
        Assert.That(shape!.Box, Is.EqualTo(ClipBox.Margin), "the shape's own default box, not the border box");

        var clip = ClipPathShape.Read("inset(10px 20px)", Dimensions, Font);

        Assert.That(clip!.Box, Is.EqualTo(ClipBox.Border), "where a clip still measures the border box");
    }

    /// <summary>
    /// One inline-block per line, each line as tall as the band it stands in: a block that establishes no
    /// formatting context of its own overlaps the float and has only its line boxes shortened, so the box
    /// inside it stands where the area leaves the line room to begin.
    /// </summary>
    [Test]
    public async Task AShapeSoSmallItBoundsNothingStillStandsACircleWhenItIsGivenAMargin()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:600px;line-height:0'>" +
            "<div id='f' style='float:left;width:200px;height:180px;shape-outside:circle(0px);shape-margin:90px'></div>" +
            Boxes(3) +
            "</div>"));

        // Where the shape encloses no area, the perpendicular is not defined and the margin's own circle
        // stands in for it (§6.3) - which is how a zero-sized circle with a 90-pixel margin comes to the same
        // area as `circle(90px)`, the reference the corpus states for it. The disc is centred on the middle
        // of the box and reaches x=190 at y=90, so a band only holds what the circle has grown to by the
        // height it stands across.
        Assert.That(Bounds(view, "b1").X, Is.EqualTo(100 + Math.Sqrt(8100 - 70 * 70)).Within(0.5));
        Assert.That(Bounds(view, "b2").X, Is.EqualTo(100 + Math.Sqrt(8100 - 50 * 50)).Within(0.5));
        Assert.That(Bounds(view, "b3").X, Is.EqualTo(100 + Math.Sqrt(8100 - 30 * 30)).Within(0.5));
    }

    [Test]
    public async Task AHorizontalLineStandsACapsuleOnceItIsGivenAMargin()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:600px;line-height:0'>" +
            "<div id='f' style='float:left;width:200px;height:180px;shape-outside:inset(50% 50% 50% 0);shape-margin:90px'></div>" +
            Boxes(1) +
            "</div>"));

        // The left half of the box's midline, swept out by 90, is a stadium whose right end is the circle of
        // that radius about the line's end point - and for a left float that is the only end that holds.
        Assert.That(Bounds(view, "b1").X, Is.EqualTo(100 + Math.Sqrt(8100 - 70 * 70)).Within(0.5));
    }

    private static String Boxes(Int32 count, String? prefix = null, Double height = 20) =>
        string.Join(
            string.Empty,
            Enumerable.Range(1, count).Select(index =>
                $"<div style='height:{height}px'>" +
                $"<span id='{prefix ?? "b"}{index}' style='display:inline-block;width:60px;height:{height}px'></span>" +
                "</div>"));

    private static Rect Bounds(HutuViewInternal view, String id)
    {
        var element = view.Document!.GetElementById(id);

        Assert.That(element, Is.Not.Null, $"no element with id '{id}' in the document");

        var bounds = view.GetElementBounds(element!);

        Assert.That(bounds, Is.Not.Null, $"'{id}' generated no box to measure");
        return bounds!.Value;
    }

    private static IRenderDimensions Dimensions => new StubDimensions();

    private static LengthReader.FontSource Font => new(16);

    private static String Page(String content) =>
        "<html><body style='margin:0'>" + content + "</body></html>";

    private static HutuViewInternal CreateView(Int32 viewportWidth = 600)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private sealed class StubDimensions : IRenderDimensions
    {
        public Double RenderWidth => 600;
        public Double RenderHeight => 600;
        public Double FontSize => 16;
    }
}

using Hutu.Rendering;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers <c>clip-path</c>: the shapes a page can write, the box they measure themselves against, and the
/// cut they come to when the box has been laid out.
/// </summary>
/// <remarks>
/// The property is a shape rather than a rectangle, so the answer splits across the same two moments the
/// legacy <c>clip</c> does: the shape belongs to the style and is read when the style is, and the area it
/// covers belongs to the box as laid out, because every per cent in it is a share of a box whose size the
/// layout has not settled at the time the declaration is read (CSS Masking 1 §5.1, CSS Shapes 1 §3). The
/// first of these tests read the shape off the style, the rest read the cut the painter lays around the box.
/// <para>
/// The numbers are the chapter's own, with two exceptions measured in Chrome on 2026-10-22: a circle's
/// per cent radius is a share of the box taken diagonally rather than of either axis, and the corners an
/// <c>inset()</c> is rounded with are shares of the reference box but fitted to the rectangle the insets
/// left - which is what makes <c>inset(11.2923% round 50% 50% 0 0)</c> of a 661x445 box come to
/// 255.86x172.25 corners rather than to 330.5x222.5 ones.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ClipPathTests
{
    private const UInt32 Blue = 0xFF00_00FF;
    private const UInt32 Red = 0xFFFF_0000;
    private const UInt32 Green = 0xFF00_8000;

    [Test]
    public async Task ABoxThatStatesNoShapeIsNotCutAtAll()
    {
        using var none = await Drawn("#b{clip-path:none}" + Box200x200);
        using var silent = await Drawn(Box200x200);

        Assert.Multiple(() =>
        {
            Assert.That(Cut(none, Green), Is.Null, "the default states no cut");
            Assert.That(Cut(silent, Green), Is.Null, "and a box that wrote nothing at all is not cut either");
        });
    }

    [Test]
    public async Task ARectangleIsInsetFromEachEdgeOfTheBoxItWasWrittenOn()
    {
        var cut = Only(await Shaped("#b{clip-path:inset(10px 20px 30px 40px)}"));

        Assert.That(Edges(cut.Bounds), Is.EqualTo((40d, 10d, 140d, 160d)),
            "the four numbers name top, right, bottom and left, and each is a distance in from that edge");
        Assert.That(cut.Outline, Is.Null, "a rectangle needs no outline drawn");
        Assert.That(cut.Radii.IsZero, Is.True, "and one with no corners named is square");
    }

    [Test]
    public async Task AnInsetPerCentIsAShareOfTheAxisItsEdgeRunsAlong()
    {
        var cut = Only(await Shaped("#b{clip-path:inset(10% 20% 30% 40%)}", Box200x100));

        Assert.That(Edges(cut.Bounds), Is.EqualTo((80d, 10d, 80d, 60d)),
            "a top or bottom per cent is a share of the height and a left or right one of the width");
    }

    [Test]
    public async Task InsetsThatPassEachOtherAreSharedBackToTheAxis()
    {
        // CSS Shapes 1 §3.1 reads the overlapping-curves rule of CSS Backgrounds 3 §4.5 back into the
        // insets: the pair sums to the axis and each keeps the share of it it was written with.
        var cut = Only(await Shaped("#b{clip-path:inset(75% 0 50% 0)}", Box200x100));

        Assert.That(Edges(cut.Bounds), Is.EqualTo((0d, 60d, 200d, 0d)),
            "75 and 50 of a hundred are shared back to 60 and 40, and nothing is left between them");
    }

    [Test]
    public async Task TheFrameOfReferenceIsTheBorderBoxUnlessThePageNamesAnother()
    {
        var border = Only(await Shaped("#b{clip-path:border-box}", Framed));
        var content = Only(await Shaped("#b{clip-path:content-box}", Framed));
        var padding = Only(await Shaped("#b{clip-path:padding-box}", Framed));

        Assert.Multiple(() =>
        {
            Assert.That(Edges(border.Bounds), Is.EqualTo((50d, 50d, 200d, 200d)),
                "the box stands where its own margin left it, and the frame is the border box");
            Assert.That(Edges(padding.Bounds), Is.EqualTo((60d, 60d, 180d, 180d)), "the border is off the inside");
            Assert.That(Edges(content.Bounds), Is.EqualTo((80d, 80d, 140d, 140d)), "and the padding off that");
        });
    }

    [Test]
    public async Task AFrameNamedBesideTheShapeIsTheFrameTheShapeUses()
    {
        const String Wide = "#b{clip-path:polygon(0% 0%, 100% 0%, 100% 100%, 0% 100%)}";
        const String Margin = "#b{clip-path:polygon(0% 0%, 100% 0%, 100% 100%, 0% 100%) margin-box}";

        var border = Only(await Shaped(Wide, Framed));
        var margin = Only(await Shaped(Margin, Framed));

        Assert.Multiple(() =>
        {
            Assert.That(Edges(border.Bounds), Is.EqualTo((50d, 50d, 200d, 200d)));
            Assert.That(Edges(margin.Bounds), Is.EqualTo((25d, 25d, 250d, 250d)),
                "the margins the box was given come back into the frame it measures itself against - of a box " +
                "that has them, which an inline does not");
        });
    }

    [Test]
    public async Task TheSvgFramesAnswerTheBoxesTheLayoutMadeForThem()
    {
        // CSS Masking 1 §5.1: for a box the layout made, `fill-box` is the content box and `stroke-box`
        // and `view-box` the border one - which is what the polygon-011 and -012 pairs measure.
        var fill = Only(await Shaped("#b{clip-path:fill-box}", Framed));
        var stroke = Only(await Shaped("#b{clip-path:stroke-box}", Framed));
        var view = Only(await Shaped("#b{clip-path:view-box}", Framed));

        Assert.Multiple(() =>
        {
            Assert.That(Edges(fill.Bounds), Is.EqualTo((80d, 80d, 140d, 140d)));
            Assert.That(Edges(stroke.Bounds), Is.EqualTo((50d, 50d, 200d, 200d)));
            Assert.That(Edges(view.Bounds), Is.EqualTo((50d, 50d, 200d, 200d)));
        });
    }

    [Test]
    public async Task AnInlineBoxIsCutToTheFragmentsItWasLaidOutIn()
    {
        // A non-replaced inline has no margin box: the vertical padding and border it is given are painted
        // and do widen the fragment's border box, but the line it stands on is not grown by them, and
        // CSS Box Alignment 4 §3.1 answers an inline's margin box with its border box - which is what the
        // `clip-path-inline-004` pair measures with `circle(25%) margin-box` on a span wearing margins.
        using var view = await Drawn(
            "#b{clip-path:circle(50%) margin-box;padding:10px 20px;margin:30px;background-color:#008000}",
            "<p style='margin:0'><span id='b'>X</span></p>");

        var box = Box(view, "b");
        var cut = Only(Cut(view, Green));

        Assert.Multiple(() =>
        {
            var diagonal = Math.Sqrt(
                (box.BorderBox.Width * box.BorderBox.Width + box.BorderBox.Height * box.BorderBox.Height) / 2);

            Assert.That(cut.Bounds.Width, Is.EqualTo(diagonal).Within(0.001),
                "half the diagonal of the border box is the diameter the per cent was a share of");
            Assert.That(cut.Bounds.X + cut.Bounds.Width / 2,
                Is.EqualTo(box.BorderBox.X + box.BorderBox.Width / 2).Within(0.001),
                "and the shape stands at the centre of the box it was drawn with, margins and all");
            Assert.That(cut.Bounds.Y + cut.Bounds.Height / 2,
                Is.EqualTo(box.BorderBox.Y + box.BorderBox.Height / 2).Within(0.001),
                "the vertical margins gave the centre no shift, because an inline has no margin box");
            Assert.That(box.BorderBox.Width, Is.GreaterThan(box.ContentBox.Width),
                "and the padding the span wore is part of that box");
        });
    }

    [Test]
    public async Task ACircleTakesItsPerCentRadiusFromTheBoxMeasuredDiagonally()
    {
        var cut = Only(await Shaped("#b{clip-path:circle(25%)}", Box300x100));

        // sqrt((300^2 + 100^2) / 2) = 223.6068, and a quarter of it is neither a quarter of the width nor
        // of the height, which is what makes `circle(25%)` of one box answer `circle(50%)` of another.
        var radius = Math.Sqrt((300 * 300 + 100 * 100) / 2) / 4;

        Assert.Multiple(() =>
        {
            Assert.That(cut.Bounds.X, Is.EqualTo(150 - radius).Within(0.001));
            Assert.That(cut.Bounds.Y, Is.EqualTo(50 - radius).Within(0.001));
            Assert.That(cut.Bounds.Width, Is.EqualTo(radius * 2).Within(0.002));
            Assert.That(cut.Bounds.Height, Is.EqualTo(radius * 2).Within(0.002));
            Assert.That(cut.Radii.TopLeft.Horizontal, Is.EqualTo(radius).Within(0.001),
                "an oval is a rectangle with every corner turned all the way round");
            Assert.That(cut.Radii.TopLeft.Vertical, Is.EqualTo(radius).Within(0.001));
        });
    }

    [Test]
    public async Task ACircleReachesTheEdgeOrCornerThePageNames()
    {
        var side = Only(await Shaped("#b{clip-path:circle(farthest-side at 50px 50px)}", Box200x100));
        var corner = Only(await Shaped("#b{clip-path:circle(closest-corner at 50px 50px)}", Box200x100));
        var stated = Only(await Shaped("#b{clip-path:circle()}", Box200x100));

        Assert.Multiple(() =>
        {
            Assert.That(side.Bounds.Width, Is.EqualTo(300), "the farthest edge of the box is 150 away");
            Assert.That(corner.Bounds.Width, Is.EqualTo(70.7107 * 2).Within(0.002),
                "the nearest corner is 70.71 away, and the diameter is twice it");
            Assert.That(stated.Bounds.Width, Is.EqualTo(Math.Sqrt(100 * 100 + 50 * 50) * 2).Within(0.002),
                "a circle left with nothing to say reaches the farthest corner");
        });
    }

    [Test]
    public async Task AnEllipseTakesEachAxisFromItsOwnExtent()
    {
        var cut = Only(await Shaped("#b{clip-path:ellipse(30% 25% at 50% 50%)}", Box200x100));

        Assert.Multiple(() =>
        {
            Assert.That(Edges(cut.Bounds), Is.EqualTo((40d, 25d, 120d, 50d)),
                "the two radii are shares of the width and of the height, not of the diagonal");
            Assert.That(cut.Radii.TopLeft.Horizontal, Is.EqualTo(60));
            Assert.That(cut.Radii.TopLeft.Vertical, Is.EqualTo(25));
        });
    }

    [Test]
    public async Task AnEllipseReachesEachAxisOfTheBoxOnItsOwn()
    {
        var cut = Only(await Shaped("#b{clip-path:ellipse(farthest-side closest-side)}", Box200x100));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Bounds.Width, Is.EqualTo(200), "the horizontal radius reaches the farther side");
            Assert.That(cut.Bounds.Height, Is.EqualTo(100), "and the vertical one the nearer, which are equal here");
        });
    }

    [Test]
    public async Task APolygonPutsEachPointOnTheBoxItWasDrawnAgainst()
    {
        using var view = await Drawn(
            "#b{clip-path:polygon(50px 50px, 200px 50px, 200px 150px, 50px 150px)}",
            "<div id='b' style='position:absolute;top:30px;left:20px;width:150px;height:100px;" +
                "background-color:#008000'></div>");

        var cut = Only(Cut(view, Green));
        var points = cut.Outline!.Where(edge => edge.Edge != ClipEdge.Close).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(points.Select(edge => (edge.X, edge.Y)),
                Is.EquivalentTo(new[] { (70d, 80d), (220d, 80d), (220d, 180d), (70d, 180d) }),
                "the shape's own corner is the box's corner, and the page stood the box at 20,30");
            Assert.That(points.Count(edge => edge.Edge == ClipEdge.Begin), Is.EqualTo(1),
                "one sub-shape, opened once");
            Assert.That(cut.Outline!.Count(edge => edge.Edge == ClipEdge.Close), Is.EqualTo(1),
                "and closed, because an outline that is not closed is still a shape");
            Assert.That(cut.EvenOdd, Is.False, "winding is the rule a polygon answers with by default");
        });
    }

    [Test]
    public async Task APolygonKeepsTheWindingRuleThePageNamed()
    {
        var even = Only(await Shaped("#b{clip-path:polygon(evenodd, 0 0, 200px 0, 200px 200px, 0 200px)}", Box200x100));
        var nonzero = Only(await Shaped("#b{clip-path:polygon(nonzero, 0 0, 200px 0, 200px 200px, 0 200px)}", Box200x100));

        Assert.Multiple(() =>
        {
            Assert.That(even.EvenOdd, Is.True);
            Assert.That(nonzero.EvenOdd, Is.False, "the rule the grammar names first is asked for by name");
        });
    }

    [Test]
    public async Task APathIsMovedOntoTheBoxFromTheCornerItsOwnNumbersStartAt()
    {
        using var view = await Drawn(
            "#b{clip-path:path('M0,0 L100,0 L0,100 L0,0')}",
            "<div id='b' style='position:absolute;top:40px;left:40px;width:100px;height:100px;" +
                "background-color:#008000'></div>");

        var cut = Only(Cut(view, Green));

        Assert.Multiple(() =>
        {
            Assert.That(
                cut.Outline!.Select(edge => (edge.Edge, edge.X, edge.Y)).ToList(),
                Is.EqualTo(new[]
                {
                    (ClipEdge.Begin, 40d, 40d),
                    (ClipEdge.Line, 140d, 40d),
                    (ClipEdge.Line, 40d, 140d),
                    (ClipEdge.Line, 40d, 40d),
                }),
                "the string's numbers are pixels against the reference box, so the box's corner is added, " +
                "and a sub-shape the page left open still ends where it was written to end");
        });
    }

    [Test]
    public async Task APathClosesTheShapeItsStringLeavesOpen()
    {
        var closed = Only(await Shaped("#b{clip-path:path(nonzero, 'm 0,0 l 10,0 l 0,10 z')}", Box200x100));

        Assert.Multiple(() =>
        {
            Assert.That(
                closed.Outline!.Select(edge => (edge.X, edge.Y)).Take(3),
                Is.EqualTo(new[] { (0d, 0d), (10d, 0d), (10d, 10d) }),
                "a relative move starts at the corner and each pair after it is read against the point before");
            Assert.That(closed.Outline!.Last().Edge, Is.EqualTo(ClipEdge.Close));
        });
    }

    [Test]
    public async Task AQuadraticCurveIsCarriedIntoTheOneKindOfEdgeTheCutIsDrawnWith()
    {
        var cut = Only(await Shaped("#b{clip-path:path('M 0 0, Q 10 20, 30 40')}", Box200x100));
        var edge = cut.Outline!.Single(piece => piece.Edge == ClipEdge.Curve);

        Assert.Multiple(() =>
        {
            Assert.That(edge.X, Is.EqualTo(30));
            Assert.That(edge.Y, Is.EqualTo(40));
            Assert.That(edge.Control1X, Is.EqualTo(20 / 3.0).Within(0.0001),
                "a quadratic's first cubic control stands two thirds of the way from the start to its focus");
            Assert.That(edge.Control1Y, Is.EqualTo(40 / 3.0).Within(0.0001));
            Assert.That(edge.Control2X, Is.EqualTo(30 + (2 / 3.0) * (10 - 30)).Within(0.0001));
        });
    }

    [Test]
    public async Task ARoundedInsetFitsItsCornersToTheRectangleLeftBehind()
    {
        // Measured in Chrome: 661x445 with `inset(11.2923% round 50% 50% 0 0)` comes to the same corners as
        // `inset(50.25px 74.64px round 255.86px 255.86px 0px 0px / 172.25px 172.25px 0px 0px)`. The radii
        // are shares of the reference box, and the two of them on a side are then fitted to the inset
        // rectangle - which is the overlap rule, and where the 330.5 the halves come to is shrunk to 255.86.
        var cut = Only(await Shaped(
            "#b{clip-path:inset(11.2923% round 50% 50% 0 0)}",
            "#b{position:absolute;top:0;left:0;width:661px;height:445px;background-color:#008000}"));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Bounds.X, Is.EqualTo(74.64).Within(0.01));
            Assert.That(cut.Bounds.Y, Is.EqualTo(50.25).Within(0.01));
            Assert.That(cut.Radii.TopLeft.Horizontal, Is.EqualTo(255.86).Within(0.02));
            Assert.That(cut.Radii.TopLeft.Vertical, Is.EqualTo(172.25).Within(0.02));
            Assert.That(cut.Radii.BottomRight.IsZero, Is.True, "the corners the page left out stay square");
        });
    }

    [Test]
    public async Task ARoundCornerWrittenInPerCentsOfBothAxesIsReadAsOneEllipticalCorner()
    {
        var cut = Only(await Shaped("#b{clip-path:inset(10px round 25% / 10%)}", Box200x100));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Radii.TopLeft.Horizontal, Is.EqualTo(50), "before the slash is the width's share");
            Assert.That(cut.Radii.TopLeft.Vertical, Is.EqualTo(10), "and after it the height's");
            Assert.That(cut.Radii.BottomRight.Horizontal, Is.EqualTo(50), "one name stands for all four corners");
        });
    }

    [Test]
    public async Task ARectangleNamedByItsOwnPlaceAndSizeIsTheSameCut()
    {
        var place = Only(await Shaped("#b{clip-path:xywh(50px 50px 150px 100px)}", Box400x200));
        var edges = Only(await Shaped("#b{clip-path:rect(50px 200px 150px 50px)}", Box400x200));

        Assert.Multiple(() =>
        {
            Assert.That(Edges(place.Bounds), Is.EqualTo((50d, 50d, 150d, 100d)));
            Assert.That(Edges(edges.Bounds), Is.EqualTo(Edges(place.Bounds)),
                "the two grammars say where the edges stand, and the shapes compute to one rectangle");
        });
    }

    [Test]
    public async Task AnEdgeLeftToAutoIsTheEdgeOfTheBoxItWasWrittenOn()
    {
        var cut = Only(await Shaped("#b{clip-path:rect(auto 200px 150px auto)}", Box400x200));

        Assert.That(Edges(cut.Bounds), Is.EqualTo((0d, 0d, 200d, 150d)),
            "an edge left out is not inset at all (CSS Shapes 1 §3.1)");
    }

    [Test]
    public async Task ASizeThatIsNegativeIsNoShapeAtAll()
    {
        var cut = await Shaped("#b{clip-path:xywh(50px 50px -150px 100px)}", Box400x200);

        Assert.That(cut, Is.Null, "the grammar takes a size of zero or more, and a value outside it is dropped");
    }

    [Test]
    public async Task EveryValueThisGrammarCannotReadCutsNothing()
    {
        var refused = await Task.WhenAll(
            Shaped("#b{clip-path:polygon(0 0, 10px 10px)}", Box200x100),
            Shaped("#b{clip-path:circle(10px 20px)}", Box200x100),
            Shaped("#b{clip-path:ellipse(10px)}", Box200x100),
            Shaped("#b{clip-path:url(#clip)}", Box200x100),
            Shaped("#b{clip-path:polygon(0 0, 10px 10px) margin-box extra}", Box200x100),
            Shaped("#b{clip-path:shape(from 0px 0px, hline by 80px, close)}", Box200x100));

        Assert.Multiple(() =>
        {
            Assert.That(refused[0], Is.Null, "two points are not a shape three makes");
            Assert.That(refused[1], Is.Null, "a circle has one radius, not two");
            Assert.That(refused[2], Is.Null, "and an ellipse has both of them or neither");
            Assert.That(refused[3], Is.Null, "an SVG clipPath is a shape this reader was not given");
            Assert.That(refused[4], Is.Null, "a word the grammar has no place for is no declaration");
            Assert.That(refused[5], Is.Null, "and a shape from another chapter is not read as nothing");
        });
    }

    [Test]
    public async Task ABoxCutToAShapeStandsOnALayerOfItsOwn()
    {
        using var shaped = await Drawn("#b{clip-path:inset(10px)}", "<div id='b'></div>");
        using var plain = await Drawn("#b{clip-path:none}", "<div id='b'></div>");

        Assert.Multiple(() =>
        {
            Assert.That(Box(shaped, "b").EstablishesStackingContext, Is.True,
                "a computed value other than none stands a stacking context (CSS Masking 1 §5.1)");
            Assert.That(Box(plain, "b").EstablishesStackingContext, Is.False, "and the default stands none");
        });
    }

    [Test]
    public async Task TheCutClosesAfterTheBoxAndWhatItHoldsIsPaintedInsideIt()
    {
        using var view = await Drawn(
            "#outer{position:absolute;top:0;left:0;width:100px;height:100px;background-color:#008000;" +
                "clip-path:inset(10px 20px)}" +
                "#b{width:300px;height:300px;background-color:rgb(0 0 255)}",
            "<div id='outer'><div id='b'></div></div>");

        var commands = view.Paint!.Commands.ToList();
        var push = commands.FindIndex(command => command is PushClipCommand);
        var pop = commands.FindIndex(command => command is PopClipCommand);
        var own = commands.FindIndex(command => command is FillCommand here && here.Color == Green);
        var child = commands.FindIndex(command => command is FillCommand here && here.Color == Blue);

        Assert.Multiple(() =>
        {
            Assert.That(push, Is.GreaterThanOrEqualTo(0));
            Assert.That(Edges(commands[push].Bounds), Is.EqualTo((20d, 10d, 60d, 80d)));
            Assert.That(own, Is.GreaterThan(push), "the box's own background is inside the cut it wrote");
            Assert.That(child, Is.GreaterThan(own), "and so is the box standing within it");
            Assert.That(pop, Is.GreaterThan(child), "the pair closes after the last of them");
        });
    }

    [Test]
    public async Task TheCutOfABoxIsNoPartOfTheBoxBelowIt()
    {
        using var view = await Drawn(
            "#outer{position:absolute;top:0;left:0;width:100px;height:100px;background-color:#008000;" +
                "clip-path:inset(10px)}" +
                "#b{position:absolute;top:200px;left:200px;width:20px;height:20px;background-color:rgb(0 0 255)}",
            "<div id='outer'></div><div id='b'></div>");

        Assert.Multiple(() =>
        {
            Assert.That(view.Paint!.Commands.OfType<PushClipCommand>().ToList(), Has.Count.EqualTo(1),
                "one box wrote one shape");
            Assert.That(Standing(view, Blue), Is.Empty,
                "and the box that stands beside it is painted under no cut of its own");
            Assert.That(Standing(view, Green), Has.Count.EqualTo(1),
                "while the box that wrote it is painted inside it");
        });
    }

    [Test]
    public async Task AChildThatAsksForTheShapeAboveItMeasuresItFromItsOwnBoxes()
    {
        using var view = await Drawn(
            "#outer{clip-path:inset(10px 20px)}#b{clip-path:inherit;background-color:rgb(0 0 255)}",
            "<div id='outer'><div id='b' style='width:200px;height:200px'></div></div>");

        var cut = CutOf(view, "b");

        Assert.Multiple(() =>
        {
            Assert.That(Edges(cut.Bounds), Is.EqualTo((20d, 10d, 160d, 180d)),
                "the shape comes down, and is measured from the box that asks, which is the one the page sized");
            Assert.That(Standing(view, Blue)[^1].Bounds, Is.EqualTo(cut.Bounds),
                "and the box is painted inside the cut the keyword asked for");
        });
    }

    [Test]
    public async Task AShapeCutToNothingTakesTheBoxOutOfThePictureButLeavesItDrawn()
    {
        var cut = Only(await Shaped("#b{clip-path:inset(0 200px 100px 0)}", Box200x100));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Bounds, Is.EqualTo(Rect.Empty), "the two insets meet and leave no area");
            Assert.That(cut.Outline, Is.Null, "the cut is still the rectangle the shape came to");
        });
    }

    [Test]
    public async Task TheDocumentElementCutsTheBackdropTheCanvasWears()
    {
        // The root's background is not painted by the root: CSS Backgrounds 3 §2.11 gives it to the
        // canvas, which the document's own picture is drawn over. A shape on the document element cuts
        // that backdrop with everything else it paints (CSS Masking 1 §5.1, and what the
        // clip-path-document-element pair is built to catch).
        using var view = await Drawn(
            "html{background-color:rgb(255 0 0);clip-path:polygon(0 0, 100px 0, 100px 100px, 0 100px)}",
            "<div style='width:300px;height:300px;background-color:#008000'></div>");

        var standing = Standing(view, Red);

        Assert.Multiple(() =>
        {
            Assert.That(standing, Has.Count.EqualTo(1), "the page's own colour stands inside one cut");
            Assert.That(standing[0].Outline, Is.Not.Null, "and the cut is the shape the document wrote");
            Assert.That(Edges(standing[0].Bounds), Is.EqualTo((0d, 0d, 100d, 100d)));
            Assert.That(Standing(view, Green), Has.Count.EqualTo(1),
                "the box within the page is cut by the same shape, and not by a second pair of it");
        });
    }

    /// <summary>Gets the cuts standing over the ink of the colour asked for, outermost first.</summary>
    private static List<ClipCut> Standing(HutuViewInternal view, UInt32 ink)
    {
        var standing = new List<ClipCut>();

        foreach (var command in view.Paint!.Commands)
        {
            switch (command)
            {
                case PushClipCommand pushed:
                    standing.Add(Of(pushed));
                    break;

                case PopClipCommand:
                    standing.RemoveAt(standing.Count - 1);
                    break;

                case FillCommand drawn when drawn.Color == ink:
                    return standing;
            }
        }

        return standing;
    }

    private static ClipCut Of(PushClipCommand clip) =>
        new(clip.Bounds, clip.Radii, clip.Outline, clip.EvenOdd);

    /// <summary>Gets the one cut the page's single shape came to, and says what it was that there is one.</summary>
    private static ClipCut? Cut(HutuViewInternal view, UInt32 ink)
    {
        var standing = Standing(view, ink);

        Assert.That(standing, Has.Count.AtMost(1), "one shape was written, so one pair stands around the ink");

        return standing.Count == 0 ? null : standing[0];
    }

    /// <summary>Gets the cut the box's own shape comes to on the box as the layout left it.</summary>
    private static ClipCut CutOf(HutuViewInternal view, String id)
    {
        var shape = Box(view, id).Source.Style.ClipPath;

        Assert.That(shape, Is.Not.Null, "the box was asked to write a shape");

        return shape!.Cut(BoxesOf(Box(view, id)));
    }

    /// <summary>Gets the four boxes the layout settled for the box, which is what a shape measures against.</summary>
    private static ClipBoxes BoxesOf(LayoutBox box)
    {
        var border = box.BorderBox;
        var margin = box.Margin;

        return new ClipBoxes(
            border,
            box.PaddingBox,
            box.ContentBox,
            new Rect(
                border.X - margin.Left,
                border.Y - margin.Top,
                border.Width + margin.Left + margin.Right,
                border.Height + margin.Top + margin.Bottom));
    }

    /// <summary>Gets the one shape a page wrote, drawn on a box of the geometry the test asked for.</summary>
    private static async Task<ClipCut?> Shaped(String css, String frame = Box200x200)
    {
        using var view = await Drawn($"{css}{frame}", "<div id='b'></div>");

        return Cut(view, Green);
    }

    /// <summary>Gets the one cut a test wrote, and says what it was that there should be one.</summary>
    private static ClipCut Only(ClipCut? cut)
    {
        Assert.That(cut, Is.Not.Null, "a shape was written, and a cut should come of it");

        return cut!.Value;
    }

    // Every test box is filled, so that the cut standing over it is the one that can be read off the list.
    private const String Box200x200 =
        "#b{position:absolute;top:0;left:0;width:200px;height:200px;background-color:#008000}";

    private const String Box200x100 =
        "#b{position:absolute;top:0;left:0;width:200px;height:100px;background-color:#008000}";

    private const String Box300x100 =
        "#b{position:absolute;top:0;left:0;width:300px;height:100px;background-color:#008000}";

    private const String Box400x200 =
        "#b{position:absolute;top:0;left:0;width:400px;height:200px;background-color:#008000}";

    /// <summary>A box with a border, a padding and a margin, so that each frame has its own answer.</summary>
    private const String Framed =
        "#b{position:absolute;top:25px;left:25px;width:140px;height:140px;background-color:#008000;" +
            "padding:20px;border:10px solid rgb(255 0 0);margin:25px}";

    /// <summary>Gets the page, painted.</summary>
    private static async Task<HutuViewInternal> Drawn(String css, String body = "<div id='b'></div>")
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml($"<html><head><style>{css}</style></head><body style='margin:0'>{body}</body></html>");

        return view;
    }

    private static LayoutBox Box(HutuViewInternal view, String id) =>
        Walk(view.Layout!.Root).First(node => node.Source.Element?.GetAttribute("id") == id);

    /// <summary>Gets the four numbers of a rectangle, which is how these tests compare one.</summary>
    private static (Double X, Double Y, Double Width, Double Height) Edges(Rect area) =>
        (area.X, area.Y, area.Width, area.Height);

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
}

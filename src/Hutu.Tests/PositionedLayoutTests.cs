using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Positioning asserted through real documents: a relative box is displaced without moving its
/// siblings, an absolute or fixed box leaves the flow and resolves against the right containing
/// block, and the paint order follows the stacking levels.
/// </summary>
[TestFixture]
public sealed class PositionedLayoutTests
{
    [Test]
    public async Task ARightToLeftRowKeepsTheRightOffsetOfAnOverconstrainedBox()
    {
        using var view = CreateView(300);
        await view.SetHtml(
            "<html><body style='margin:0;direction:rtl'>" +
            "<div id='a' style='position:relative;left:40px;right:40px;width:100px;height:10px'></div>" +
            "</body></html>");

        var a = Find(view, "a");

        Assert.That(
            a.BorderBox.X,
            Is.EqualTo(160),
            "the box stands against the right edge at 200 and `right` moves it 40 further left");
    }

    [Test]
    public async Task ALeftToRightRowKeepsTheLeftOffsetOfAnOverconstrainedBox()
    {
        using var view = CreateView(300);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='a' style='position:relative;left:40px;right:40px;width:100px;height:10px'></div>" +
            "</body></html>");

        var a = Find(view, "a");

        Assert.That(a.BorderBox.X, Is.EqualTo(40), "the box stands at the left edge and `left` moves it 40 right");
    }

    [Test]
    public async Task RelativeMovesTheBoxButNotItsSiblings()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:10px'></div>" +
            "<div id='a' style='position:relative;top:20px;left:30px;height:10px'></div>" +
            "<div id='b' style='height:10px'></div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");

        Assert.That(a.BorderBox.Y, Is.EqualTo(30).Within(0.5), "10 + the 20px offset");
        Assert.That(a.BorderBox.X, Is.EqualTo(30).Within(0.5), "the 30px offset");
        Assert.That(b.BorderBox.Y, Is.EqualTo(20).Within(0.5), "the sibling keeps the place the offset left behind");
    }

    [Test]
    public async Task RelativeMovesEveryFragmentOfAnInlineBox()
    {
        // A relative element keeps the place it had in the flow and is drawn where its offset puts it.
        // An inline element is drawn as one fragment on every line it reaches, so each of them is moved
        // - and the text, background and line drawn through the fragment go with it (CSS 2.1 §9.4.3).
        var moved = await Fragments("position:relative;top:10px;left:6px;");
        var plain = await Fragments("");

        Assert.That(moved.Count, Is.GreaterThan(1), "the element was broken across lines");
        Assert.That(
            moved.Select(box => box.Y),
            Is.EqualTo(plain.Select(box => box.Y + 10)),
            "every fragment stands ten pixels lower than the place it had");
        Assert.That(
            moved.Select(box => box.X),
            Is.EqualTo(plain.Select(box => box.X + 6)),
            "and six pixels along from where it stood");
    }

    [Test]
    public async Task RelativeMovesTheFragmentsOfAColumnToo()
    {
        // The offsets are physical, so the same ten pixels move a fragment down the page whether the
        // text reads along a row or runs down a column.
        var moved = await Fragments("position:relative;top:10px;left:6px;", column: true);
        var plain = await Fragments("", column: true);

        Assert.That(
            moved.Select(box => box.Y),
            Is.EqualTo(plain.Select(box => box.Y + 10)),
            "the fragments run down the page ten pixels further along");
        Assert.That(
            moved.Select(box => box.X),
            Is.EqualTo(plain.Select(box => box.X + 6)),
            "and six pixels across from where they stood");
    }

    /// <summary>
    /// Lays one page out with the given style on an inline element broken over two lines, and returns
    /// where each of its fragments was drawn.
    /// </summary>
    private static async Task<List<(Double X, Double Y)>> Fragments(String style, Boolean column = false)
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;width:120px;font-family:Segoe UI;" +
            "font-size:40px;" + (column ? "writing-mode:vertical-rl;" : "") + "'>aa<span id='s' style='" +
            style + "'>bbee<br/>ffoo</span>cc</p></body></html>");

        var fragments = Walk(view.Layout!.Root)
            .Where(node => node.Source.Element?.Id == "s")
            .Select(node => (node.BorderBox.X, node.BorderBox.Y))
            .ToList();

        Assert.That(fragments, Is.Not.Empty, "the element was laid out");
        return fragments;
    }

    [Test]
    public async Task AbsoluteAnchorsToTheNearestPositionedAncestor()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='outer' style='position:relative;width:300px;height:200px'>" +
            "<div id='a' style='position:absolute;left:10px;top:20px;width:50px;height:30px'></div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");

        Assert.That(a.BorderBox.X, Is.EqualTo(10).Within(0.5));
        Assert.That(a.BorderBox.Y, Is.EqualTo(20).Within(0.5));
        Assert.That(a.BorderBox.Width, Is.EqualTo(50).Within(0.5));
        Assert.That(a.BorderBox.Height, Is.EqualTo(30).Within(0.5));
    }

    [Test]
    public async Task AbsoluteBlockifiesTheBoxThatWasInline()
    {
        using var view = CreateView(400);

        // A picture is inline, and a page that pins one to an offset of its containing block means it
        // to leave the line it was written on. Left on the line, the offsets ask nothing of a box the
        // line is placing, and the line grows to the height of the picture - so a logo declared at
        // <c>top: 158px</c> is drawn at the head of its block and makes that block a hundred and
        // fifty-eight pixels taller than anyone asked for.
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='wrap' style='position:relative;width:200px'>" +
            "<img id='p' src='x.png' style='position:absolute;top:60px;left:20px;width:40px;height:30px'>" +
            "text" +
            "</div></body></html>");

        var wrap = Find(view, "wrap");

        // A picture is its own kind of box rather than a block, so it is looked up by its identity.
        var picture = Walk(view.Layout!.Root).First(node => node.Source.Element?.Id == "p");

        Assert.That(picture.BorderBox.X, Is.EqualTo(20).Within(0.5), "the declared left, not the place on the line");
        Assert.That(picture.BorderBox.Y, Is.EqualTo(60).Within(0.5), "the declared top");
        Assert.That(
            wrap.BorderBox.Height,
            Is.LessThan(60),
            "a box that left the flow holds nothing up: the block is as tall as its one line");
    }

    [Test]
    public async Task AbsoluteResolvesAgainstThePaddingBoxOfItsAncestor()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='outer' style='position:relative;box-sizing:border-box;width:200px;height:100px;" +
            "padding:20px;border:10px solid #000'>" +
            "<div id='a' style='position:absolute;left:0;top:0;width:10px;height:10px'></div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");

        // The containing block is the padding box, so it starts inside the ancestor's 10px border.
        Assert.That(a.BorderBox.X, Is.EqualTo(10).Within(0.5));
        Assert.That(a.BorderBox.Y, Is.EqualTo(10).Within(0.5));
    }

    [Test]
    public async Task AbsoluteWithBothEdgesFillsTheGapBetweenThem()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='outer' style='position:relative;width:300px;height:100px'>" +
            "<div id='a' style='position:absolute;left:20px;right:30px;top:0;height:10px'></div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");

        Assert.That(a.BorderBox.X, Is.EqualTo(20).Within(0.5));
        Assert.That(a.BorderBox.Width, Is.EqualTo(250).Within(0.5), "300 - 20 - 30");
    }

    [Test]
    public async Task AbsoluteWithOnlyTheRightEdgeAnchorsToIt()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='outer' style='position:relative;width:300px;height:100px'>" +
            "<div id='a' style='position:absolute;right:10px;top:0;width:40px;height:10px'></div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");

        Assert.That(a.BorderBox.Right, Is.EqualTo(290).Within(0.5), "300 - 10 from the right edge");
    }

    [Test]
    public async Task AnOutOfFlowBoxDoesNotStretchItsParent()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='outer' style='width:300px'>" +
            "<div style='height:50px'></div>" +
            "<div id='a' style='position:absolute;top:0;left:0;width:10px;height:500px'></div>" +
            "</div>" +
            "<div id='after' style='height:10px'></div>" +
            "</body></html>");

        var outer = Find(view, "outer");
        var after = Find(view, "after");

        Assert.That(outer.BorderBox.Height, Is.EqualTo(50).Within(0.5), "the absolute box takes no space");
        Assert.That(after.BorderBox.Y, Is.EqualTo(50).Within(0.5), "so the next block closes the gap");
    }

    [Test]
    public async Task FixedResolvesAgainstTheViewportAndPaintsIntoItsOwnLayer()
    {
        using var view = CreateView(400, 600);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:1000px'></div>" +
            "<div id='f' style='position:fixed;top:10px;left:20px;width:50px;height:30px;background:#0000ff'></div>" +
            "</body></html>");

        var fixedBox = Find(view, "f");

        Assert.That(fixedBox.BorderBox.X, Is.EqualTo(20).Within(0.5));
        Assert.That(fixedBox.BorderBox.Y, Is.EqualTo(10).Within(0.5), "viewport coordinates, not document ones");
        Assert.That(fixedBox.BorderBox.Width, Is.EqualTo(50).Within(0.5));

        var paint = view.Paint!;

        Assert.That(paint.FixedCommands.OfType<FillCommand>().Any(fill => fill.Color == 0xFF0000FFu), Is.True);
        Assert.That(paint.Commands.OfType<FillCommand>().Any(fill => fill.Color == 0xFF0000FFu), Is.False, "a fixed box is not part of the document layer");
    }

    [Test]
    public async Task RelativeCarriesItsAbsolutelyPositionedDescendant()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='outer' style='position:relative;top:50px;width:200px;height:100px'>" +
            "<div id='a' style='position:absolute;left:0;top:0;width:10px;height:10px'></div>" +
            "</div>" +
            "</body></html>");

        var outer = Find(view, "outer");
        var a = Find(view, "a");

        Assert.That(outer.BorderBox.Y, Is.EqualTo(50).Within(0.5));
        Assert.That(a.BorderBox.Y, Is.EqualTo(50).Within(0.5), "the subtree moves as one");
    }

    [Test]
    public async Task StickyProducesAPinnedLayerThatShiftsWithTheScroll()
    {
        using var view = CreateView(400, 600);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:1000px'></div>" +
            "<div id='s' style='position:sticky;top:20px;height:40px'></div>" +
            "<div style='height:1000px'></div>" +
            "</body></html>");

        var sticky = Find(view, "s");

        Assert.That(sticky.BorderBox.Y, Is.EqualTo(1000).Within(0.5), "static until the threshold is crossed");

        var paint = view.Paint!;

        Assert.That(paint.Sticky, Has.Count.EqualTo(1));

        var constraint = paint.Sticky[0].Constraint;

        Assert.That(constraint.Shift(Offset.Zero).Y, Is.EqualTo(0).Within(0.5), "no shift before it is reached");
        Assert.That(constraint.Shift(new Offset(0, 1050)).Y, Is.EqualTo(70).Within(0.5), "pinned 20px below the top of the scrollport");
    }

    [Test]
    public async Task PositiveZIndexPaintsHigherLevelsLast()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='position:relative;z-index:5;width:10px;height:10px;background:#ff0000'></div>" +
            "<div style='position:relative;z-index:1;width:10px;height:10px;background:#00ff00'></div>" +
            "</body></html>");

        var fills = view.Paint!.Commands.OfType<FillCommand>().ToList();

        var low = fills.FindIndex(fill => fill.Color == 0xFF00FF00u);
        var high = fills.FindIndex(fill => fill.Color == 0xFFFF0000u);

        Assert.That(low, Is.GreaterThanOrEqualTo(0));
        Assert.That(high, Is.GreaterThan(low), "the higher level is painted on top");
    }

    [Test]
    public async Task NegativeZIndexPaintsBelowTheInFlowContent()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:10px;background:#0000ff'></div>" +
            "<div style='position:relative;z-index:-1;width:10px;height:10px;background:#ff0000'></div>" +
            "</body></html>");

        var fills = view.Paint!.Commands.OfType<FillCommand>().ToList();

        var negative = fills.FindIndex(fill => fill.Color == 0xFFFF0000u);
        var flow = fills.FindIndex(fill => fill.Color == 0xFF0000FFu);

        Assert.That(negative, Is.GreaterThanOrEqualTo(0));
        Assert.That(negative, Is.LessThan(flow), "a negative level goes under the normal flow");
    }

    /// <summary>
    /// A box that names a level is painted from that level of the stacking context it belongs to, even
    /// when the walk reaches it through a box that names none - the structure
    /// `css-transforms/transform-stacking-002` is built out of, and the whole of what it asserts.
    /// </summary>
    [Test]
    public async Task ALevelMetInsideAPlainBoxIsPaintedFromThatLevel()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div><div style='position:relative;z-index:1;width:10px;height:10px;background:#00ff00'></div></div>" +
            "<div style='position:relative;width:10px;height:10px;background:#ff0000'></div>" +
            "</body></html>");

        var fills = view.Paint!.Commands.OfType<FillCommand>().ToList();

        var automatic = fills.FindIndex(fill => fill.Color == 0xFFFF_0000u);
        var levelled = fills.FindIndex(fill => fill.Color == 0xFF00_FF00u);

        Assert.Multiple(() =>
        {
            Assert.That(automatic, Is.GreaterThanOrEqualTo(0));
            Assert.That(levelled, Is.GreaterThan(automatic), "a level is a layer of its own, wherever the walk met the box");
        });
    }

    /// <summary>
    /// Gets the same answer for a box taken out of the flow that stands on a line, which is where the
    /// place it would have had - and so its static position - is recorded.
    /// </summary>
    [Test]
    public async Task ALevelMetOnALineIsPaintedFromThatLevel()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='position:relative;width:10px;height:10px;background:#ff0000'></div>" +
            "<div><span style='position:absolute;top:0;left:0;width:10px;height:10px;" +
            "background:#00ff00;z-index:3'></span></div>" +
            "</body></html>");

        var fills = view.Paint!.Commands.OfType<FillCommand>().ToList();

        var automatic = fills.FindIndex(fill => fill.Color == 0xFFFF_0000u);
        var levelled = fills.FindIndex(fill => fill.Color == 0xFF00_FF00u);

        Assert.Multiple(() =>
        {
            Assert.That(automatic, Is.GreaterThanOrEqualTo(0));
            Assert.That(levelled, Is.GreaterThan(automatic), "the line holds the place, and the level holds the paint");
        });
    }

    /// <summary>
    /// The walk that carries a level up to the context stops at a box that forms one of its own: this is
    /// the whole of what `css-contain/contain-paint-stacking-context-001a` asserts, and the same shape
    /// answers <c>contain: layout</c>, a <c>transform-style</c> that keeps the third dimension and a
    /// <c>will-change</c> that names a layer.
    /// </summary>
    [Test]
    public async Task ABoxThatContainsItsPaintAnswersTheLevelsInsideIt()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='front' style='position:absolute;z-index:10;width:100px;height:100px;background:#008000'></div>" +
            "<div id='back' style='contain:paint;width:100px;height:100px'>" +
            "<div id='inside' style='position:absolute;z-index:1000;width:100px;height:100px;background:#ff0000'></div>" +
            "</div></body></html>");

        var fills = view.Paint!.Commands.OfType<FillCommand>().ToList();

        var inside = fills.FindIndex(fill => fill.Color == 0xFFFF_0000u);
        var front = fills.FindIndex(fill => fill.Color == 0xFF00_8000u);

        Assert.Multiple(() =>
        {
            Assert.That(inside, Is.GreaterThanOrEqualTo(0), "the red box is painted");
            Assert.That(front, Is.GreaterThan(inside),
                "and the contained level of 1000 stays under the context's own level of 10 (CSS Contain 2 2.6)");
            Assert.That(Find(view, "back").EstablishesStackingContext, Is.True);
            Assert.That(Find(view, "front").EstablishesStackingContext, Is.True, "a positioned box that states a level");
        });
    }

    [Test]
    public async Task AHintAtALayerFormsAContextThoughNothingHasChangedYet()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='will' style='will-change:transform;width:10px;height:10px'></div>" +
            "<div id='contained' style='will-change:contain;width:10px;height:10px'></div>" +
            "<div id='blurred' style='will-change:backdrop-filter;width:10px;height:10px'></div>" +
            "<div id='levelled' style='position:relative;will-change:z-index;width:10px;height:10px'></div>" +
            "<div id='height' style='will-change:height;width:10px;height:10px'></div>" +
            "<div id='style' style='transform-style:preserve-3d;width:10px;height:10px'></div>" +
            "<div id='plain' style='width:10px;height:10px'></div>" +
            "</body></html>");

        Assert.Multiple(() =>
        {
            Assert.That(Find(view, "will").EstablishesStackingContext, Is.True,
                "the hint alone is the answer (CSS Will Change 1 2)");
            Assert.That(Find(view, "contained").EstablishesStackingContext, Is.True,
                "and a hint of a property that would form one counts, whether or not the box uses it");
            Assert.That(Find(view, "blurred").EstablishesStackingContext, Is.True, "the same for a backdrop filter");
            Assert.That(Find(view, "levelled").EstablishesStackingContext, Is.True,
                "and for a level, once the box is positioned enough for one to mean anything");
            Assert.That(Find(view, "height").EstablishesStackingContext, Is.False,
                "a property that forms no context hints at none (CSS Will Change 1 2)");
            Assert.That(Find(view, "style").EstablishesStackingContext, Is.True,
                "a box that keeps its third dimension is the frame for what stands in it (CSS Transforms 2)");
            Assert.That(Find(view, "plain").EstablishesStackingContext, Is.False, "a box that says none of them");
        });
    }

    [Test]
    public async Task AnAutomaticLevelIsNotAContextOfItsOwn()
    {
        // `z-index: auto` paints at level zero but forms no context, which is the distinction
        // `css-masking/clip-no-stacking-context` is built to test: the levels inside such a box belong to
        // the context above.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='auto' style='position:absolute;width:10px;height:10px'></div>" +
            "<div id='zero' style='position:absolute;z-index:0;width:10px;height:10px'></div>" +
            "</body></html>");

        Assert.Multiple(() =>
        {
            Assert.That(Find(view, "auto").EstablishesStackingContext, Is.False, "an automatic level");
            Assert.That(Find(view, "zero").EstablishesStackingContext, Is.True, "and a stated zero are not the same answer");
            Assert.That(Find(view, "auto").ZIndex, Is.EqualTo(Find(view, "zero").ZIndex), "though they paint at one level");
        });
    }

    /// <summary>
    /// Containment is a property of boxes: a span that contains its paint forms no context, and the levels
    /// written inside it are answered by the context above, as
    /// <c>contain-paint-ignored-cases-ib-split-001</c> measures (CSS Contain 2 §2.6).
    /// </summary>
    [Test]
    public async Task ContainmentOnAnInlineLevelFormsNoContext()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='position:absolute;z-index:10;width:100px;height:100px;background:#008000'></div>" +
            "<span style='contain:paint'>" +
            "<div style='position:absolute;z-index:1000;width:100px;height:100px;background:#ff0000'></div>" +
            "</span></body></html>");

        var fills = view.Paint!.Commands.OfType<FillCommand>().ToList();

        var inside = fills.FindIndex(fill => fill.Color == 0xFFFF_0000u);
        var front = fills.FindIndex(fill => fill.Color == 0xFF00_8000u);

        Assert.Multiple(() =>
        {
            Assert.That(inside, Is.GreaterThanOrEqualTo(0));
            Assert.That(inside, Is.GreaterThan(front), "the span holds nothing back, level included");
        });
    }

    /// <summary>
    /// A box that only captures - a filter, a backdrop filter, containment, a transform, with no position of
    /// its own - stands in its descendants' containing block chain just as a positioned box does, so the
    /// overflow it hides cuts what flew out of it as well (CSS Overflow 1 §3.2 with CSS Filter Effects 1
    /// §2.4, CSS Contain 2 §2.2 and CSS Transforms 1 §transform-rendering). Chrome clips at each of them,
    /// which <c>build/probe-clip-chain.html</c> reads back by hitting the place a box pokes out to, and not
    /// at a static box that captures nothing.
    /// </summary>
    /// <remarks>
    /// Nothing had to be written for this to hold: a box that captures is a stacking context as well - a
    /// transform and containment already were, and the filter and the opacity became one in the two
    /// commits before this - so the walk that gathers a context's levels stops at the box, and what flew
    /// out of a capturing ancestor is painted where its clips stand rather than in a list above them.
    /// The cases are kept as the guard on that reasoning: take the context back off the filter and the
    /// first two go red.
    /// </remarks>
    [Test]
    public async Task TheOverflowOfACapturingBoxCutsWhatFliesOutOfIt(
        [Values("filter:brightness(100%);", "backdrop-filter:blur(2px);", "contain:paint;",
                "transform:translateX(0);", "opacity:.5;")] String capture)
    {
        var flown = "<div style='position:absolute;top:0;left:0;width:200px;height:200px;background:#ff0000'></div>";

        using var view = await Painted(
            $"<div id='c' style='overflow:hidden;{capture}width:50px;height:50px'>" + flown + "</div>");

        var cut = Find(view, "c").PaddingBox;

        Assert.Multiple(() =>
        {
            Assert.That(CutAround(view, 0xFFFF_0000u), Is.True,
                $"a box that only says `{capture.TrimEnd(';')}` is the containing block, and cuts what left the flow");
            Assert.That(view.Paint!.Commands.OfType<PushClipCommand>().Any(clip => clip.Bounds.Equals(cut)),
                Is.True, "and the clip it is cut by is that box's own padding box");
        });
    }

    /// <summary>
    /// A box taken out of the flow is clipped along its containing block chain and not along the boxes it
    /// merely stands in, so the layer it is painted from has to carry the clips of that chain: the positioned
    /// box that hides its overflow cuts it, and a static one above its containing block does not
    /// (CSS 2.1 §11.1.1, CSS Overflow 1 §3.2 - the shape <c>gecko/pagination/bfc-004</c> is built on).
    /// </summary>
    [Test]
    public async Task TheClipsOfTheContainingBlockChainTravelWithABoxTakenOutOfTheFlow()
    {
        var flown = "<div style='position:absolute;top:0;left:0;width:200px;height:200px;background:#ff0000'></div>";

        using var inChain = await Painted(
            "<div id='c' style='position:relative;overflow:hidden;width:50px;height:50px'>" + flown + "</div>");
        using var offChain = await Painted(
            "<div id='c' style='overflow:hidden;width:50px;height:50px'>" + flown + "</div>");

        var cut = Find(inChain, "c").PaddingBox;

        Assert.Multiple(() =>
        {
            Assert.That(CutAround(inChain, 0xFFFF_0000u), Is.True,
                "the positioned box the flow was left from is the containing block, and its clip is put back");
            Assert.That(inChain.Paint!.Commands.OfType<PushClipCommand>().Any(clip => clip.Bounds.Equals(cut)),
                Is.True, "and the clip put back around it is that box's own padding box");
            Assert.That(CutAround(offChain, 0xFFFF_0000u), Is.False,
                "a static box that hides its overflow is not in the chain, and clips nothing taken out");
        });
    }

    /// <summary>
    /// Gets whether the ink of the given colour is drawn inside a clip pair - which is how these tests tell
    /// a clip that reaches a box from the pair a clipping box pushes around content the box was left out of.
    /// </summary>
    private static Boolean CutAround(HutuViewInternal view, UInt32 ink)
    {
        var depth = 0;

        foreach (var command in view.Paint!.Commands)
        {
            switch (command)
            {
                case PushClipCommand:
                    depth++;
                    break;

                case PopClipCommand:
                    depth--;
                    break;

                case FillCommand fill when fill.Color == ink:
                    return depth > 0;
            }
        }

        return false;
    }

    /// <summary>
    /// A positioned box whose level is <c>auto</c> paints at level zero but forms no context, so a level
    /// written inside it is answered by the context above - the whole of
    /// <c>css-masking/clip-no-stacking-context</c>, and the reason a context's levels are gathered by a walk
    /// that goes down through the boxes that name none.
    /// </summary>
    [Test]
    public async Task ALevelInsideAnAutomaticBoxIsAnsweredAboveIt()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p>x</p>" +
            "<div style='position:absolute;width:100px;height:100px;background:#ff0000;z-index:1'></div>" +
            "<div style='position:absolute'>" +
            "<div style='position:absolute;width:100px;height:100px;background:#008000;z-index:3'></div>" +
            "</div></body></html>");

        var fills = view.Paint!.Commands.OfType<FillCommand>().ToList();

        var levelled = fills.FindIndex(fill => fill.Color == 0xFFFF_0000u);
        var inside = fills.FindIndex(fill => fill.Color == 0xFF00_8000u);

        Assert.Multiple(() =>
        {
            Assert.That(levelled, Is.GreaterThanOrEqualTo(0), "both boxes are painted");
            Assert.That(inside, Is.GreaterThan(levelled),
                "and the higher level inside the automatic box is the context above that answers (§Appendix E)");
        });
    }

    /// <summary>
    /// A negative level reaches under the whole of the content of its context, however deep the box stands -
    /// which is what gathering the levels before any painting buys the engine.
    /// </summary>
    [Test]
    public async Task ANegativeLevelDeepInTheFlowReachesUnderTheContent()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:200px;height:100px;background:#0000ff'>" +
            "<div style='position:absolute;z-index:-1;width:100px;height:100px;background:#ff0000'></div>" +
            "</div></body></html>");

        var fills = view.Paint!.Commands.OfType<FillCommand>().ToList();

        var negative = fills.FindIndex(fill => fill.Color == 0xFFFF_0000u);
        var flowed = fills.FindIndex(fill => fill.Color == 0xFF00_00FFu);

        Assert.Multiple(() =>
        {
            Assert.That(negative, Is.GreaterThanOrEqualTo(0));
            Assert.That(negative, Is.LessThan(flowed), "the negative level goes under the block it stands in");
        });
    }

    /// <summary>
    /// A span whose only block-level child left the flow is still the inline box the page wrote: its
    /// vertical padding paints outward without moving its own text, the line it sits on keeps the height
    /// the line-height asks for, and the out-of-flow child's static position is the place in that line
    /// rather than a fresh one below the padding (CSS 2.1 §9.2.1.1, §10.3.7). Measured against Chrome on
    /// 2026-10-22: the block three lines of `line-height: 1` tall, the box taken out of the flow standing
    /// at the head of the second of them.
    /// </summary>
    [Test]
    public async Task AnInlineBoxHoldingOnlyAnOutOfFlowChildStaysInline()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='p' style='position:relative;top:30px;left:20px;width:200px;font:16px/1 Arial'>" +
            "one<br><span style='padding-top:60px'>two" +
            "<span id='c' style='position:absolute;width:5px;height:5px'></span></span><br>three" +
            "</div></body></html>");

        var lines = Find(view, "p");
        var flown = Find(view, "c");

        Assert.Multiple(() =>
        {
            Assert.That(lines.BorderBox.Height, Is.EqualTo(48), "three lines of 16px, padding included in nothing");
            Assert.That(flown.BorderBox.Y, Is.EqualTo(46), "the second line's top, not 60px below it");
            Assert.That(flown.BorderBox.X, Is.GreaterThan(20), "and along the line, where the text had stood");
        });
    }

    /// <summary>
    /// A span is a containing block for the boxes flown out of its content whatever its display: the
    /// rectangle they resolve their offsets against is its own padding box, and nothing about being an
    /// inline-level box excuses it (CSS 2.1 10.3.7, CSS Positioned Layout 3 2.1). Before this the line
    /// layout asked only the block chain, so a box inside a positioned span flew to the page corner.
    /// </summary>
    [Test]
    public async Task ABoxFlownOutOfAPositionedSpanAnchorsToTheSpan()
    {
        var (fragments, flown) = await Anchored("", "456<div id='a' style='position:absolute;top:0;left:0;width:5px;height:5px'></div>");

        Assert.Multiple(() =>
        {
            Assert.That(flown.X, Is.EqualTo(fragments[0].X), "along from where the span's first fragment begins");
            Assert.That(flown.Y, Is.EqualTo(fragments[0].Y), "and down from where it begins");
            Assert.That(flown.X, Is.GreaterThan(0), "which is past the text the span starts after, not the page corner");
        });
    }

    /// <summary>
    /// The rectangle an inline element gives its flown boxes is drawn from the start edges of its first
    /// fragment to the end edges of its last, so the whole of the element answers for them - not the line,
    /// and not the fragment the box happened to stand in (CSS Positioned Layout 3 2.1, measured against
    /// Chrome on a span broken over two lines).
    /// </summary>
    [Test]
    public async Task AFlownBoxReadsTheWholeOfTheSpanThatHoldsIt()
    {
        const string flown = "<div id='a' style='position:absolute;top:0;left:0;width:5px;height:5px'></div>";
        const string right = "<div id='a' style='position:absolute;top:0;right:0;width:5px;height:5px'></div>";
        const string below = "<div id='a' style='position:absolute;top:0;bottom:0;left:0;width:5px'></div>";

        var (broken, brokenEnd) = await Anchored("", "456<br>7" + right);
        var (single, singleEnd) = await Anchored("", "456" + right);
        var (_, belowBoth) = await Anchored("", "456<br>7" + below);
        var (_, atStart) = await Anchored("", "456<br>7" + flown);

        Assert.Multiple(() =>
        {
            Assert.That(brokenEnd.Right, Is.EqualTo(broken[0].X),
                "two lines leave nothing between the span's inline edges, so its end edge is where it began");
            Assert.That(singleEnd.Right, Is.EqualTo(single[0].Right),
                "one fragment is its own containing block, end edge and all");
            Assert.That(belowBoth.Bottom, Is.EqualTo(broken[^1].Bottom),
                "and the far end of the block axis is the last fragment stands at");
            Assert.That(atStart.X, Is.EqualTo(broken[0].X), "still starting where the first fragment starts");
        });
    }

    /// <summary>
    /// A relative offset on an inline element carries the boxes flown out of it along with its text, the
    /// same ride every block gives its own absolute children (CSS 2.1 9.4.3 with 10.3).
    /// </summary>
    [Test]
    public async Task APositionedSpanCarriesItsFlownBoxWithItsOffset()
    {
        const string flown = "<div id='a' style='position:absolute;top:0;left:0;width:5px;height:5px'></div>";

        var (plain, plainBox) = await Anchored("", "456<br>7" + flown);
        var (moved, movedBox) = await Anchored("top:10px;left:20px;", "456<br>7" + flown);

        Assert.Multiple(() =>
        {
            Assert.That(movedBox.X, Is.EqualTo(plainBox.X + 20), "the offset moves the box the fragment moves by");
            Assert.That(movedBox.Y, Is.EqualTo(plainBox.Y + 10), "along both axes");
            Assert.That(moved[1].Y, Is.GreaterThan(moved[0].Y), "the second fragment is on a later line");
            Assert.That(movedBox.X, Is.EqualTo(moved[0].X), "and the box stands at the edge the fragment was moved to");
        });
    }

    /// <summary>
    /// Content flown inside an inline-block standing under a positioned span flies to the span as well:
    /// the nearest positioned ancestor is a question about the tree, and the atomic box's own border does
    /// not answer it while it is static (measured against `wpt/css-multicol/multicol-oof-inline-cb-001`).
    /// </summary>
    [Test]
    public async Task ABoxFlownInsideAnInlineBlockUnderASpanFliesToTheSpan()
    {
        var (fragments, flown) = await Anchored(
            "",
            "456<br>7<span style='display:inline-block;width:20px;height:20px'>" +
            "<div id='a' style='position:absolute;top:0;left:0;width:5px;height:5px'></div></span>");

        Assert.Multiple(() =>
        {
            Assert.That(flown.X, Is.EqualTo(fragments[0].X), "the span's first fragment, not the inline-block beside it");
            Assert.That(flown.Y, Is.EqualTo(fragments[0].Y), "and its start on the block axis");
        });
    }

    /// <summary>
    /// The nearest positioned ancestor wins, so an inner span holds what the outer one would otherwise
    /// take - which is the same rule the block chain follows, read through an inline element.
    /// </summary>
    [Test]
    public async Task TheInnermostPositionedSpanHoldsTheBoxFlownOutOfIt()
    {
        const string inner =
            "AA<span id='i' style='position:relative'>BB" +
            "<div id='a' style='position:absolute;top:0;left:0;width:5px;height:5px'></div></span>";

        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'><div style='width:300px;font:16px/16px Arial'>123" +
            "<span id='o' style='position:relative'>" + inner + "</span></div></body></html>");

        var outer = FragmentsOf(view, "o")[0].PaddingBox;
        var held = FragmentsOf(view, "i")[0].PaddingBox;
        var flown = Find(view, "a").BorderBox;

        Assert.Multiple(() =>
        {
            Assert.That(held.X, Is.GreaterThan(outer.X), "the two spans start at different places");
            Assert.That(flown.X, Is.EqualTo(held.X), "and the box goes to the nearer one");
            Assert.That(flown.Y, Is.EqualTo(held.Y), "on both axes");
        });
    }

    /// <summary>
    /// Down a column the same answer holds, since the rectangle is drawn along the axis the element's own
    /// text runs - which the lines of a column run down, not across.
    /// </summary>
    [Test]
    public async Task AFlownBoxInAColumnAnchorsToTheSpanThatHoldsIt()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:300px;writing-mode:vertical-rl;font:16px/16px Arial'>123" +
            "<span id='c' style='position:relative'>456" +
            "<div id='a' style='position:absolute;top:0;left:0;width:5px;height:5px'></div></span></div></body></html>");

        var fragment = FragmentsOf(view, "c")[0].PaddingBox;
        var flown = Find(view, "a").BorderBox;

        Assert.Multiple(() =>
        {
            Assert.That(flown.X, Is.EqualTo(fragment.X), "the side of the fragment the text runs back from");
            Assert.That(flown.Y, Is.EqualTo(fragment.Y), "and the end of the column it begins at");
        });
    }

    /// <summary>
    /// A filter gives the box it is written on the frame its descendants resolve against, exactly as a
    /// position does (CSS Filter Effects 1 2.4) - and it reaches <c>fixed</c> descendants too, the way a
    /// transform and containment do. Chrome measures the frame as the padding box of the box the filter
    /// stands on (<c>build/probe-cb-rect.html</c>: a bordered, padded container takes an absolute and a
    /// fixed descendant five of its border in, not at its outer edge).
    /// </summary>
    [Test]
    public async Task AFilterHoldsTheBoxesFlownOutOfTheBoxItIsWrittenOn()
    {
        using var view = await Painted(
            "<div id='c' style='margin:30px 0 0 20px;padding:4px;border:2px solid green;filter:brightness(100%)'>" +
            "<div id='a' style='position:absolute;top:0;left:0;width:5px;height:5px'></div>" +
            "<div id='f' style='position:fixed;top:0;left:0;width:5px;height:5px'></div></div>");

        var held = Find(view, "c").PaddingBox;
        var flown = Find(view, "a").BorderBox;
        var pinned = Find(view, "f").BorderBox;

        Assert.Multiple(() =>
        {
            Assert.That(flown.X, Is.EqualTo(held.X), "the absolute box stands at the filtered box's padding edge");
            Assert.That(flown.Y, Is.EqualTo(held.Y), "on both axes");
            Assert.That(pinned.X, Is.EqualTo(held.X), "and the fixed one is pulled in behind it as well");
            Assert.That(pinned.Y, Is.EqualTo(held.Y), "rather than left at the viewport corner");
        });
    }

    /// <summary>
    /// The filter reaches an inline box too, whatever its display: the chapter writes the containing block
    /// as a consequence of the value rather than of the effect being drawn, and Chrome follows it
    /// (<c>build/probe-cb-props.html</c>, where <c>filter</c>, <c>backdrop-filter</c> and
    /// <c>will-change: filter</c> each hold an absolute descendant of a span while a transform does not).
    /// </summary>
    [Test]
    public async Task AFilterOnASpanHoldsTheBoxFlownOutOfIt()
    {
        const string flown = "<div id='a' style='position:absolute;top:0;left:0;width:5px;height:5px'></div>";

        var (first, filter) = await HeldBy("filter:brightness(100%)", "456" + flown);
        var (_, backdrop) = await HeldBy("backdrop-filter:blur(2px)", "456" + flown);
        var (_, hint) = await HeldBy("will-change:filter", "456" + flown);
        var (_, transform) = await HeldBy("transform:translateX(0)", "456" + flown);
        var (_, contained) = await HeldBy("contain:paint", "456" + flown);

        Assert.Multiple(() =>
        {
            Assert.That(first.X, Is.GreaterThan(0), "the span starts past the text it follows");
            Assert.That(filter.X, Is.EqualTo(first.X), "a filter holds the box at the span's fragment");
            Assert.That(filter.Y, Is.EqualTo(first.Y), "on both axes");
            Assert.That(backdrop.X, Is.EqualTo(first.X), "and so does a backdrop filter");
            Assert.That(hint.X, Is.EqualTo(first.X), "and a hint at either");
            Assert.That(transform.X, Is.EqualTo(0), "a transform is not drawn on a span, so it holds nothing");
            Assert.That(contained.X, Is.EqualTo(0), "and containment is a property of the principal box");
        });
    }

    /// <summary>
    /// Containment of layout or paint gives a box the same frame, fixed descendants included
    /// (CSS Contain 2 2.2) - and a <c>will-change</c> hint at it stands for the property, at the level that
    /// property reaches (CSS Will Change 1 2).
    /// </summary>
    [Test]
    public async Task ContainmentAndItsHintHoldTheBoxesFlownOutOfABlock()
    {
        const string flown = "<div id='a' style='position:absolute;top:0;left:0;width:5px;height:5px'></div>";

        using var contained = await Painted(
            "<div id='c' style='margin:30px 0 0 20px;contain:paint'>" + flown + "</div>");
        using var hinted = await Painted(
            "<div id='c' style='margin:30px 0 0 20px;will-change:contain'>" + flown + "</div>");

        Assert.Multiple(() =>
        {
            Assert.That(Find(contained, "a").BorderBox.X, Is.EqualTo(20), "the contained box holds the flown one");
            Assert.That(Find(contained, "a").BorderBox.Y, Is.EqualTo(30), "on both axes");
            Assert.That(Find(hinted, "a").BorderBox.X, Is.EqualTo(20), "and so does a hint at containment");
            Assert.That(Find(hinted, "a").BorderBox.Y, Is.EqualTo(30), "the same pair");
        });
    }

    /// <summary>
    /// The frame a transformed box lends is its padding box, the same one a positioned, filtered or
    /// contained box lends - not the border box the transform is drawn around
    /// (<c>build/probe-cb-rect.html</c>: a bordered container puts an absolute descendant at its padding
    /// edge, and a fixed one with it).
    /// </summary>
    [Test]
    public async Task ATransformedBoxLendsItsPaddingBoxRatherThanItsBorderBox()
    {
        using var view = await Painted(
            "<div id='c' style='margin:20px 0 0 10px;border:3px solid;transform:translateX(0)'>" +
            "<div id='a' style='position:absolute;top:0;left:0;width:5px;height:5px'></div></div>");

        var held = Find(view, "c");

        Assert.Multiple(() =>
        {
            Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(held.PaddingBox.X), "inset by the border it draws");
            Assert.That(held.PaddingBox.X, Is.GreaterThan(held.BorderBox.X), "which is not the outer edge");
            Assert.That(Find(view, "a").BorderBox.Y, Is.EqualTo(held.PaddingBox.Y), "on both axes");
        });
    }

    /// <summary>
    /// Draws a line holding a span with the given style and content, and hands back the padding box of the
    /// span's first fragment with the box inside it that left the flow.
    /// </summary>
    private static async Task<(Rect First, Rect Flown)> HeldBy(String span, String content)
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            $"<div style='width:300px;font:16px/16px Arial'>123" +
            $"<span id='c' style='{span}'>{content}</span></div></body></html>");

        return (FragmentsOf(view, "c")[0].PaddingBox, Find(view, "a").BorderBox);
    }

    /// <summary>
    /// Containment belongs to a principal box, so a span holds nothing even when a block standing inside it
    /// gives the box tree a shape of its own (CSS Contain 2 §2.4): what decides is the display the element
    /// was written with, not the box the split left behind - which is what
    /// <c>wpt/css-contain/contain-paint-ignored-cases-ib-split-001</c> and
    /// <c>...-no-principal-box-003</c> draw.
    /// </summary>
    [Test]
    public async Task ContainmentDoesNotGiveASplitSpanAFrame()
    {
        using var view = await Painted(
            "<div id='w' style='position:relative;margin:20px;width:200px'>123" +
            "<span id='c' style='contain:paint'>456<div style='height:8px'></div>" +
            "<div id='a' style='position:absolute;top:0;left:0;width:5px;height:5px'></div></span></div>");

        var above = Find(view, "w").PaddingBox;
        var split = Find(view, "c").PaddingBox;
        var flown = Find(view, "a").BorderBox;

        Assert.Multiple(() =>
        {
            Assert.That(split.Y, Is.GreaterThan(above.Y), "the span did come to a box of its own below the line");
            Assert.That(flown.X, Is.EqualTo(above.X), "and yet the absolute box falls past it, as CSS says");
            Assert.That(flown.Y, Is.EqualTo(above.Y), "to the positioned ancestor above");
        });
    }

    /// <summary>
    /// A <c>display: contents</c> element is not a principal box either, so the containment it asks for
    /// reaches nothing (CSS Contain 2 §2.4) - which is what
    /// <c>wpt/css-contain/contain-layout-ignored-cases-no-principal-box-003</c> measures, the fixed box
    /// standing at the viewport corner whatever the contents element said. The engine still gives the
    /// element a box, which is the other half of that pair's story and stands on its own.
    /// </summary>
    [Test]
    public async Task ContainmentDoesNotGiveAContentsElementAFrame()
    {
        using var view = await Painted(
            "<div id='a' style='display:contents;contain:layout;width:100px;height:100px;margin:50px'>" +
            "<div><div id='b' style='position:fixed;top:0;left:0;width:100px;height:100px'></div></div></div>");

        var flown = Find(view, "b").BorderBox;

        Assert.Multiple(() =>
        {
            Assert.That(flown.X, Is.EqualTo(0), "the fixed box keeps the viewport");
            Assert.That(flown.Y, Is.EqualTo(0), "on both axes, past the contents element that asked to be contained");
        });
    }

    /// <summary>
    /// A <c>display: contents</c> element generates no box: the chapter hands its children to the
    /// parent's formatting context (CSS Display 3 §2.5), so the text it holds stands on the parent's
    /// line as if the element were not there.
    /// </summary>
    [Test]
    public async Task AContentsElementGeneratesNoBoxAtAll()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='p' style='font:16px/16px Arial'>a" +
            "<span id='c' style='display:contents;font-size:40px'>b</span>c</div></body></html>");

        Assert.Multiple(() =>
        {
            Assert.That(FragmentsOf(view, "c"), Is.Empty, "no box carries the element");
            Assert.That(
                Walk(view.Layout!.Root).Where(node => node.Kind == LayoutKind.Text).Select(node => node.Text),
                Is.EqualTo(new[] { "a", "b", "c" }),
                "its text joins the parent's line");
            Assert.That(Find(view, "p").BorderBox.Height, Is.EqualTo(16), "and the block is one line tall");
        });
    }

    /// <summary>
    /// Draws a line holding a positioned span with the given style and content, and hands back every box
    /// the span was drawn through together with the one box inside it that left the flow. The container is
    /// a div rather than a p, because a div inside a p closes the p and takes the box out of the span.
    /// </summary>
    private static async Task<(List<Rect> Fragments, Rect Flown)> Anchored(String span, String content)
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            $"<div style='width:300px;font:16px/16px Arial'>123" +
            $"<span id='c' style='position:relative;{span}'>{content}</span></div></body></html>");

        return (FragmentsOf(view, "c").Select(node => node.PaddingBox).ToList(), Find(view, "a").BorderBox);
    }

    /// <summary>Gets every box the element with this id was drawn through, in the order the lines put them.</summary>
    private static List<LayoutBox> FragmentsOf(HutuViewInternal view, String id) =>
        Walk(view.Layout!.Root).Where(node => node.Source.Element?.Id == id).ToList();

    /// <summary>
    /// An inline box that forms a stacking context answers the levels written inside it, the same answer a
    /// block gives - which the painting can only know if the line layout carries the flag onto the box it
    /// builds (CSS Will Change 1 §2, measured by `will-change-stacking-context-opacity-2`).
    /// </summary>
    [Test]
    public async Task AnInlineBoxThatFormsAContextHoldsItsOwnLevels()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='position:absolute;width:100px;height:100px;background:#008000;z-index:1'></div>" +
            "<span style='will-change:opacity'>" +
            "<div style='position:absolute;width:100px;height:100px;background:#ff0000;z-index:3'></div>" +
            "</span></body></html>");

        var fills = view.Paint!.Commands.OfType<FillCommand>().ToList();

        var indicator = fills.FindIndex(fill => fill.Color == 0xFF00_8000u);
        var inside = fills.FindIndex(fill => fill.Color == 0xFFFF_0000u);

        Assert.Multiple(() =>
        {
            Assert.That(inside, Is.GreaterThanOrEqualTo(0), "the box inside the inline context is painted");
            Assert.That(inside, Is.LessThan(indicator), "and not lifted over the level it belongs to");
        });
    }

    /// <summary>
    /// A filter stands its box on a layer of its own, so the levels written inside it are its own to
    /// answer - at either display. The filter chapter writes the consequence twice over for one value
    /// (CSS Filter Effects 1 §2.4: "the creation of a containing block for absolute and fixed positioned
    /// descendants, and the creation of a stacking context"), and Chrome draws it that way for a div and a
    /// span alike, which <c>build/probe-filter-layer.html</c> reads back through
    /// <c>document.elementFromPoint</c> over the middle of the box inside.
    /// </summary>
    [Test]
    public async Task AFilteredBoxAnswersTheLevelsWrittenInsideIt(
        [Values("div", "span")] String tag)
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='position:absolute;width:100px;height:100px;background:#008000;z-index:1'></div>" +
            $"<{tag} style='filter:brightness(100%)'>" +
            "<div style='position:absolute;width:100px;height:100px;background:#ff0000;z-index:3'></div>" +
            $"</{tag}></body></html>");

        var fills = view.Paint!.Commands.OfType<FillCommand>().ToList();

        var indicator = fills.FindIndex(fill => fill.Color == 0xFF00_8000u);
        var inside = fills.FindIndex(fill => fill.Color == 0xFFFF_0000u);

        Assert.Multiple(() =>
        {
            Assert.That(inside, Is.GreaterThanOrEqualTo(0), "the box inside the filtered element is painted");
            Assert.That(inside, Is.LessThan(indicator), $"and stays under the level above, from a {tag}");
        });
    }

    /// <summary>
    /// An <c>opacity</c> below one stands its box on a level of its own and draws its whole picture through
    /// one layer (CSS Compositing 1 §9.1, CSS 2.1 §Appendix E) - the two consequences the chapters give the
    /// one value. Chrome answers both for a div and for a span, which
    /// <c>build/probe-filter-layer.html</c> reads back through <c>document.elementFromPoint</c>.
    /// </summary>
    [Test]
    public async Task AFadedBoxAnswersTheLevelsWrittenInsideIt(
        [Values("div", "span")] String tag)
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='position:absolute;width:100px;height:100px;background:#008000;z-index:1'></div>" +
            $"<{tag} style='opacity:.5'>" +
            "<div style='position:absolute;width:100px;height:100px;background:#ff0000;z-index:3'></div>" +
            $"</{tag}></body></html>");

        var fills = view.Paint!.Commands.OfType<FillCommand>().ToList();

        var indicator = fills.FindIndex(fill => fill.Color == 0xFF00_8000u);
        var inside = fills.FindIndex(fill => fill.Color == 0xFFFF_0000u);

        Assert.Multiple(() =>
        {
            Assert.That(inside, Is.GreaterThanOrEqualTo(0), "the box inside the faded element is painted");
            Assert.That(inside, Is.LessThan(indicator), $"and stays under the level above, from a {tag}");
        });
    }

    /// <summary>
    /// The layer covers the box's own picture and everything drawn inside it, and only that: the pair is
    /// laid around the subtree rather than written into each command, because the group is the meaning of
    /// the number.
    /// </summary>
    [Test]
    public async Task AFadedBoxWrapsItsPictureInOneLayer()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='f' style='opacity:.25;background:#ffff00;width:100px;height:100px'>" +
            "<div id='in' style='background:#0000ff;width:50px;height:50px'></div></div>" +
            "<div id='out' style='background:#ff0000;width:20px;height:20px'></div></body></html>");

        var commands = view.Paint!.Commands;
        var layer = commands.OfType<PushLayerCommand>().SingleOrDefault();

        Assert.That(layer, Is.Not.Null, "the faded box opens a layer");
        Assert.That(layer!.Alpha, Is.EqualTo(0.25).Within(0.001), "at the strength the page wrote");

        var opened = commands.IndexOf(layer);
        var closed = commands.IndexOf(commands.OfType<PopLayerCommand>().Single());
        var inside = commands.ToList().FindIndex(
            command => command is FillCommand fill && fill.Color == 0xFF00_00FFu);
        var outside = commands.ToList().FindIndex(
            command => command is FillCommand fill && fill.Color == 0xFFFF_0000u);

        Assert.Multiple(() =>
        {
            Assert.That(inside, Is.InRange(opened, closed), "what is drawn inside the box rides on the layer");
            Assert.That(outside, Is.GreaterThan(closed).Or.LessThan(opened), "and the box beside it does not");
        });
    }

    [Test]
    public async Task AnAbsoluteChildOfAFlexContainerIsNotAFlexItem()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='flex' style='display:flex;position:relative;width:300px;height:100px'>" +
            "<div id='item' style='width:50px;height:20px'></div>" +
            "<div id='a' style='position:absolute;right:0;top:0;width:30px;height:30px'></div>" +
            "</div>" +
            "</body></html>");

        var item = Find(view, "item");
        var a = Find(view, "a");

        Assert.That(item.BorderBox.X, Is.EqualTo(0).Within(0.5), "the one flex item starts at the container edge");
        Assert.That(a.BorderBox.X, Is.EqualTo(270).Within(0.5), "the absolute child anchors to the container, not to the line");
    }

    [Test]
    public async Task AnAbsoluteChildOfAGridContainerIsNotAGridItem()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='grid' style='display:grid;grid-template-columns:100px 100px;position:relative;width:300px;height:100px'>" +
            "<div id='item' style='height:20px'></div>" +
            "<div id='a' style='position:absolute;right:0;top:0;width:30px;height:30px'></div>" +
            "</div>" +
            "</body></html>");

        var item = Find(view, "item");
        var a = Find(view, "a");

        Assert.That(item.BorderBox.X, Is.EqualTo(0).Within(0.5), "it takes the first track");
        Assert.That(a.BorderBox.X, Is.EqualTo(270).Within(0.5), "the absolute child is out of the grid flow");
    }

    /// <summary>
    /// A content keyword sizes the box to what its content asks rather than leaving it to the offsets it
    /// stands between: <c>min-content</c> and its siblings are sizes the content settles (CSS Sizing 3
    /// §4), and naming one for the axis the text runs along means the same thing as naming the side it
    /// lies on (CSS Logical 1 §4).
    /// </summary>
    /// <remarks>
    /// The axis spelling is the one that reaches the layout here: the parser's grammar for
    /// <c>height</c> is the plain <c>auto | &lt;length-percentage&gt;</c> one and rejects the content
    /// keywords outright, so a page that writes <c>height: min-content</c> has the declaration dropped
    /// before it is read. <c>width</c> and the two axis names take them.
    /// </remarks>
    [Test]
    [TestCase("block-size:min-content", 200)]
    [TestCase("block-size:max-content", 200)]
    [TestCase("block-size:fit-content", 200)]
    [TestCase("block-size:210px", 210)]
    [TestCase("height:auto", 600)]
    public async Task AContentSizedAbsoluteBoxIsNotStretchedByItsOffsets(String sizes, Double height)
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            $"<div id='a' style='position:absolute;inset:0;{sizes}'>" +
            "<div style='height:200px'></div></div></body></html>");

        Assert.That(
            Find(view, "a").BorderBox.Height,
            Is.EqualTo(height).Within(0.5),
            $"`{sizes}` between four zero offsets, where an unstretched box is 600 tall");
    }

    /// <summary>
    /// Draws a body of the given markup and hands back the view, so a test can read the commands the page
    /// was painted with.
    /// </summary>
    private static async Task<HutuViewInternal> Painted(String body)
    {
        var view = CreateView(400);
        await view.SetHtml($"<html><body style='margin:0'>{body}</body></html>");

        return view;
    }

    /// <summary>
    /// The place a box left out of the flow falls back to when it sets no edge, and the margins that
    /// move it off that place.
    /// </summary>
    /// <remarks>
    /// CSS 2.1 §10.3.7 gives an unset edge the static position - the place the box's own margins would
    /// have left it in if it had stayed in flow - and §8.3.1 folds no margin into a box that has left the
    /// flow, so neither the run its neighbour opened nor the run it would have opened is any part of
    /// either answer. Measured in Chrome on 2026-10-22: a paragraph with a 16px margin, then
    /// `position:absolute; margin-top:50px; margin-left:50px`, puts the box at 58,100.5 while the
    /// paragraph after it stands at 50.5 - the box is 50 along and 50 down from the edge the first
    /// paragraph's margin reached, and the next paragraph saw none of its margins.
    /// </remarks>
    [Test]
    public async Task AFlownBoxStandsWhereItsOwnMarginsLeftItAndMovesNoOtherBox()
    {
        using var plain = CreateView(400);
        await plain.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:20px;margin:10px 0'></div>" +
            "<div id='a' style='position:absolute;width:100px;height:40px'></div>" +
            "<div id='b' style='height:20px'></div>" +
            "</body></html>");

        using var margined = CreateView(400);
        await margined.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:20px;margin:10px 0'></div>" +
            "<div id='a' style='position:absolute;margin:50px 50px 0 50px;width:100px;height:40px'></div>" +
            "<div id='b' style='height:20px'></div>" +
            "</body></html>");

        var stood = Find(plain, "a").BorderBox;
        var moved = Find(margined, "a").BorderBox;

        Assert.Multiple(() =>
        {
            Assert.That(moved.X - stood.X, Is.EqualTo(50), "the left margin moves the box along by itself, once");
            Assert.That(moved.Y - stood.Y, Is.EqualTo(50), "and the top margin moves it down by itself, once");
            Assert.That(Find(plain, "b").BorderBox.Y, Is.EqualTo(Find(margined, "b").BorderBox.Y),
                "the box that left the flow lends its margins to no other box");
            // The box 20 tall with 10 of margin above and below leaves the run at 40, and the 50 the
            // absolved box was given stands it there - the same arithmetic Chrome showed on a paragraph
            // that ended at 34.5 with 16 of margin below it, giving 100.5 for a 50px margin-top.
            Assert.That(moved.Y, Is.EqualTo(90), "the edge it stands at is the run it would have joined");
        });
    }

    /// <summary>
    /// An out-of-flow box whose specified display was inline-level hangs from the insertion point the
    /// line box would have given it, not from the container's start corner.
    /// </summary>
    /// <remarks>
    /// Measured in Chrome on 2026-10-28 with a 100x60 relative container, a 20x10 absolute child
    /// written <c>display: inline</c> and no offsets on the child: text-align moves an empty line's
    /// insertion point (0 / 50 / 100 for left / center / right), the box grows rightwards from it in
    /// a left-to-right line and leftwards in a right-to-left one (80 for a bare right-to-left
    /// container), and the floats standing at that offset shorten the line first (a 30px left float
    /// puts the point at 30; in a right-to-left container the point is the right edge of what the
    /// float left, so the box stands at 80 with a left float and at 50 with a right one). A
    /// <c>display: block</c> box keeps the container's start corner whatever the alignment asks (0).
    /// </remarks>
    [Test]
    public async Task AnInlineLevelFlownBoxHangsFromTheInsertionPointItsLineWouldHave()
    {
        const String abs = "display:inline;position:absolute;width:20px;height:10px";
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            $"<div id='c1' style='position:relative;width:100px;height:60px;text-align:left'><div id='a1' style='{abs}'></div></div>" +
            $"<div id='c2' style='position:relative;width:100px;height:60px;text-align:center'><div id='a2' style='{abs}'></div></div>" +
            $"<div id='c3' style='position:relative;width:100px;height:60px;text-align:right'><div id='a3' style='{abs}'></div></div>" +
            $"<div id='c4' style='position:relative;width:100px;height:60px;direction:rtl'><div id='a4' style='{abs}'></div></div>" +
            $"<div id='c5' style='position:relative;width:100px;height:60px;direction:rtl;text-align:center'><div id='a5' style='{abs}'></div></div>" +
            $"<div id='c6' style='position:relative;width:100px;height:60px'><div style='float:left;width:30px;height:30px'></div><div id='a6' style='{abs}'></div></div>" +
            $"<div id='c7' style='position:relative;width:100px;height:60px'><div style='float:right;width:30px;height:30px'></div><div id='a7' style='{abs}'></div></div>" +
            $"<div id='c8' style='position:relative;width:100px;height:60px;direction:rtl'><div style='float:left;width:30px;height:30px'></div><div id='a8' style='{abs}'></div></div>" +
            $"<div id='c9' style='position:relative;width:100px;height:60px;direction:rtl'><div style='float:right;width:30px;height:30px'></div><div id='a9' style='{abs}'></div></div>" +
            "<div id='c10' style='position:relative;width:100px;height:60px;text-align:center'><div id='a10' style='display:block;position:absolute;width:20px;height:10px'></div></div>" +
            "</body></html>");

        Assert.Multiple(() =>
        {
            Assert.That(StandsAt(view, "c1", "a1"), Is.EqualTo(0), "a left-to-right line begins at its start edge");
            Assert.That(StandsAt(view, "c2", "a2"), Is.EqualTo(50), "an empty line's insertion point is its middle under center");
            Assert.That(StandsAt(view, "c3", "a3"), Is.EqualTo(100), "and its far edge under right");
            Assert.That(StandsAt(view, "c4", "a4"), Is.EqualTo(80), "a right-to-left line begins at the right, and the box hangs leftwards");
            Assert.That(StandsAt(view, "c5", "a5"), Is.EqualTo(30), "the middle of a right-to-left line is where the box's right edge stands");
            Assert.That(StandsAt(view, "c6", "a6"), Is.EqualTo(30), "the 30px float shortens the line before the point is chosen");
            Assert.That(StandsAt(view, "c7", "a7"), Is.EqualTo(0), "a right float leaves the start of the line where it was");
            Assert.That(StandsAt(view, "c8", "a8"), Is.EqualTo(80), "a right-to-left line begins past the left float");
            Assert.That(StandsAt(view, "c9", "a9"), Is.EqualTo(50), "and past the right float at the other end");
            Assert.That(StandsAt(view, "c10", "a10"), Is.EqualTo(0), "a block-level display keeps the container's start corner");
        });
    }

    private static Double StandsAt(HutuViewInternal view, String container, String child) =>
        Find(view, child).BorderBox.X - Find(view, container).BorderBox.X;

    /// <summary>
    /// The same fallback where the box stands inside one that holds it, which is the case the
    /// containing block makes rather than the flow.
    /// </summary>
    [Test]
    public async Task AFlownBoxInsideAPositionedBoxCountsItsMarginsOnceToo()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='position:relative;top:10px;left:20px;margin:50px;width:200px;height:20px'>" +
            "<div id='a' style='position:absolute;margin:50px 0 0 50px;width:100px;height:40px'></div>" +
            "</div></body></html>");

        var stood = Find(view, "a").BorderBox;

        Assert.That(
            (stood.X, stood.Y),
            Is.EqualTo((120d, 110d)),
            "the box stands at its holding box's own edge, moved by its margins once: the wrapper is at "
            + "70,60 after its offset, and the child it holds is 50 along and 50 down from there "
            + "(CSS 2.1 §10.3.7, and Chrome's 128,351 for a holder at 78,301)");
    }

    private static HutuViewInternal CreateView(Int32 width, Int32 height = 600)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(width, height);
        return view;
    }

    private static LayoutBox Find(HutuViewInternal view, String id)
    {
        foreach (var node in Walk(view.Layout!.Root))
        {
            if (node.Kind == LayoutKind.Block && node.Source.Element?.Id == id)
            {
                return node;
            }
        }

        Assert.Fail($"no block box found for #{id}");
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
}

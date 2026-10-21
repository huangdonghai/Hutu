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

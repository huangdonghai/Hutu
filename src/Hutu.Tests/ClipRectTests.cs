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
/// Covers the legacy <c>clip</c> property: what a rectangle a page wrote comes to, and where the box it
/// cuts is drawn.
/// </summary>
/// <remarks>
/// The property is the one CSS 2.1 §11.1.1 wrote and CSS Masking 1 §1.3 kept as an appendix: four offsets
/// that state a rectangle, and the answer split across two moments. The offsets belong to the style and are
/// read when the style is; the rectangle they come to belongs to the box as laid out, because a distance
/// from the top-left corner of a border box has no answer until the box has a corner. The first half of
/// these tests reads the four numbers off the style, the second reads the area the painter cut and the
/// commands it put inside the cut.
/// <para>
/// Two of the answers are pinned by Chrome rather than by the chapter, which says only that the property
/// reaches an absolutely positioned element: measured on 2026-10-22 that a box fixed to the viewport is cut
/// as surely as one positioned absolutely, and that all four offsets run from the same corner, so that
/// <c>rect(10px, 180px, 190px, 20px)</c> leaves the band x 20 to 180 rather than a rectangle 180 wide.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ClipRectTests
{
    private const UInt32 Blue = 0xFF00_00FF;

    [Test]
    public async Task TheFourOffsetsAreReadInTheOrderThePageWroteThem()
    {
        var cut = Only(await Cut("#b{clip:rect(10px, 20px, 30px, 40px)}"));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Top, Is.EqualTo(10));
            Assert.That(cut.Right, Is.EqualTo(20));
            Assert.That(cut.Bottom, Is.EqualTo(30));
            Assert.That(cut.Left, Is.EqualTo(40));
        });
    }

    [Test]
    public async Task AnOffsetIsFoldedInTheFontOfTheBoxThatWroteIt()
    {
        var cut = Only(await Cut("#b{font-size:10px;clip:rect(1em, 2em, 3em, 4em)}"));

        Assert.That(
            new[] { cut.Top, cut.Right, cut.Bottom, cut.Left },
            Is.EqualTo(new Double?[] { 10, 20, 30, 40 }),
            "an em is the element's own size, and nothing above it has a say");
    }

    [Test]
    public void EveryOffsetRunsFromTheTopLeftCornerOfTheBorderBox()
    {
        // The grammar is peculiar in the one way a reader is likely to get wrong: right and bottom are the
        // far edges' distances from the near corner, not distances in from the edges they name.
        var shape = new ClipShape(10, 180, 190, 20);

        Assert.That(Edges(shape.ClipOf(new Rect(0, 0, 200, 200))), Is.EqualTo((20d, 10d, 160d, 180d)),
            "measured in Chrome on a box whose border box is 200x200 (CSS 2.1 §11.1.1)");
    }

    [Test]
    public async Task ASideLeftToAutoIsTheEdgeOfTheBoxItWasWrittenOn()
    {
        var cut = Only(await Cut("#b{clip:rect(auto, 150px, auto, 50px)}"));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Top, Is.Null, "the keyword is the absence of a number, not zero");
            Assert.That(cut.Bottom, Is.Null);
            Assert.That(cut.Right, Is.EqualTo(150));
            Assert.That(cut.Left, Is.EqualTo(50));
            Assert.That(Edges(cut.ClipOf(new Rect(0, 0, 200, 200))), Is.EqualTo((50d, 0d, 100d, 200d)),
                "and the two sides left to it are the box's own edges");
        });
    }

    [Test]
    public async Task ARectangleOfNothingButAutoIsTheWholeBorderBoxAndStillCuts()
    {
        // This is the pair `gecko/box-properties/clip-rect-auto.html` draws: a box whose children overflow
        // it is cut to its own border box by `rect(auto, auto, auto, auto)`, which is a clip and not the
        // absence of one. `clip: auto` alone is the absence.
        var cut = Only(await Cut("#b{clip:rect(auto, auto, auto, auto)}"));

        Assert.That(Edges(cut.ClipOf(new Rect(0, 0, 200, 200))), Is.EqualTo((0d, 0d, 200d, 200d)));
    }

    [Test]
    public async Task ARectangleNeedsNoCommasToBeOne()
    {
        var spaced = Only(await Cut("#b{clip:rect(10px 20px 30px 40px)}"));
        var commas = Only(await Cut("#b{clip:rect(10px, 20px, 30px, 40px)}"));

        Assert.That(
            new[] { spaced.Top, spaced.Right, spaced.Bottom, spaced.Left },
            Is.EqualTo(new Double?[] { commas.Top, commas.Right, commas.Bottom, commas.Left }),
            "the form without commas is what CSS 2.1 wrote, and it reads the same (§11.1.1)");
    }

    [Test]
    public async Task ASeparatorThatChangesHalfwayIsNoRectangle()
    {
        var first = await Cut("#b{clip:rect(10px 20px, 30px, 40px)}");
        var second = await Cut("#b{clip:rect(10px, 20px 30px, 40px)}");
        var third = await Cut("#b{clip:rect(10px, 20px, 30px 40px)}");

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Null, "one comma among three spaces is no rectangle (CSS Masking 1 §1.3)");
            Assert.That(second, Is.Null);
            Assert.That(third, Is.Null);
        });
    }

    [Test]
    public async Task ARectangleThatNamesMoreOrFewerThanFourOffsetsIsNoRectangle()
    {
        var three = await Cut("#b{clip:rect(10px, 20px, 30px)}");
        var five = await Cut("#b{clip:rect(10px, 20px, 30px, 40px, 50px)}");
        var one = await Cut("#b{clip:rect(10px)}");

        Assert.Multiple(() =>
        {
            Assert.That(three, Is.Null, "the grammar takes four offsets and no fewer");
            Assert.That(five, Is.Null, "and no more");
            Assert.That(one, Is.Null);
        });
    }

    [Test]
    public async Task APerCentStandingWhereAnOffsetShouldBeIsRefused()
    {
        Assert.That(await Cut("#b{clip:rect(10%, 50%, 60%, 20%)}"), Is.Null,
            "the grammar gives a rectangle lengths only, and a per cent is not one");
    }

    [Test]
    public async Task TheKeywordThatNamesNoRectangleLeavesTheBoxUncut()
    {
        var stated = await Cut("#b{clip:auto}");
        var refused = await Cut("#b{clip:none}");

        Assert.Multiple(() =>
        {
            Assert.That(stated, Is.Null, "the default states no rectangle");
            Assert.That(refused, Is.Null, "and a keyword the grammar of clip does not take is no rectangle either");
        });
    }

    [Test]
    public async Task ANegativeOffsetStandsWhereItIsWrittenAndLeavesNothingToShow()
    {
        // The four pairs `clip-negative-values-00N` are built on this: an offset is a number on a scale and
        // may be negative, and a rectangle whose far edge falls behind its near one has no area at all.
        var cut = Only(await Cut("#b{clip:rect(0, -50px, 200px, 50px)}"));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Right, Is.EqualTo(-50));
            Assert.That(cut.ClipOf(new Rect(0, 0, 200, 200)), Is.EqualTo(Rect.Empty),
                "the right edge falls left of the left one");
        });
    }

    [Test]
    public async Task TheRectangleIsTheBoxesOwnAndFallsToNoChild()
    {
        var pair = await Paired("#outer{clip:rect(10px, 200px, 200px, 10px)}", "<div id='outer'><div id='b'></div></div>");

        Assert.Multiple(() =>
        {
            Assert.That(pair[0], Is.Not.Null, "the box that wrote the rectangle is cut by it");
            Assert.That(pair[1], Is.Null, "and the property is not inherited, so the one inside is not");
        });
    }

    [Test]
    public async Task AChildThatAsksForTheRectangleAboveItMeasuresItFromItsOwnCorner()
    {
        var pair = await Paired(
            "#outer{clip:rect(10px, 200px, 200px, 10px)}#b{clip:inherit}",
            "<div id='outer'><div id='b'></div></div>");

        Assert.Multiple(() =>
        {
            Assert.That(pair[1], Is.Not.Null, "the keyword that asks is honoured wherever it is written");
            Assert.That(Edges(pair[1]!.Value.ClipOf(new Rect(0, 0, 100, 100))), Is.EqualTo((10d, 10d, 190d, 190d)),
                "the four distances come down, and are read against the box that asks");
        });
    }

    [Test]
    public async Task ABoxTakenOutOfTheFlowIsCutToTheRectangleItStates()
    {
        using var view = await Drawn(
            "#b{position:absolute;top:10px;left:20px;width:200px;height:200px;clip:rect(10px, 180px, 190px, 20px)}",
            "<div id='b'></div>");

        var cut = view.Paint!.Commands.OfType<PushClipCommand>().Single();

        Assert.That(Edges(cut.Bounds), Is.EqualTo((40d, 20d, 160d, 180d)),
            "the offsets run from the corner the box stands at, which the layout settled");
        Assert.That(cut.Radii.IsZero, Is.True, "a rectangle is cut square");
    }

    [Test]
    public async Task ABoxFixedToTheViewportIsCutByItsRectangleToo()
    {
        using var view = await Drawn(
            "#b{position:fixed;top:10px;left:20px;width:200px;height:200px;clip:rect(10px, 180px, 190px, 20px)}",
            "<div id='b'></div>");

        var cut = view.Paint!.FixedCommands.OfType<PushClipCommand>().Single();

        Assert.That(Edges(cut.Bounds), Is.EqualTo((40d, 20d, 160d, 180d)),
            "measured in Chrome on 2026-10-22: a fixed box answers to the property as an absolute one does");
    }

    [Test]
    public async Task ABoxThatStandsInTheFlowIsNotCutByWhatItStates()
    {
        var stated = "#b{width:200px;height:200px;clip:rect(10px, 180px, 190px, 20px)}";

        using var flow = await Drawn(stated, "<div id='b'></div>");
        using var relative = await Drawn($"#b{{position:relative;top:0;left:0}}{stated}", "<div id='b'></div>");

        Assert.Multiple(() =>
        {
            Assert.That(flow.Paint!.Commands.OfType<PushClipCommand>(), Is.Empty,
                "the property reaches an absolutely positioned box and nothing else (§11.1.1)");
            Assert.That(relative.Paint!.Commands.OfType<PushClipCommand>(), Is.Empty, "nor a relatively positioned one");
        });
    }

    [Test]
    public async Task TheCutHoldsTheBoxesOwnBackgroundBorderAndOutlineInsideIt()
    {
        using var view = await Drawn(
            "#b{position:absolute;top:0;left:0;width:100px;height:100px;background-color:#008000;" +
                "border:5px solid rgb(0 0 255);outline:2px solid rgb(255 0 0);clip:rect(10px, 90px, 90px, 10px)}",
            "<div id='b'></div>");

        var commands = view.Paint!.Commands.ToList();
        var push = commands.FindIndex(command => command is PushClipCommand);
        var pop = commands.FindIndex(command => command is PopClipCommand);

        var own = commands
            .Select((command, index) => (command, index))
            .Where(here => here.command is FillCommand or BorderCommand or OutlineCommand)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(own, Is.Not.Empty, "the box draws a background, a border and an outline");
            Assert.That(push, Is.LessThan(own[0].index), "the cut is opened before any of them");
            Assert.That(pop, Is.GreaterThan(own[^1].index),
                "and closed after the last, because the legacy clip cuts the outline the box wears too");
        });
    }

    [Test]
    public async Task WhatTheCutBoxHoldsIsCutWithIt()
    {
        // The clip is a cut through the painting of the whole subtree, which is what the gecko pair puts a
        // child larger than its parent over: the child's own rectangle is the parent's, whatever its size.
        using var view = await Drawn(
            "#outer{position:absolute;top:0;left:0;width:100px;height:100px;clip:rect(auto, auto, auto, auto)}" +
                "#b{width:300px;height:300px;margin:-100px;background-color:rgb(0 0 255)}",
            "<div id='outer'><div id='b'></div></div>");

        var commands = view.Paint!.Commands.ToList();
        var push = commands.FindIndex(command => command is PushClipCommand);
        var child = commands.FindIndex(command => command is FillCommand here && here.Color == Blue);

        Assert.Multiple(() =>
        {
            Assert.That(push, Is.GreaterThanOrEqualTo(0));
            Assert.That(child, Is.GreaterThan(push), "the child is drawn inside the parent's cut");
            Assert.That(Edges(commands[push].Bounds), Is.EqualTo((0d, 0d, 100d, 100d)), "to the parent's border box");
        });
    }

    [Test]
    public async Task ABoxThatStatesNothingIsNotCutWhateverItsParentWrote()
    {
        using var view = await Drawn(
            "#outer{position:absolute;top:0;left:0;width:100px;height:100px}" +
                "#b{position:absolute;top:0;left:0;width:300px;height:300px;background-color:rgb(0 0 255)}",
            "<div id='outer'><div id='b'></div></div>");

        Assert.That(view.Paint!.Commands.OfType<PushClipCommand>(), Is.Empty,
            "no rectangle written is no cut made");
    }

    [Test]
    public async Task ACutReachesTheSubtreeItWasWrittenOnEvenIntoTheViewportLayer()
    {
        // The pair this stands for is `clip-fixed-pos-transform-descendant-001`: a box cut to its own
        // border box holds a child fixed to the viewport, and that child is painted from a layer of its
        // own - which does not let it out of the cut above it, because a `clip` cuts the subtree it was
        // written on rather than the descendants whose containing block stands inside (CSS Masking 1
        // §1.3). An overflow clip is the opposite, and reaches nowhere near it.
        using var view = await Drawn(
            "#outer{position:absolute;top:0;left:0;width:100px;height:100px;clip:rect(0, 100px, 100px, 0)}" +
                "#b{position:fixed;top:10px;left:10px;width:200px;height:200px;background-color:rgb(255 0 0)}",
            "<div id='outer'><div id='b'></div></div>");

        var lifted = view.Paint!.FixedCommands;
        var cut = lifted.OfType<PushClipCommand>().SingleOrDefault();

        Assert.Multiple(() =>
        {
            Assert.That(cut, Is.Not.Null, "the fixed box is painted inside the cut its ancestor wrote");
            Assert.That(Edges(cut!.Bounds), Is.EqualTo((0d, 0d, 100d, 100d)));
            Assert.That(lifted.OfType<PopClipCommand>().Count(), Is.EqualTo(1), "and the pair is balanced");
        });
    }

    [Test]
    public async Task TheCutOfAClipThatLeavesNothingToDrawTakesTheBoxOutOfThePictureEntirely()
    {
        var commands = await Laid("#b{position:absolute;top:0;left:0;width:100px;height:100px;background-color:#008000;clip:rect(0, 0, 0, 0)}");

        Assert.Multiple(() =>
        {
            Assert.That(commands.OfType<PushClipCommand>().Single().Bounds, Is.EqualTo(Rect.Empty),
                "the four edges meet at the corner and leave no area");
            Assert.That(commands, Has.Count.GreaterThan(1), "the box is still drawn, and what is drawn is cut to nothing");
        });
    }

    /// <summary>
    /// Gets the rectangle the box under the style resolved, over a border box of 200x200 that is absolutely
    /// positioned at the page corner.
    /// </summary>
    private static async Task<ClipShape?> Cut(String css)
    {
        using var view = await Drawn($"{css}#b{{position:absolute;top:0;left:0;width:200px;height:200px}}", "<div id='b'></div>");

        return Box(view, "b").Source.Style.Clip;
    }

    /// <summary>Gets the one rectangle a test wrote, and says what it was that there should be exactly one.</summary>
    private static ClipShape Only(ClipShape? cut)
    {
        Assert.That(cut, Is.Not.Null, "a rectangle was written, and one should be read");

        return cut!.Value;
    }

    /// <summary>Gets the commands the page is painted with.</summary>
    private static async Task<List<PaintCommand>> Laid(String css, String body = "<div id='b'></div>")
    {
        using var view = await Drawn(css, body);

        return view.Paint!.Commands.ToList();
    }

    /// <summary>Gets the rectangles the outer box and the one inside it resolved, in that order.</summary>
    private static async Task<List<ClipShape?>> Paired(String css, String body)
    {
        using var view = await Drawn(
            $"{css}#outer{{position:absolute;top:0;left:0;width:200px;height:200px}}" +
                "#b{position:absolute;top:0;left:0;width:100px;height:100px}",
            body);

        return [Box(view, "outer").Source.Style.Clip, Box(view, "b").Source.Style.Clip];
    }

    private static async Task<HutuViewInternal> Drawn(String css, String body)
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

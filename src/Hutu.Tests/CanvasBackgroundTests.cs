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
/// Covers the background of the canvas - the surface the document is drawn on, which belongs to no
/// element and is therefore given one: the root's, or the body's where the root says nothing.
/// </summary>
/// <remarks>
/// CSS Backgrounds 3 §2.11 states the rule in two parts, and both are about taking a background away from
/// the box that wrote it: the root "does not paint this background again", and the used values of a body
/// whose background was handed over "are their initial values". What the canvas paints instead is the whole
/// of itself, while the layers inside it are still placed against the root's box - so a page whose body is
/// coloured and whose content is shorter than the window is coloured to every edge, which is what a reader
/// sees and what a box the size of the text never was.
/// </remarks>
[TestFixture]
public sealed class CanvasBackgroundTests
{
    private const UInt32 Green = 0xFF00_8000;
    private const UInt32 Red = 0xFFFF_0000;

    [Test]
    public async Task TheRootsBackgroundIsTheCanvasesAndTheRootKeepsNoneOfIt()
    {
        var painted = await Fills("html{background-color:#008000}", "<p>x</p>");

        Assert.Multiple(() =>
        {
            Assert.That(painted, Has.Count.EqualTo(1), "the root hands its background to the surface (§2.11.1)");
            Assert.That(painted[0].Color, Is.EqualTo(Green));
            Assert.That(Edges(painted[0].Bounds), Is.EqualTo(Canvas), "and it covers the canvas, not the root's box");
        });
    }

    [Test]
    public async Task TheBodysBackgroundGoesToTheCanvasWhereTheRootSaysNothing()
    {
        var painted = await Fills("html{background-color:transparent;background-image:none}body{background-color:#008000}", "<p>x</p>");

        Assert.Multiple(() =>
        {
            Assert.That(painted, Has.Count.EqualTo(1), "the body is the one that colours the page (§2.11.2)");
            Assert.That(painted[0].Color, Is.EqualTo(Green));
            Assert.That(Edges(painted[0].Bounds), Is.EqualTo(Canvas));
        });
    }

    [Test]
    public async Task ABodyThatWroteNoBackgroundLeavesTheCanvasTheUAsOwn()
    {
        Assert.That(await Fills("body{margin:0}", "<p>x</p>"), Is.Empty, "nothing was handed over, so nothing is painted");
    }

    [Test]
    public async Task ATransparentColourIsNoBackgroundToHandOver()
    {
        // The condition the chapter asks is on the computed values, and a colour with no ink in it is the
        // same answer as the absence of one: the body under it is the one that colours the page.
        var painted = await Fills("html{background-color:rgba(0,0,0,0)}body{background-color:#008000}", "<p>x</p>");

        Assert.Multiple(() =>
        {
            Assert.That(painted, Has.Count.EqualTo(1));
            Assert.That(painted[0].Color, Is.EqualTo(Green), "the body's colour, not the root's transparent one");
        });
    }

    [Test]
    public async Task TheRootKeepsItsOwnBackgroundWhenItStatesAnImageAndTheBodyAColor()
    {
        // §2.11.2 hands the body's background over only where the root's image is none *and* its colour
        // transparent. A root that states an image states a background, so the body keeps painting its own
        // box and the canvas wears the root's layer instead.
        using var view = await Drawn(
            "html{background-image:linear-gradient(#ff0000,#ff0000)}body{background-color:#008000;margin:0}",
            "<p>x</p>");

        var canvas = view.Paint!.Commands.Where(command => Edges(command.Bounds) == Canvas).ToList();
        var own = view.Paint!.Commands.OfType<FillCommand>().Where(command => command.Color == Green).Single();

        Assert.Multiple(() =>
        {
            Assert.That(canvas, Has.Count.EqualTo(1), "the root's layer is what the canvas wears (§2.11.1)");
            Assert.That(canvas[0], Is.InstanceOf<GradientCommand>());
            Assert.That(Edges(own.Bounds), Is.Not.EqualTo(Canvas), "the body keeps its colour for its own box");
        });
    }

    [Test]
    public async Task AHiddenBodyHasNoBackgroundToHandOverAndNoneToPaint()
    {
        // A body that generated no box has no background to hand to the canvas, and none to paint itself:
        // the page is the surface the UA puts under a document, and nothing more.
        var painted = await Solid("body{background-color:#ff0000;display:none}", "<p>x</p>");

        Assert.That(painted, Is.Empty, Describe(painted));
    }

    [Test]
    public async Task ARootThatGeneratedNoBoxLeavesTheDocumentWithoutACanvas()
    {
        var painted = await Solid("html{display:none}body{background-color:#ff0000}", "<p>x</p>");

        Assert.That(painted, Is.Empty,
            "the element whose background the canvas would wear is the one that is missing (§2.11) " + Describe(painted));
    }

    [Test]
    public async Task AContainedBodyKeepsItsBackgroundForItsOwnBox()
    {
        // The note to §2.11.2: using containment disables the special handling of the body element. What is
        // left is an ordinary box painting an ordinary background, over its own area only.
        var painted = await Solid("body{contain:paint;background-color:#008000;height:100px}", "<p>x</p>");

        Assert.Multiple(() =>
        {
            Assert.That(painted, Has.Count.EqualTo(1));
            Assert.That(painted[0].Color, Is.EqualTo(Green));
            Assert.That(Edges(painted[0].Bounds), Is.Not.EqualTo(Canvas), "the body's own box, not the surface");
        });
    }

    [Test]
    public async Task AContainedRootStopsTheBodysBackgroundFromReachingTheCanvas()
    {
        var painted = await Solid("html{contain:paint}body{background-color:#008000;height:100px}", "<p>x</p>");

        Assert.Multiple(() =>
        {
            Assert.That(painted, Has.Count.EqualTo(1));
            Assert.That(Edges(painted[0].Bounds), Is.Not.EqualTo(Canvas));
        });
    }

    [Test]
    public async Task ContainmentOfAnyKindIsContainmentForThisPurpose()
    {
        // The chapter names no kind: any containment disables the handing over, so a body that contains its
        // style is kept from the canvas as surely as one that contains its paint.
        var painted = await Solid("body{contain:style;background-color:#ff0000;height:100px}", "<p>x</p>");

        Assert.Multiple(() =>
        {
            Assert.That(painted, Has.Count.EqualTo(1));
            Assert.That(Edges(painted[0].Bounds), Is.Not.EqualTo(Canvas));
        });
    }

    [Test]
    public async Task ADisplayContentsElementPaintsNoBackground()
    {
        // CSS Display 3 §2.5: the element generates no box, so the background it states has nothing to
        // paint on; its children are the page.
        using var view = await Drawn(
            "body{margin:0}",
            "<div style='display:contents;background-color:#ff0000'>" +
            "<div style='width:20px;height:20px;background-color:#008000'></div></div>");

        var solid = SolidOf(view);

        Assert.Multiple(() =>
        {
            Assert.That(solid.Exists(fill => fill.Color == Red), Is.False, "no box, so no red");
            Assert.That(solid.Exists(fill => fill.Color == Green), Is.True, "the child still paints");
        });
    }

    [Test]
    public async Task TheCanvasIsPaintedBeforeAnythingTheDocumentPutsOnIt()
    {
        using var view = await Drawn("html{background-color:#ff0000}body{margin:0}", "<div style='background-color:#008000;width:20px;height:20px'></div>");
        var commands = view.Paint!.Commands.ToList();

        Assert.That(commands.FindIndex(command => command is FillCommand { Color: Red }),
            Is.LessThan(commands.FindIndex(command => command is FillCommand { Color: Green })),
            "the surface is the first thing drawn, and the document lies over it");
    }

    [Test]
    public async Task ABackgroundHandedToTheCanvasIsStillPlacedAgainstTheRootsBox()
    {
        using var view = await Drawn(
            "html{background-image:linear-gradient(#ff0000,#ff0000);background-size:100px 100px;margin:0}" +
            "body{margin:0}",
            "<p>x</p>");

        var gradient = view.Paint!.Commands.OfType<GradientCommand>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(Edges(gradient.Bounds), Is.EqualTo(Canvas), "the layer is painted over the whole canvas");
            Assert.That(gradient.Tile.Width, Is.EqualTo(100));
            Assert.That(gradient.Tile.X, Is.EqualTo(view.Layout!.Root.BorderBox.X), "and stood where the root's box begins (§2.11.1)");
        });
    }

    /// <summary>
    /// Gets the solid fills the canvas was painted with - the commands at the head of the list, which cover
    /// the whole of the surface rather than a box of the document.
    /// </summary>
    private static async Task<List<FillCommand>> Fills(String css, String body)
    {
        using var view = await Drawn(css, body);
        var canvas = Canvas;

        return SolidOf(view).Where(command => Edges(command.Bounds) == canvas).ToList();
    }

    /// <summary>Gets every solid fill the page painted, on the canvas or on a box of the document.</summary>
    private static async Task<List<FillCommand>> Solid(String css, String body)
    {
        using var view = await Drawn(css, body);

        return SolidOf(view);
    }

    /// <summary>
    /// Gets the solid fills of a painted page. A document the engine draws nothing for has no fills to list
    /// either, which is the answer a page whose root element left no box behind comes to.
    /// </summary>
    private static List<FillCommand> SolidOf(HutuViewInternal view) =>
        view.Paint is null ? new List<FillCommand>() : view.Paint.Commands.OfType<FillCommand>().ToList();

    /// <summary>Says what the page did paint, for the message of an assertion that it should not have.</summary>
    private static String Describe(List<FillCommand> painted) =>
        String.Join(
            "; ",
            painted.Select(fill => $"{fill.Bounds.X},{fill.Bounds.Y} {fill.Bounds.Width}x{fill.Bounds.Height} #{fill.Color:X8}"));

    private static async Task<HutuViewInternal> Drawn(String css, String body)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml($"<html><head><style>{css}</style></head><body>{body}</body></html>");

        return view;
    }

    /// <summary>The canvas the tests are drawn in: the whole of what the page was laid out to fill.</summary>
    private static (Double X, Double Y, Double Width, Double Height) Canvas => (0d, 0d, 400d, 600d);

    private static (Double X, Double Y, Double Width, Double Height) Edges(Rect area) =>
        (area.X, area.Y, area.Width, area.Height);
}

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
/// Covers the shadows a box and a run of text are drawn with: what a declaration of them comes to, and
/// where the picture of one is laid.
/// </summary>
/// <remarks>
/// The specification splits the two in the same place, so the tests do too. §6.1 of CSS Backgrounds 3 says
/// what a shadow is - two offsets, a blur, a spread and a colour a page may leave to the element; §6.1.1
/// and §6.1.3 say what shape that makes of the box it was written on and where that shape is painted. The
/// first half reads the answer off the style, where a wrong one is a wrong number; the second reads the
/// commands the painter emitted, where a wrong one is a wrong picture.
/// <para>
/// What the second half expects is what the chapter works out rather than what a browser was seen to draw:
/// a shadow leaves no geometry a script can be asked about, so the only oracles for the drawing are the
/// chapter's own construction - and the borrowed pairs, which build a shadowed box out of plain
/// rectangles and insist the two come to the same picture.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ShadowTests
{
    private const UInt32 Green = 0xFF00_8000;
    private const UInt32 Red = 0xFFFF_0000;
    private const UInt32 Blue = 0xFF00_00FF;
    private const UInt32 Lime = 0xFF00_FF00;

    [Test]
    public async Task AShadowThatNamesNoColourIsDrawnInTheInkOfTheBoxThatCastsIt()
    {
        var shadow = Only(await Casts("#b{color:#008000;box-shadow:3px 3px}"));

        Assert.Multiple(() =>
        {
            Assert.That(shadow.Color, Is.EqualTo(Green), "an omitted colour is the element's own ink (CSS Backgrounds 3 §6.1)");
            Assert.That(shadow.OffsetX, Is.EqualTo(3));
            Assert.That(shadow.OffsetY, Is.EqualTo(3));
            Assert.That(shadow.Blur, Is.EqualTo(0), "an omitted length is nothing, not a missing value");
            Assert.That(shadow.Spread, Is.EqualTo(0));
            Assert.That(shadow.Inset, Is.False);
        });
    }

    [Test]
    public async Task TheKeywordThatAsksForTheInkMeansTheSameAsSayingNothing()
    {
        var written = Only(await Casts("#b{color:#008000;box-shadow:currentcolor 3px 3px}"));
        var left = Only(await Casts("#b{color:#008000;box-shadow:3px 3px}"));

        Assert.That(written, Is.EqualTo(left), "the keyword and the absence of a colour come to one answer (§6.1)");
    }

    [Test]
    public async Task TheFourLengthsOfAShadowAreReadInOrderAndAColourMayStandAmongThem()
    {
        var shadow = Only(await Casts("#b{box-shadow:1px 2px 3px 4px rgb(0 0 255)}"));

        Assert.Multiple(() =>
        {
            Assert.That(shadow.OffsetX, Is.EqualTo(1));
            Assert.That(shadow.OffsetY, Is.EqualTo(2));
            Assert.That(shadow.Blur, Is.EqualTo(3));
            Assert.That(shadow.Spread, Is.EqualTo(4));
            Assert.That(shadow.Color, Is.EqualTo(Blue));
        });
    }

    [Test]
    public async Task AShadowIsReadInTheFontOfTheBoxAndInTheRoots()
    {
        var shadow = Only(await Casts("#b{font-size:10px;box-shadow:1em 2rem}"));

        Assert.Multiple(() =>
        {
            Assert.That(shadow.OffsetX, Is.EqualTo(10), "an em is the element's own size, not the one it inherited");
            Assert.That(shadow.OffsetY, Is.EqualTo(32), "and a rem is the root's, which the device holds at 16");
        });
    }

    [Test]
    public async Task AShadowWhoseCalculationNeedsTheFontIsFoldedBeforeItIsDrawn()
    {
        var shadow = Only(await Casts("#b{font-size:16px;box-shadow:2px 2px calc(1em + 3px)}"));

        Assert.That(shadow.Blur, Is.EqualTo(19), "the blur is the length the expression came to");
    }

    [Test]
    public async Task TheInsetKeywordAnywhereInTheShadowMakesItAnInnerOne()
    {
        var first = Only(await Casts("#b{box-shadow:inset 3px 3px}"));
        var last = Only(await Casts("#b{box-shadow:3px 3px inset}"));
        var between = Only(await Casts("#b{box-shadow:rgb(0 0 255) inset 0 0 4px}"));

        Assert.Multiple(() =>
        {
            Assert.That(first.Inset, Is.True, "the keyword is written first");
            Assert.That(last.Inset, Is.True, "and it is written last");
            Assert.That(between.Inset, Is.True, "and between a colour and a length");
            Assert.That(between.Color, Is.EqualTo(Blue));
        });
    }

    [Test]
    public async Task TheListKeepsTheOrderThePageWrote()
    {
        var shadows = await Casts("#b{box-shadow:1px 1px red, 2px 2px blue, 3px 3px lime}");

        Assert.Multiple(() =>
        {
            Assert.That(shadows, Has.Count.EqualTo(3));
            Assert.That(shadows[0].Color, Is.EqualTo(Red), "the first written is the first of the list, and the one on top");
            Assert.That(shadows[1].Color, Is.EqualTo(Blue));
            Assert.That(shadows[2].Color, Is.EqualTo(Lime));
        });
    }

    [Test]
    public async Task AShadowWithOneLengthOnlyIsNoShadowAtAll()
    {
        Assert.That(await Casts("#b{box-shadow:10px}"), Is.Empty, "two offsets are the least a shadow can be");
    }

    [Test]
    public async Task ABlurBelowZeroIsRefusedAndTakesTheWholeListDownWithIt()
    {
        // The grammar bounds the blur at zero (§6.1), and a list is one value: an item the grammar refuses
        // is not the list's to keep.
        Assert.That(await Casts("#b{box-shadow:0 0 -5px black}"), Is.Empty);
        Assert.That(await Casts("#b{box-shadow:1px 1px red, 2px 2px -1px blue}"), Is.Empty);
    }

    [Test]
    public async Task APercentageWrittenWhereAShadowWantsALengthIsRefused()
    {
        Assert.That(await Casts("#b{box-shadow:10% 10%}"), Is.Empty, "a shadow is made of lengths and nothing else");
        Assert.That(await Casts("#b{box-shadow:0 0 0 50%}"), Is.Empty);
    }

    [Test]
    public async Task AKeywordStandingWhereTheListOfShadowsShouldBeRefusesTheWholeDeclaration()
    {
        Assert.That(await Casts("#b{box-shadow:none, 4px 4px}"), Is.Empty, "a list of shadows cannot begin with the keyword");
        Assert.That(await Casts("#b{box-shadow:2px 2px red, none}"), Is.Empty);
    }

    [Test]
    public async Task TwoInsetKeywordsInOneShadowAreNotAShadow()
    {
        Assert.That(await Casts("#b{box-shadow:10px 20px 5px 6px inset rgb(0 0 255) inset}"), Is.Empty);
    }

    [Test]
    public async Task FiveLengthsAreMoreThanAShadowHas()
    {
        Assert.That(await Casts("#b{box-shadow:1px 2px 3px 4px 5px}"), Is.Empty);
    }

    [Test]
    public async Task TheInheritanceOfAShadowThatWasNotWrittenOnTheBoxEndsAtNone()
    {
        // `box-shadow` is not inherited, so a box under a shadowed one casts nothing of its own - but the
        // keyword that asks is honoured wherever it is written.
        var plain = await Paired("#outer{box-shadow:3px 3px black}", "<div id='outer'><div id='b'></div></div>");
        var asked = await Paired("#outer{box-shadow:3px 3px black}#b{box-shadow:inherit}", "<div id='outer'><div id='b'></div></div>");

        Assert.Multiple(() =>
        {
            Assert.That(plain[1], Is.Empty, "a shadow is the box's own, and does not fall down to it");
            Assert.That(asked[1], Has.Count.EqualTo(1), "unless the box asks for the one above it");
        });
    }

    [Test]
    public async Task AShadowOnTheTextIsInheritedDownToTheWordsBelowIt()
    {
        var shadows = await Words("#outer{text-shadow:2px 3px rgb(0 0 255)}", "<div id='outer'><p id='b'>x</p></div>");

        Assert.Multiple(() =>
        {
            Assert.That(shadows, Has.Count.EqualTo(1), "the run of text below the box that wrote the shadow carries it");
            Assert.That(shadows[0].OffsetX, Is.EqualTo(2));
            Assert.That(shadows[0].OffsetY, Is.EqualTo(3));
            Assert.That(shadows[0].Color, Is.EqualTo(Blue));
        });
    }

    [Test]
    public async Task ATextShadowTakesNeitherASpreadNorTheInsetKeyword()
    {
        // The grammar of CSS Text Decoration 4 §4.1 gives a text shadow two offsets, a blur and a colour;
        // a fourth length and the keyword are the box property's alone, and a page that writes one gets
        // neither shadow at all.
        var spread = await Words("#b{text-shadow:1px 2px 3px 4px}", "<div id='b'>x</div>");
        var inset = await Words("#b{text-shadow:inset 1px 2px 3px}", "<div id='b'>x</div>");

        Assert.Multiple(() =>
        {
            Assert.That(spread, Is.Empty, "the spread is not a text shadow's to take");
            Assert.That(inset, Is.Empty, "nor the inset keyword");
        });
    }

    [Test]
    public async Task TheShadowWrittenForTheFirstLineStylesTheLineItReaches()
    {
        var shadows = await Words(
            "#b{text-shadow:1px 1px rgb(255 0 0)}#b::first-line{text-shadow:4px 4px rgb(0 0 255)}",
            "<div id='b'>x</div>");

        Assert.Multiple(() =>
        {
            Assert.That(shadows, Has.Count.EqualTo(1));
            Assert.That(shadows[0].OffsetX, Is.EqualTo(4), "the line the arm was written on is drawn with the arm's shadow");
            Assert.That(shadows[0].Color, Is.EqualTo(Blue));
        });
    }

    [Test]
    public async Task AShadowReachesTheRunOfAColumnAsSurelyAsTheRunOfALine()
    {
        var shadows = await Words(
            "#b{writing-mode:vertical-rl;text-shadow:2px 3px rgb(0 0 255)}",
            "<div id='b'>x</div>");

        Assert.Multiple(() =>
        {
            Assert.That(shadows, Has.Count.EqualTo(1), "a vertical run is text, and text is shadowed the same way");
            Assert.That(shadows[0].OffsetX, Is.EqualTo(2), "the offsets are physical: right is right in either flow");
            Assert.That(shadows[0].OffsetY, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task TheMarkAnItemWearsIsDrawnWithTheShadowWrittenForIt()
    {
        using var view = await Drawn(
            "ul{margin:0}li::marker{text-shadow:2px 2px rgb(0 0 255)}",
            "<ul><li id='i'>one</li></ul>");

        var bounds = Box(view, "i").MarkerBounds!.Value;
        var marker = view.Paint!.Commands.OfType<TextCommand>().Single(command =>
            command.Bounds.X == bounds.X && command.Bounds.Y == bounds.Y);

        Assert.Multiple(() =>
        {
            Assert.That(marker.Shadows, Has.Count.EqualTo(1), "the rules written for the mark are read for the mark");
            Assert.That(marker.Shadows[0].Color, Is.EqualTo(Blue));
        });
    }

    [Test]
    public async Task AnOuterShadowIsTheBoxMovedByItsOffsetsWithTheBoxCutOutOfIt()
    {
        var cast = await Cast("#b{box-shadow:3px 4px black}");

        Assert.Multiple(() =>
        {
            Assert.That(cast.Inset, Is.False);
            Assert.That(Edges(cast.Shape), Is.EqualTo((3d, 4d, 40d, 20d)), "the shape is the border box, moved");
            Assert.That(Edges(cast.Area), Is.EqualTo((0d, 0d, 40d, 20d)), "and what the box stands on is cut out of it (§6.1.1)");
        });
    }

    [Test]
    public async Task ASpreadGrowsTheShapeAndGrowsTheCornersWithIt()
    {
        var cast = await Cast("#b{border-radius:6px;box-shadow:2px 3px 0 4px black}");

        Assert.Multiple(() =>
        {
            // The straight edges are outset by the spread and the corners with them, so that the shape keeps
            // the box's own, and the offsets move the whole of it afterwards (§6.1.1).
            Assert.That(Edges(cast.Shape), Is.EqualTo((-2d, -1d, 48d, 28d)));
            Assert.That(cast.ShapeRadii.TopLeft.Horizontal, Is.EqualTo(10).Within(0.001));
            Assert.That(cast.AreaRadii.TopLeft.Horizontal, Is.EqualTo(6).Within(0.001), "the box keeps the radius it was given");
        });
    }

    [Test]
    public async Task ASpreadWiderThanTheBoxLeavesNothingToDraw()
    {
        var cast = await Cast("#b{box-shadow:0 0 0 -60px black}");

        Assert.Multiple(() =>
        {
            // The size the spread leaves is floored at zero, and the edges are left where the outsetting put
            // them rather than folded back into a rectangle of negative size (§6.1.1).
            Assert.That(cast.Shape.Width, Is.EqualTo(0));
            Assert.That(cast.Shape.Height, Is.EqualTo(0));
            Assert.That(cast.Shape.X, Is.EqualTo(60), "the edge is left where the insetting put it");
            Assert.That(cast.ShapeRadii.IsZero, Is.True, "a shape with no corners left to round");
        });
    }

    [Test]
    public async Task AnInnerShadowIsDrawnInsideThePaddingBoxAndShrunkByItsSpread()
    {
        var cast = await Cast("#b{border:5px solid rgb(0 0 255);box-shadow:inset 2px 0 0 3px black}");

        Assert.Multiple(() =>
        {
            Assert.That(cast.Inset, Is.True);
            Assert.That(Edges(cast.Area), Is.EqualTo((5d, 5d, 40d, 20d)), "the box an inner shadow is clipped to is the padding box (§6.1.1)");
            Assert.That(Edges(cast.Shape), Is.EqualTo((10d, 8d, 34d, 14d)), "and its shape is that box, inset and then moved");
        });
    }

    [Test]
    public async Task AShadowThatIsBlurredCoversTheRoomItsEdgeNeedsToFade()
    {
        var cast = await Cast("#b{box-shadow:2px 0 4px black}");

        Assert.Multiple(() =>
        {
            Assert.That(Edges(cast.Shape), Is.EqualTo((2d, 0d, 40d, 20d)));
            Assert.That(cast.Blur, Is.EqualTo(4));
            Assert.That(Edges(cast.Bounds), Is.EqualTo((-6d, -8d, 56d, 36d)),
                "the blur reaches out around the shape, and it is that whole area a backend is culled against");
        });
    }

    [Test]
    public async Task TheShadowsOfOneBoxAreLaidLastFirstSoTheFirstWrittenIsOnTop()
    {
        using var view = await Drawn("#b{box-shadow:1px 1px rgb(255 0 0), 2px 2px rgb(0 0 255)}", "<div id='b'></div>");
        var casts = view.Paint!.Commands.OfType<BoxShadowCommand>().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(casts, Has.Count.EqualTo(2));
            Assert.That(casts[0].Color, Is.EqualTo(Blue), "the one written last is drawn first, and so stands furthest back");
            Assert.That(casts[1].Color, Is.EqualTo(Red), "and the first written is the last laid, over the rest (§6.1.3)");
        });
    }

    [Test]
    public async Task AnOuterShadowIsLaidUnderTheBackgroundAndAnInnerOneOverIt()
    {
        using var view = await Drawn(
            "#b{background-color:rgb(0 0 255);box-shadow:inset 0 0 0 2px rgb(255 0 0), 4px 4px rgb(0 128 0)}",
            "<div id='b'></div>");

        var order = view.Paint!.Commands.Where(command => command is BoxShadowCommand or FillCommand).ToList();

        var background = order.FindIndex(command => command is FillCommand);
        var outer = order.FindIndex(command => command is BoxShadowCommand here && !here.Inset);
        var inner = order.FindIndex(command => command is BoxShadowCommand here && here.Inset);

        Assert.Multiple(() =>
        {
            Assert.That(outer, Is.LessThan(background), "the outer shadow is drawn immediately below the background (§6.1.3)");
            Assert.That(inner, Is.GreaterThan(background), "and the inner one immediately above it");
        });
    }

    [Test]
    public async Task AnInnerShadowStandsUnderTheBorderItIsDrawnBeside()
    {
        using var view = await Drawn(
            "#b{border:2px solid rgb(0 0 255);box-shadow:inset 0 0 0 2px rgb(255 0 0)}",
            "<div id='b'></div>");

        var commands = view.Paint!.Commands.ToList();
        var inner = commands.FindIndex(command => command is BoxShadowCommand);
        var border = commands.FindIndex(command => command is BorderCommand);

        Assert.That(inner, Is.LessThan(border), "the border, and the border image with it, are painted over an inner shadow (§6.1.3)");
    }

    [Test]
    public async Task AnInternalTableElementOfACollapsingTableCastsNoOuterShadow()
    {
        var collapsing = await Table("collapse");
        var separated = await Table("separate");

        Assert.Multiple(() =>
        {
            Assert.That(collapsing.Read, Has.Count.EqualTo(1), "the row reads the shadow it was written with");
            Assert.That(collapsing.Painted, Is.EqualTo(1),
                "a table whose borders collapse leaves its row's outer shadow unpainted (§6.1.3)");
            Assert.That(separated.Painted, Is.EqualTo(2),
                "in the separated model an internal element is a box like any other, and both cast");
        });
    }

    [Test]
    public async Task AShadowThatAsksForTheInkIsDrawnInEachInkItReachesAndNotTheOneItCameFrom()
    {
        // The asking travels down the tree and the answer is each box's own (CSS Color 4 §4, CSS
        // Backgrounds 3 §6.1): the list a child inherits is the one its parent wrote, but the ink it asks
        // for is the child's own.
        var pair = await Paired(
            "#outer{color:red;box-shadow:3px 3px currentcolor}#b{box-shadow:inherit;color:#008000}",
            "<div id='outer'><div id='b'></div></div>");

        Assert.Multiple(() =>
        {
            Assert.That(pair[0], Has.Count.EqualTo(1));
            Assert.That(pair[0][0].Color, Is.EqualTo(Red), "the box that wrote the asking is drawn in its own ink");
            Assert.That(pair[1][0].Color, Is.EqualTo(Green), "and the box that inherited the asking in its own");
        });
    }

    [Test]
    public async Task AShadowInheritedFromABlankInkIsDrawnInTheInkThatReplacedIt()
    {
        // The pair this stands for is `box-shadow-currentcolor.html`: a div whose ink is transparent writes
        // the asking, and the box under it that asks for the inherited list is drawn in the colour it was
        // given - which is the whole of what that test asserts.
        var pair = await Paired(
            "#outer{color:transparent;box-shadow:4px 4px currentcolor}#b{box-shadow:inherit;color:#008000}",
            "<div id='outer'><div id='b'></div></div>");

        using var view = await Drawn(
            "#outer{position:absolute;top:0;left:0;width:60px;height:60px;color:transparent;box-shadow:4px 4px currentcolor}" +
            "#b{position:absolute;top:0;left:0;width:20px;height:20px;box-shadow:inherit;color:#008000}",
            "<div id='outer'><div id='b'></div></div>");

        var cast = view.Paint!.Commands.OfType<BoxShadowCommand>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(pair[0][0].Color, Is.EqualTo(0u), "a box whose ink is transparent is given a shadow of nothing");
            Assert.That(cast.Color, Is.EqualTo(Green), "the box that inherited the asking takes its own ink (CSS Color 4 §4)");
            Assert.That(cast.Shape.X, Is.EqualTo(4), "while the offsets come down the inheritance with it");
        });
    }

    [Test]
    public async Task ATextShadowLeftWithoutAColourIsDrawnInTheInkOfTheWordsItShadowsWithEach()
    {
        var shadows = await Words(
            "#outer{color:red;text-shadow:2px 2px}#b{color:#008000}",
            "<div id='outer'><p id='b'>x</p></div>");

        Assert.Multiple(() =>
        {
            Assert.That(shadows, Has.Count.EqualTo(1));
            Assert.That(shadows[0].Color, Is.EqualTo(Green), "the shadow falls down, and the ink it asks for does not");
        });
    }

    [Test]
    public async Task TheMarkStandingInsideItsItemIsDrawnWithTheShadowWrittenForIt()
    {
        using var view = await Drawn(
            "ul{margin:0;list-style-position:inside}li::marker{text-shadow:2px 2px rgb(0 0 255)}",
            "<ul><li>one</li></ul>");

        var runs = view.Paint!.Commands.OfType<TextCommand>().ToList();
        var marked = runs.Where(run => run.Shadows.Count > 0).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(runs, Has.Count.GreaterThanOrEqualTo(2), "the mark and its words are drawn as two runs");
            Assert.That(marked, Has.Count.EqualTo(1), "and only the mark is drawn with the shadow written for it");
            Assert.That(marked[0].Text, Is.Not.EqualTo("one"), "which is not the item's own words");
            Assert.That(marked[0].Shadows[0].Color, Is.EqualTo(Blue));
        });
    }

    /// <summary>
    /// Gets the shadows the box under the style was resolved with, in the order the page wrote them.
    /// </summary>
    private static async Task<IReadOnlyList<Shadow>> Casts(String css, String body = "<div id='b'></div>")
    {
        using var view = await Drawn($"{css}#b{{position:absolute;top:0;left:0;width:40px;height:20px}}", body);

        return Box(view, "b").Source.Style.BoxShadows;
    }

    /// <summary>Gets the one shadow of a box, and says what it was that there should be exactly one.</summary>
    private static Shadow Only(IReadOnlyList<Shadow> shadows)
    {
        Assert.That(shadows, Has.Count.EqualTo(1), "one shadow was written, and one should be read");

        return shadows[0];
    }

    /// <summary>Gets the one shadow the box casts, with the shape it is drawn from and the box cut out of it.</summary>
    private static async Task<BoxShadowCommand> Cast(String css)
    {
        using var view = await Drawn($"{css}#b{{position:absolute;top:0;left:0;width:40px;height:20px}}", "<div id='b'></div>");

        return view.Paint!.Commands.OfType<BoxShadowCommand>().Single();
    }

    /// <summary>Gets the shadows of every run of text the page draws.</summary>
    /// <remarks>
    /// The box stands where the page puts it, because a line belongs to a block in the flow: the arm
    /// written <c>::first-line</c> reaches the lines of an out-of-flow box through a different door, and
    /// these tests want the one the words are set in.
    /// </remarks>
    private static async Task<List<Shadow>> Words(String css, String body)
    {
        using var view = await Drawn($"{css}#b{{width:40px;height:20px}}", body);

        return view.Paint!.Commands.OfType<TextCommand>().SelectMany(run => run.Shadows).ToList();
    }

    /// <summary>Gets the shadows of the outer box and of the one inside it, in that order.</summary>
    private static async Task<List<IReadOnlyList<Shadow>>> Paired(String css, String body)
    {
        using var view = await Drawn(
            $"{css}#outer{{position:absolute;top:0;left:0;width:60px;height:60px}}#b{{width:20px;height:20px}}",
            body);

        return [Box(view, "outer").Source.Style.BoxShadows, Box(view, "b").Source.Style.BoxShadows];
    }

    /// <summary>
    /// Gets the shadows the row of a table reads, and how many shadows the table and its row were painted
    /// with.
    /// </summary>
    private static async Task<(Int32 Painted, IReadOnlyList<Shadow> Read)> Table(String collapse)
    {
        using var view = await Drawn(
            $"#b{{border-collapse:{collapse};box-shadow:3px 3px black}}#r{{box-shadow:3px 3px black}}" +
                "#b{position:absolute;top:0;left:0}",
            "<table id='b'><tr id='r'><td>x</td></tr></table>");

        return (view.Paint!.Commands.OfType<BoxShadowCommand>().Count(), Box(view, "r").Source.Style.BoxShadows);
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

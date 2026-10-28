using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the box an inline element is given: a band around its own runs rather than around a whole
/// line, one band per line where the element was broken, carrying the background, the padding, the
/// border and the radii the element declared.
/// </summary>
/// <remarks>
/// Everything is asserted against the fake measurer's fixed numbers - five pixels a character, an
/// ascent of eight and a descent of two, whatever the font size - so that the geometry is the layout's
/// own arithmetic and not the machine's fonts.
/// </remarks>
[TestFixture]
public sealed class InlineBoxTests
{
    [Test]
    public async Task AnElementDrawsABandTheHeightOfItsOwnFont()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<mark>abcd</mark>"));

        var mark = Inline(view, "mark");

        Assert.That(mark.Children.Single().Text, Is.EqualTo("abcd"), "the run it holds sits inside the band");
        Assert.That(mark.BorderBox.X, Is.EqualTo(0));
        Assert.That(mark.BorderBox.Width, Is.EqualTo(20), "the band covers the runs and nothing more");
        Assert.That(mark.BorderBox.Height, Is.EqualTo(10), "the band is the font's content area");
        Assert.That(mark.BorderBox.Y, Is.EqualTo(5), "and it is centred on the line's shared baseline");
    }

    [Test]
    public async Task ALineIsAsTallAsItsLineHeightHoweverTallTheFontIs()
    {
        using var view = CreateView();

        // The measurer gives every font ten pixels of ascent and descent whatever it is set at, so a
        // line height of five is a font taller than its line - and the line box is still five: a face
        // that overflows the line it is set on is drawn overflowing it.
        await view.SetHtml(Document("<p style='margin:0;font-size:5px;line-height:1'>abcd</p>"));

        Assert.That(
            Lines(view).Single().BorderBox.Height,
            Is.EqualTo(5),
            "the line is the line height, not the height of the font set on it");
    }

    [Test]
    public async Task TheBandOfAnElementIsStillTheHeightOfItsFont()
    {
        using var view = CreateView();

        // The other half of the same rule: an element's own band is its font's content area, which is
        // taller here than the line the band sits on.
        await view.SetHtml(Document("<p style='margin:0;font-size:5px;line-height:1'><mark>abcd</mark></p>"));

        Assert.That(Inline(view, "mark").BorderBox.Height, Is.EqualTo(10), "the band is the font's content area");
        Assert.That(Lines(view).Single().BorderBox.Height, Is.EqualTo(5), "and the line is the line height");
    }

    [Test]
    public async Task ALineWithNoHeightStillDrawsWhatIsOnIt()
    {
        using var view = CreateView();

        // A run set with no line height at all overflows the line it sits on rather than taking the line
        // away with it: the line box is nothing tall and the text is still there to be drawn.
        await view.SetHtml(Document("<p style='margin:0;font-size:10px;line-height:0'>abcd</p>"));

        Assert.That(Lines(view).Single().BorderBox.Height, Is.EqualTo(0), "the line is nothing tall");
        Assert.That(Text(view, "abcd").BorderBox.Width, Is.EqualTo(20), "and the run it holds is still placed");
    }

    [Test]
    public async Task AKeywordThatStandsForNoValueIsNotALength()
    {
        using var view = CreateView();

        // `font: inherit` is answered by AngleSharp with zero pixels, which read as a font of no size at
        // all - a label with a font of nothing is invisible.
        await view.SetHtml(Document("<p style='margin:0'><span style='font: inherit'>abcd</span></p>"));

        Assert.That(Inline(view, "span").Source.Style.FontSize, Is.GreaterThan(0), "the span is set in some size of type");
    }

    [Test]
    public async Task AnElementWithNothingToDrawStillGetsABox()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<em>abcd</em>"));

        // The box has to be there whatever the element draws, because whether it draws anything is a
        // question for the painter: a style that only adds a background is a change the layout is
        // deliberately reused through, so the node the background would land on must already exist.
        Assert.That(Inline(view, "em"), Is.Not.Null);

        Assert.That(Fills(view), Is.Empty);
        Assert.That(Borders(view), Is.Empty);
    }

    [Test]
    public async Task PaddingAndBorderGrowTheBandOnEverySide()
    {
        using var view = CreateView();

        await view.SetHtml(Document("abc<span style='padding:2px 5px;border:1px solid #000000'>ab</span>"));

        var span = Inline(view, "span");
        var run = Text(view, "ab");

        // The run sits fifteen pixels along, where it would have been without any of this, and six
        // pixels of padding and border on either side of it make the band twenty-two wide.
        Assert.That(run.BorderBox.X, Is.EqualTo(21));
        Assert.That(span.BorderBox.X, Is.EqualTo(15), "the band begins a border and a padding before its first run");
        Assert.That(span.BorderBox.Width, Is.EqualTo(22));
        Assert.That(span.BorderBox.Y, Is.EqualTo(2));
        Assert.That(span.BorderBox.Height, Is.EqualTo(16), "1 + 2 + 8 + 2 + 2 + 1");
    }

    [Test]
    public async Task InlinePaddingAndBorderDoNotGrowTheLine()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<mark style='padding:6px;border:4px solid #000000'>abcd</mark>"));

        Assert.That(
            Lines(view).Single().BorderBox.Height,
            Is.EqualTo(20),
            "inline padding and border overflow the line rather than widening it");
    }

    [Test]
    public async Task LineHeightMovesTheBandButDoesNotGrowIt()
    {
        using var tight = CreateView();
        using var loose = CreateView();

        await tight.SetHtml(Document("<mark>abcd</mark>", lineHeight: "1"));
        await loose.SetHtml(Document("<mark>abcd</mark>", lineHeight: "3"));

        var a = Inline(tight, "mark");
        var b = Inline(loose, "mark");

        Assert.That(a.BorderBox.Height, Is.EqualTo(10));
        Assert.That(b.BorderBox.Height, Is.EqualTo(10), "an inline element's band does not follow the line height");
        Assert.That(a.BorderBox.Y, Is.EqualTo(0));
        Assert.That(b.BorderBox.Y, Is.EqualTo(10), "it only moves with the baseline the line settled on");
    }

    [Test]
    public async Task AnElementBrokenOverLinesLeavesOneBandPerLine()
    {
        using var view = CreateView(40);

        await view.SetHtml(Document("aaaa <span style='padding:0 5px;border:1px solid #000000'>abcd efgh</span>", width: 40));

        var bands = Inlines(view, "span").ToList();

        Assert.That(bands.Count, Is.EqualTo(2), "the element was broken, so each line gets a band of its own");

        var head = Text(view, "abcd");
        var tail = Text(view, "efgh");

        // The element began on the first line, so that line draws its start edge and stops where the
        // content does: the end edge was cut, and belongs to the line the element ends on.
        Assert.That(bands[0].SlicesInlineStart, Is.False);
        Assert.That(bands[0].SlicesInlineEnd, Is.True);
        Assert.That(bands[0].BorderBox.X, Is.EqualTo(head.BorderBox.X - 6));
        Assert.That(bands[0].BorderBox.Right, Is.EqualTo(head.BorderBox.Right));
        Assert.That(bands[0].Padding.Right, Is.EqualTo(0), "a cut edge carries no padding");

        // The mirror of it on the line the element carries on to.
        Assert.That(bands[1].SlicesInlineStart, Is.True);
        Assert.That(bands[1].SlicesInlineEnd, Is.False);
        Assert.That(bands[1].BorderBox.X, Is.EqualTo(tail.BorderBox.X));
        Assert.That(bands[1].BorderBox.Right, Is.EqualTo(tail.BorderBox.Right + 6));
        Assert.That(bands[1].Padding.Left, Is.EqualTo(0), "a cut edge carries no padding");
    }

    [Test]
    public async Task ABrokenElementKeepsOnlyTheCornersItEndsWith()
    {
        using var view = CreateView(40);

        await view.SetHtml(Document("aaaa <span style='border-radius:2px'>abcd efgh</span>", width: 40));

        var bands = Inlines(view, "span").ToList();

        Assert.That(bands[0].Radii.TopLeft.IsZero, Is.False, "the line the element begins on draws its start corners");
        Assert.That(bands[0].Radii.BottomLeft.IsZero, Is.False);
        Assert.That(bands[0].Radii.TopRight.IsZero, Is.True, "the corners on the cut edge were drawn on the other line");
        Assert.That(bands[0].Radii.BottomRight.IsZero, Is.True);

        Assert.That(bands[1].Radii.TopLeft.IsZero, Is.True);
        Assert.That(bands[1].Radii.BottomLeft.IsZero, Is.True);
        Assert.That(bands[1].Radii.TopRight.IsZero, Is.False, "the line the element ends on draws its end corners");
        Assert.That(bands[1].Radii.BottomRight.IsZero, Is.False);
    }

    [Test]
    public async Task ThePaddingAheadOfARunCountsTowardsTheLine()
    {
        using var view = CreateView();

        // Twenty-five pixels of run and its space, sixteen of padding, and a line only forty wide:
        // together they do not fit, so the run has to wait for the next line rather than having its
        // band reach back over the text in front of it. The space is what offers the break: two
        // letters meeting across the span's edge are one word (measured in Chromium).
        await view.SetHtml(Document("aaaa <span style='padding-left:16px'>bb</span>", width: 40));

        Assert.That(Lines(view).Count(), Is.EqualTo(2));

        var band = Inline(view, "span");
        Assert.That(band.BorderBox.X, Is.EqualTo(0));
        Assert.That(Text(view, "bb").BorderBox.X, Is.EqualTo(16));
    }

    [Test]
    public async Task ASpaceTheLineDroppedIsNotCoveredByTheBand()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<span style='padding-right:10px'>ab </span>"));

        var run = Text(view, "ab");

        Assert.That(
            Inline(view, "span").BorderBox.Right,
            Is.EqualTo(run.BorderBox.Right + 10),
            "the end edge follows the last run the line kept, not the space it took off");
    }

    [Test]
    public async Task ANestedElementIsBoxedInsideTheOneAroundIt()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            "<mark style='background:yellow'><code style='background:#eeeeee'>ab</code></mark>"));

        var mark = Inline(view, "mark");
        var code = Inline(view, "code");

        Assert.That(mark.Children, Does.Contain(code), "the inner element is boxed inside the outer one");
        Assert.That(code.Children.Single().Text, Is.EqualTo("ab"));
        Assert.That(mark.BorderBox.Width, Is.EqualTo(code.BorderBox.Width), "with no padding the two are the same band");

        // The outer background is emitted first, so the inner one lands on top of it.
        var fills = Fills(view).ToList();

        Assert.That(fills.Count, Is.EqualTo(2));
        Assert.That(fills[0].Color, Is.EqualTo(0xFFFFFF00));
        Assert.That(fills[1].Color, Is.EqualTo(0xFFEEEEEE));
    }

    [Test]
    public async Task AColumnPutsThePaddingOnTheSidesTheWritingModeDoes()
    {
        using var across = CreateView();
        using var along = CreateView();

        // Padding is a physical shorthand, so in a column these two add it to opposite axes of the
        // band: the first across the column, the second along it.
        await across.SetHtml(Column("<span style='padding:0 4px'>ab</span>"));
        await along.SetHtml(Column("<span style='padding:4px 0'>ab</span>"));

        var acrossBand = Inline(across, "span");
        var acrossRun = Text(across, "ab");

        Assert.That(acrossBand.BorderBox.Width, Is.EqualTo(acrossRun.BorderBox.Width + 8));
        Assert.That(acrossBand.BorderBox.Height, Is.EqualTo(acrossRun.BorderBox.Height));

        var alongBand = Inline(along, "span");
        var alongRun = Text(along, "ab");

        Assert.That(alongBand.BorderBox.Width, Is.EqualTo(alongRun.BorderBox.Width));
        Assert.That(alongBand.BorderBox.Height, Is.EqualTo(alongRun.BorderBox.Height + 8));
    }

    [Test]
    public async Task ANoBreakSpaceAtTheStartOfALineIsNotSkipped()
    {
        using var view = CreateView();

        // CSS Text 3 §4 puts white space processing on the document white space characters and
        // nothing else: the space, the tab and the segment breaks. A no-break space is an "other
        // space separator" - never collapsed into a neighbour, never trimmed off either end of a
        // line and never a place a line may break, which is the whole point of writing one. The
        // line's own rules skip the white space a line begins with; a no-break space is content
        // that stands there, and a line that drops it for being white space loses the room it
        // takes - which is how five of them beside a float came to fit where they do not.
        await view.SetHtml(Document("\u00a0ab"));

        Assert.That(
            Text(view, "\u00a0ab").BorderBox.Width,
            Is.EqualTo(15),
            "the no-break space the line starts with keeps its advance");
    }

    [Test]
    public async Task ANoBreakSpaceAtTheEndOfARunIsNotCutFromItsWidth()
    {
        using var view = CreateView();

        // The same rule on the other side of measuring: a run that shrinks to fit its content
        // holds back the collapsible white space at its end, and a no-break space is not that -
        // it is part of the content the box is sized by.
        await view.SetHtml(Document("<span id='run' style='display:inline-block'>ab\u00a0</span>"));

        var run = view.Document!.GetElementById("run")!;
        var bounds = view.GetElementBounds(run);

        Assert.That(bounds, Is.Not.Null, "the run generated no box to measure");
        Assert.That(
            bounds!.Value.Width,
            Is.EqualTo(15),
            "the no-break space the run ends with is part of what it is sized by");
    }

    /// <summary>
    /// The place a box the line carries whole takes on it is settled by the alignment of the block the
    /// line belongs to: a <c>text-align</c> written on the inline-block speaks for the text inside that
    /// box, not for where the box itself stands (CSS Text 3 §6.2, CSS Display 3 §4).
    /// </summary>
    [Test]
    public async Task AnInlineBlockTakesItsPlaceFromTheBlocksOwnAlignment()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:2'>" +
            "<span id='aligned' style='display:inline-block;width:20px;height:10px;text-align:end'>b</span>" +
            "<span id='plain' style='display:inline-block;width:20px;height:10px'>a</span>" +
            "</p></body></html>");

        var first = view.GetElementBounds(view.Document!.GetElementById("aligned")!)!.Value;
        var second = view.GetElementBounds(view.Document!.GetElementById("plain")!)!.Value;

        Assert.That(first.X, Is.EqualTo(0), "the block aligns its line to its start, so the box begins there");
        Assert.That(second.X, Is.EqualTo(first.Right), "and the box beside it stands beside it");
    }

    [Test]
    public async Task ANoBreakSpaceIsNotAPlaceALineMayBreak()
    {
        using var view = CreateView();

        // A soft wrap opportunity is what white space gives a line, and a no-break space gives
        // none: the run either fits or it overflows whole, and it does not come apart at the
        // space that was written to hold together.
        await view.SetHtml(Document("aaaa\u00a0bbbb", width: 25));

        Assert.That(Lines(view).Count(), Is.EqualTo(1), "the run does not break at the no-break space");
        Assert.That(
            Text(view, "aaaa\u00a0bbbb").BorderBox.Width,
            Is.EqualTo(45),
            "and it overflows the paragraph rather than wrapping");
    }

    [Test]
    public async Task ALineTheBreakEndsStillTakesItsLineHeight()
    {
        using var view = CreateView();

        // CSS 2.1 §9.4.2 leaves a line box that ends with a forced break standing even when nothing
        // is on it: the two breaks of "a<br><br>b" end a line each, and the one between them is a
        // blank line of the line height. Treating it as zero-height would pull the line after it up.
        await view.SetHtml(Document("a<br><br>b"));

        var paragraph = Walk(view).Single(node => node.Kind == LayoutKind.Block && Name(node) == "p");

        Assert.That(Text(view, "b").BorderBox.Y, Is.EqualTo(40), "the text after the blank line stands two lines down");
        Assert.That(paragraph.BorderBox.Height, Is.EqualTo(60), "and the paragraph holds the blank line's room");
    }

    [Test]
    public async Task AnEmptyColumnTakesItsLineHeightAsWell()
    {
        using var view = CreateView();

        await view.SetHtml(Column("a<br><br>b"));

        Assert.That(
            Text(view, "b").BorderBox.X,
            Is.EqualTo(Text(view, "a").BorderBox.X - 20),
            "the text after the blank column stands two columns along");
    }

    [Test]
    public async Task ALineEndsAtASoftHyphenAndDrawsTheMark()
    {
        using var view = CreateView();

        // A soft hyphen is a place a line may end and nothing else: in a box too narrow for the whole
        // word the line ends after it and the mark stands at the break, taking the room the next
        // character does not (CSS Text 3 §6.1, §6.2). The fake measurer gives every character five
        // pixels, so "Hy-" is fifteen and the box at sixteen holds it.
        await view.SetHtml(Document("Hy\u00ADphen.", width: 16));

        Assert.That(Lines(view).Count(), Is.EqualTo(2), "the word is set on two lines");
        Assert.That(Text(view, "Hy-").BorderBox.Width, Is.EqualTo(15), "the mark stands where the word was cut");
        Assert.That(Text(view, "phen.").BorderBox.Width, Is.EqualTo(25), "and the rest follows on the next line");
    }

    [Test]
    public async Task TheMarkIsTheCharacterThePageNamed()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<span style='hyphenate-character:\"+\"'>Hy\u00ADphen.</span>", width: 16));

        Assert.That(Text(view, "Hy+").BorderBox.Width, Is.EqualTo(15), "the break draws the character the element named");
    }

    [Test]
    public async Task ConsecutiveSoftHyphensDrawOneMark()
    {
        using var view = CreateView();

        // Two soft hyphens stand for one place the line may end: the break draws a single mark, and the
        // second hyphen neither draws nor opens a line of its own (soft-hyphens-1b).
        await view.SetHtml(Document("Hy\u00AD\u00ADphen.", width: 16));

        Assert.That(Lines(view).Count(), Is.EqualTo(2), "the word is set on two lines");
        Assert.That(Text(view, "Hy-").BorderBox.Width, Is.EqualTo(15), "with one mark where it was cut");
        Assert.That(Text(view, "phen.").BorderBox.Width, Is.EqualTo(25), "and no line of the mark alone");
    }

    [Test]
    public async Task ASoftHyphenThatHeadsAnotherBoxStillEndsTheWord()
    {
        using var view = CreateView();

        // The word began in the span and the soft hyphen heads the text node after it: LB20a asks
        // whether the hyphen opens a word, and this one stands inside the one the span began. The mark
        // is a run of its own here, because it comes from the text node the hyphen was written in.
        await view.SetHtml(Document("<span>Hy</span>\u00ADphen.", width: 16));

        Assert.That(Lines(view).Count(), Is.EqualTo(2), "the line ends where the hyphen stands");
        Assert.That(Text(view, "-").BorderBox.X, Is.EqualTo(10), "the mark follows the letters the span set");
        Assert.That(Text(view, "phen.").BorderBox.Y, Is.EqualTo(20), "and the rest follows on the next line");
    }

    [Test]
    public async Task AClampedColumnKeepsOnlyTheLinesItAsksFor()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            "<p style='-webkit-line-clamp:2;-webkit-box-orient:vertical;white-space:pre;line-height:10px'>a\nb\nc</p>"));

        Assert.That(Lines(view).Count(), Is.EqualTo(2), "the third line is not laid out");
        Assert.That(Text(view, "\u2026").BorderBox.Y, Is.EqualTo(Lines(view).Last().BorderBox.Y), "and the mark ends the second line");
    }

    [Test]
    public async Task ALineOfAClippedBlockEndsInTheEllipsis()
    {
        using var view = CreateView();

        // The block is twenty pixels wide and every character of the fake font is five, so three
        // characters and the ellipsis fill it; the rest of the word is not drawn at all.
        await view.SetHtml(Document(
            "<p style='width:20px;overflow:hidden;white-space:nowrap;text-overflow:ellipsis'>abcdef</p>"));

        Assert.That(Text(view, "abc").BorderBox.Width, Is.EqualTo(15), "the characters the mark left room for");
        Assert.That(Text(view, "…").BorderBox.X, Is.EqualTo(15), "the mark stands where the line was cut");
        Assert.That(Walk(view).Any(node => node.Kind == LayoutKind.Text && node.Text == "def"), Is.False,
            "and nothing after it is drawn");
    }

    [Test]
    public async Task ASoftHyphenCarriesAcrossABoxThatLeftTheFlow()
    {
        using var view = CreateView();

        // The box the page took out of the flow holds none of the line's text, so the word the span
        // began is still standing when the soft hyphen is met, and the mark ends the line above
        // (CSS Text 3 §6.1, and hyphens-out-of-flow-001).
        await view.SetHtml(
            Document("high<span style='position:absolute'>i</span>­way", width: 30));

        Assert.That(Text(view, "-").BorderBox.X, Is.EqualTo(20), "the mark ends the line the word was cut on");
        Assert.That(Text(view, "way").BorderBox.Y, Is.GreaterThan(0), "and the rest begins the next line");
    }

    [Test]
    public async Task ASoftHyphenBeforeABoxInTheWordKeepsItsBreak()
    {
        using var view = CreateView();

        // The room takes "high-wa" but not the last letter, and the letter stands after a box that
        // left the flow: the line ends at the hyphen the word was written with, and the rest of the
        // word - the box included - begins the next line (hyphens-out-of-flow-001).
        await view.SetHtml(Document(
            "high­wa<span style='position:absolute'>i</span>y", width: 32));

        Assert.That(Text(view, "high-").BorderBox.Width, Is.EqualTo(25), "the mark stands where the word was cut");
        Assert.That(Text(view, "wa").BorderBox.Y, Is.GreaterThan(0), "the rest of the word begins the next line");
        Assert.That(Text(view, "y").BorderBox.Y, Is.GreaterThan(0), "with the box that stood inside it");
    }

    [Test]
    public async Task ASoftHyphenIsUsedWhenTheRestOfTheWordWouldNotFit()
    {
        using var view = CreateView();

        // The word began in the span and continues in the text node after it. "high\u00ADwa" fits the
        // line, but its last letter has nowhere to go and no break stands before it: the line ends at
        // the soft hyphen and "wa" and "y" begin the next one (CSS Text 3 §6.1).
        await view.SetHtml(Document("<span>high\u00ADwa</span>y", width: 37));

        Assert.That(Lines(view).Count(), Is.EqualTo(2), "the line ends at the hyphen");
        Assert.That(Text(view, "high-").BorderBox.Width, Is.EqualTo(25), "with the mark drawn there");
        Assert.That(Text(view, "wa").BorderBox.X, Is.EqualTo(0), "and the syllable after it begins the next line");
        Assert.That(Text(view, "y").BorderBox.Y, Is.EqualTo(20), "with the letter that ended the word");
    }

    [Test]
    public async Task ASoftHyphenTheLineDoesNotEndAtIsInvisible()
    {
        using var view = CreateView();

        // Nothing is drawn and nothing is charged where the line runs on across the hyphen: the word
        // is the word the page would have written without it.
        await view.SetHtml(Document("Hy\u00ADphen.", width: 60));

        Assert.That(Lines(view).Count(), Is.EqualTo(1), "the whole word fits");
        Assert.That(Text(view, "Hy\u00ADphen.").BorderBox.Width, Is.EqualTo(40), "and the hyphen takes no room in it");
    }

    /// <summary>
    /// Gets the document the inline box is asserted in: a paragraph with no margin of its own, set in a
    /// font the fake measurer gives a ten-pixel content area whatever the size.
    /// </summary>
    [Test]
    public async Task AControlCharacterIsContentRatherThanSpace([Values('\u000C', '\u000B')] Char control)
    {
        using var view = CreateView();

        // CSS Text 3 §4.2 asks that a control character other than the tab, the line feed and the
        // carriage return be drawn as a visible glyph and otherwise treated as an ordinary character.
        // A form feed between two letters is therefore a character of the run - it keeps its advance,
        // and the line does not come apart there - rather than a space the collapse would eat.
        var text = "a" + control + "b";

        await view.SetHtml(Document(text, width: 10));

        Assert.That(
            Text(view, text).BorderBox.Width,
            Is.EqualTo(15),
            "the control character is measured as the third character of the run");
        Assert.That(
            Lines(view).Count(),
            Is.EqualTo(1),
            "and the line does not break at it");
    }

    [Test]
    public async Task AControlCharacterCollapsesNeitherWithItsNeighboursNorAway()
    {
        using var view = CreateView();

        // A space and a control character side by side are two different things: the space is the one
        // the line may drop at its end, and the control character stands where it was written.
        var text = "a \u000C b";

        await view.SetHtml(Document("<span id='run' style='display:inline-block'>" + text + "</span>"));

        var run = view.Document!.GetElementById("run")!;

        Assert.That(
            view.GetElementBounds(run)!.Value.Width,
            Is.EqualTo(25),
            "the two spaces collapse to one and the control character keeps its own room");
    }

    [Test]
    public async Task ALatinLineTurnedRoundByAnOverrideIsDrawnFromTheFarEnd()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            "<span style='unicode-bidi:bidi-override;direction:rtl'>abc</span>"));

        var command = view.Paint!.Commands.OfType<TextCommand>().Single(run => run.Text == "abc");

        Assert.That(command.Reversed, Is.True,
            "the override sets the Latin letters at a right-to-left level, and the shaper would read them the other way");
    }

    [Test]
    public async Task AHebrewLineAnOverrideKeepsLeftToRightIsDrawnFromTheFarEnd()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            "<span dir='rtl' style='unicode-bidi:bidi-override;direction:ltr'>\u05D0\u05D1\u05D2</span>"));

        var command = view.Paint!.Commands.OfType<TextCommand>().Single(run => run.Text == "\u05D0\u05D1\u05D2");

        Assert.That(command.Reversed, Is.True,
            "the override keeps the Hebrew letters at a left-to-right level, and the shaper would read them right to left");
    }

    [Test]
    public async Task APlainRightToLeftLineIsLeftToTheShaper()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<span dir='rtl'>\u05D0\u05D1\u05D2</span>"));

        var command = view.Paint!.Commands.OfType<TextCommand>().Single(run => run.Text == "\u05D0\u05D1\u05D2");

        Assert.That(command.Reversed, Is.False, "a Hebrew run reads right to left of its own accord");
    }

    [Test]
    public async Task AnInlineBlockWithABlockChildStandsOnItsLastLinesBaseline()
    {
        using var view = CreateView();

        // The block child carries the line the box stands on, and that line is three levels down: the
        // inline-block's baseline is the child's text, not the bottom edge the box happens to reach
        // (CSS 2.1 §10.8.1). The twenty-pixel child still reaches past the ten-pixel line, so only the
        // baseline tells the two answers apart: the line stays at the font's eight.
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='font-size:10px;line-height:1'>a<span id='box' style='display:inline-block'>" +
            "<div style='height:20px;font-size:10px;line-height:10px'>b</div></span></div></body></html>");

        var box = view.Document!.GetElementById("box")!;

        Assert.That(Lines(view).First().Baseline, Is.EqualTo(8), "the line reaches only the child's text");
        Assert.That(view.GetElementBounds(box)!.Value.Y, Is.EqualTo(0), "and the box stands on that baseline");
    }

    [Test]
    public async Task AnInlineBlockThatClipsItsOverflowStandsOnItsBottomEdge()
    {
        using var view = CreateView();

        // A box that hides what leaves it has no line of its own to stand on: §10.8.1 sends it to the
        // bottom margin edge, so the text beside it keeps the line's own eight-pixel baseline.
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='font-size:10px;line-height:1'>a<span id='box' style='display:inline-block;overflow:hidden'>" +
            "<div style='height:20px;font-size:10px;line-height:10px'>b</div></span></div></body></html>");

        var box = view.Document!.GetElementById("box")!;
        var bounds = view.GetElementBounds(box)!.Value;

        Assert.That(Lines(view).First().Baseline, Is.EqualTo(20), "the box's bottom edge is its baseline");
        Assert.That(bounds.Y + bounds.Height, Is.EqualTo(20), "and that edge sits on the line's baseline");
    }

    private static String Document(String content, String lineHeight = "2", Int32 width = 0) =>
        "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:" + lineHeight +
        (width > 0 ? ";width:" + width + "px" : String.Empty) +
        "'>" + content + "</p></body></html>";

    /// <summary>Gets the same content in a column two hundred pixels long.</summary>
    private static String Column(String content, String attributes = "") =>
        "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:1;" +
        "writing-mode:vertical-rl;height:200px" + attributes + "'>" + content + "</p></body></html>";

    [Test]
    public async Task ALatinRunInARightToLeftColumnTurnsToTheNearEnd()
    {
        using var view = CreateView();

        // A column of a right-to-left page reads upwards, but the Latin run on it keeps the order its
        // own letters ask for, so the first of them stands at the top of the room the run was given.
        await view.SetHtml(Column("Xppp", ";direction:rtl"));

        var command = view.Paint!.Commands.OfType<TextCommand>().Single(run => run.Text == "Xppp");

        Assert.That(command.Reversed, Is.False,
            "the line is right to left but the run is set at an even level, which is read top to bottom");
    }

    [Test]
    public async Task AHebrewRunInARightToLeftColumnIsLeftToTheShaper()
    {
        using var view = CreateView();

        await view.SetHtml(Column("\u05D0\u05D1\u05D2", ";direction:rtl"));

        var command = view.Paint!.Commands.OfType<TextCommand>().Single(run => run.Text == "\u05D0\u05D1\u05D2");

        Assert.That(command.Reversed, Is.False, "a Hebrew run reads upward of its own accord");
    }

    [Test]
    public async Task ABdiReadsItsOwnTextAndIsolatesIt()
    {
        using var view = CreateView(300);

        // A hundred-pixel block in a right-to-left page stands against the right edge; its own text
        // reads left to right because the bdi's direction is its content's, not the page's (HTML §4.5.4,
        // "the dir attribute defaults to auto on this element").
        await view.SetHtml(
            "<html><body style='margin:0;direction:rtl'>" +
            "<bdi style='display:block;width:100px'>abc</bdi></body></html>");

        Assert.That(
            Text(view, "abc").BorderBox.X,
            Is.EqualTo(200),
            "the bdi's text starts at its box's left edge, where a left-to-right line begins");
    }

    private static HutuViewInternal CreateView(Int32 viewportWidth = 600)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static IEnumerable<FillCommand> Fills(HutuViewInternal view) =>
        view.Paint!.Commands.OfType<FillCommand>();

    private static IEnumerable<BorderCommand> Borders(HutuViewInternal view) =>
        view.Paint!.Commands.OfType<BorderCommand>();

    /// <summary>Finds the one box an inline element of the given name was given.</summary>
    private static LayoutBox Inline(HutuViewInternal view, String tag) =>
        Inlines(view, tag).Single();

    private static IEnumerable<LayoutBox> Inlines(HutuViewInternal view, String tag) =>
        Walk(view)
            .Where(node => node.Kind == LayoutKind.Inline && Name(node) == tag);

    /// <summary>Finds the one text box drawn from the given characters, which is how a run is picked out.</summary>
    private static LayoutBox Text(HutuViewInternal view, String text) =>
        Walk(view).SingleOrDefault(node => node.Kind == LayoutKind.Text && node.Text == text)
            ?? throw new AssertionException($"The layout holds no text box for '{text}'.");

    private static IEnumerable<LayoutBox> Lines(HutuViewInternal view) =>
        Walk(view).Where(node => node.Kind == LayoutKind.Line);

    private static String? Name(LayoutBox box) => box.Source.Element?.LocalName;

    private static IEnumerable<LayoutBox> Walk(HutuViewInternal view)
    {
        var root = view.Layout?.Root;

        return root is null ? Enumerable.Empty<LayoutBox>() : Walk(root);
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

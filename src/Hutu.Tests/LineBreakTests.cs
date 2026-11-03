using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the places a line may come apart that are neither a space nor an ideograph: the hyphens that
/// end a line, the marks that may not begin or end one, the numbers a line may not come apart, and the
/// joiners and zero-width characters that decide for themselves.
/// </summary>
/// <remarks>
/// A case is read off the chunks the line is built out of, because a chunk is exactly the piece a line
/// may not be taken apart in - so the list of chunks is the list of the places a break is allowed. The
/// marks are written as numbers, since the characters a test of line breaking is made of are the ones
/// no editor shows faithfully.
/// </remarks>
[TestFixture]
public sealed class LineBreakTests
{
    [Test]
    public void ALineEndsAfterAHyphen()
    {
        // LB21 lets a line end after each of these and LB13 keeps the mark on the line it ended, so the
        // three characters before it - the word and the hyphen - are one chunk and what follows is the
        // next.
        foreach (var hyphen in new[] { '-', (Char)0x00AD, (Char)0x2010, (Char)0x2013 })
        {
            var text = "ab" + hyphen + "cd";

            Assert.That(
                Chunks(text),
                Is.EqualTo(new[] { text[..3], text[3..] }),
                "the hyphen ends the line it stands at: " + (Int32)hyphen);
        }
    }

    [Test]
    public void ALineDoesNotBeginWithAHyphenOrEndAfterTheOneThatOpensAWord()
    {
        Assert.That(Chunks("ab-cd"), Is.EqualTo(new[] { "ab-", "cd" }));
        Assert.That(Chunks("-cd"), Is.EqualTo(new[] { "-cd" }), "LB20a: a leading hyphen is a minus sign");
        Assert.That(Chunks("ab -cd"), Is.EqualTo(new[] { "ab", " ", "-cd" }));
    }

    [Test]
    public void ANonBreakingHyphenIsWrittenOntoTheWordItJoins()
    {
        // U+2011 is glue, not a hyphen the line may use (LB12).
        Assert.That(Chunks("ab" + (Char)0x2011 + "cd"), Has.Count.EqualTo(1));
    }

    [Test]
    public void ALineEndsOnEitherSideOfAnEmDash()
    {
        var dash = ((Char)0x2014).ToString();

        // The em dash is the one mark the annex lets a line come apart in front of as well as after, so
        // it stands in a piece of its own (B2, LB31).
        Assert.That(Chunks("ab" + dash + "cd"), Is.EqualTo(new[] { "ab", dash, "cd" }));

        // Two of them together are the mark written twice, and a line may not come apart between the two
        // halves of it (LB17).
        Assert.That(Chunks("ab" + dash + dash + "cd"), Is.EqualTo(new[] { "ab", dash + dash, "cd" }));
    }

    [Test]
    public void AZeroWidthSpaceEndsALineAndDoesNotBeginOne()
    {
        var zwsp = ((Char)0x200B).ToString();

        Assert.That(Chunks("ab" + zwsp + "cd"), Is.EqualTo(new[] { "ab" + zwsp, "cd" }), "LB8");
        Assert.That(
            Chunks("ab" + zwsp + zwsp + "cd"),
            Is.EqualTo(new[] { "ab" + zwsp + zwsp, "cd" }),
            "LB7: two of them are still one place to end");
    }

    [Test]
    [TestCase((Char)0x00A0)]
    [TestCase((Char)0x2060)]
    [TestCase((Char)0xFEFF)]
    public void AJoinerHoldsTheWordsItStandsBetween(Char mark)
    {
        // The no-break space, the word joiner and the zero width no-break space forbid a break on either
        // side of themselves (LB11, LB12), which is the whole point of writing one.
        Assert.That(Chunks("ab" + mark + "cd"), Has.Count.EqualTo(1), "no break beside " + (Int32)mark);
    }

    [Test]
    public void ANumberIsOneThing()
    {
        Assert.That(Chunks("12.34"), Has.Count.EqualTo(1), "the decimal mark is written between digits");
        Assert.That(Chunks("12,340"), Has.Count.EqualTo(1), "so is the thousands separator");
        Assert.That(Chunks("5%"), Has.Count.EqualTo(1), "and the count stands on what it counts");
        Assert.That(Chunks("$5"), Has.Count.EqualTo(1), "and the price on what it prices");
        Assert.That(Chunks("(1,000)"), Has.Count.EqualTo(1), "and a bracketed number is one number");
        Assert.That(
            Chunks("12.34 ab"),
            Is.EqualTo(new[] { "12.34", " ", "ab" }),
            "the space after a number is still a place to end");
    }

    [Test]
    public void AMarkBetweenLettersGluesThem()
    {
        Assert.That(Chunks("e.g."), Has.Count.EqualTo(1), "LB29: the abbreviation is one word");
        Assert.That(Chunks("per(s)on"), Has.Count.EqualTo(1), "LB30: the bracket inside a word is in it");
        Assert.That(Chunks("ab" + (Char)0x00B7 + "cd"), Has.Count.EqualTo(1), "a middle dot is a letter");
        Assert.That(Chunks("ab" + (Char)0x2019 + "cd"), Has.Count.EqualTo(1), "and so is a quote");
    }

    [Test]
    public void AClosingMarkDoesNotBeginALine()
    {
        // The ideographic full stop is kinsoku: a line may not begin with it, so it stands on the line of
        // the character it closes (LB13).
        Assert.That(
            Chunks("漢字。漢字"),
            Is.EqualTo(new[] { "漢", "字。", "漢", "字" }),
            "the stop is glued to the ideograph before it and not to the one after");
    }

    [Test]
    public void AnOpeningMarkDoesNotEndALine()
    {
        Assert.That(
            Chunks("漢「漢字"),
            Is.EqualTo(new[] { "漢", "「漢", "字" }),
            "the quotation opens the line it stands at the head of (LB14)");
    }

    [Test]
    public void AnIterationMarkStandsOnTheCharacterItRepeats()
    {
        var again = ((Char)0x3005).ToString();

        Assert.That(
            Chunks("漢字" + again + again + "漢"),
            Is.EqualTo(new[] { "漢", "字" + again + again, "漢" }),
            "a non-starter is not a place to begin, and the line it ends may be a Latin one (LB21)");
    }

    [Test]
    public void ACombiningMarkIsItsBaseCharacter()
    {
        Assert.That(Chunks("abe" + (Char)0x0301 + "cd"), Has.Count.EqualTo(1), "LB9");
        Assert.That(Chunks("abe" + (Char)0x0301 + "漢"), Has.Count.EqualTo(2), "and the line ends after the pair");
        Assert.That(Chunks("ab" + (Char)0x200D + "cd"), Has.Count.EqualTo(1), "LB8a: a joiner holds the pair");
    }

    [Test]
    [TestCase((Char)0x0085)]
    [TestCase((Char)0x2028)]
    [TestCase((Char)0x2029)]
    public void ASegmentBreakFromTheDocumentEndsALine(Char mark)
    {
        // Chromium, measured, breaks the line after each of these three and does not force one: they are
        // the document's own segment breaks, and this class only has to agree with that.
        Assert.That(
            Chunks("ab" + mark + "cd"),
            Is.EqualTo(new[] { "ab" + mark, "cd" }),
            "no break before, break after " + (Int32)mark);
    }

    [Test]
    public void AnIdeographicSpaceEndsALineWithoutBeingCollapsible()
    {
        var space = ((Char)0x3000).ToString();

        Assert.That(Chunks("ab" + space + "cd"), Is.EqualTo(new[] { "ab" + space, "cd" }));
        Assert.That(TextChunker.IsCollapsible((Char)0x3000), Is.False, "and it is never collapsed away");
    }

    [Test]
    [TestCase((Char)0x000B)]
    [TestCase((Char)0x000C)]
    public void AControlCharacterIsContentBetweenTwoWords(Char mark)
    {
        // CSS Text 3 §4.2 draws a control character as a glyph and otherwise treats it as an ordinary
        // character, which makes it neither a place to break nor a space to collapse (LB9).
        Assert.That(Chunks("ab" + mark + "cd"), Has.Count.EqualTo(1), "no break beside " + (Int32)mark);
    }

    [Test]
    public void ASolidusIsAPlaceToEndALineAfter()
    {
        // The annex gives SOLIDUS a break after it and none before (LB13, then LB31). Chromium measured
        // does not take the one after: http://example.com/verylongpath stands whole on a line that would
        // have room to end it. The rule is what is implemented here.
        Assert.That(Chunks("ab/cd"), Is.EqualTo(new[] { "ab/", "cd" }));
    }

    [Test]
    public async Task ALineEndsWhereAHyphenSaysSo()
    {
        using var view = CreateView();

        // Five characters of the measurer's five pixels each, in room for two: the only place the line may
        // come apart is after the hyphen, and the hyphen is drawn with the line it ended.
        await view.SetHtml(Page("width:10px", "ab-cd"));

        Assert.That(Lines(view).Count(), Is.EqualTo(2), "the hyphen ends the line it stands at");
        Assert.That(Text(view, "ab-").BorderBox.Width, Is.EqualTo(15), "and is drawn on the line it ended");
    }

    [Test]
    public async Task ALineDoesNotBeginWithAnIdeographicStop()
    {
        using var view = CreateView();

        // Two ideographs and the stop between them fill the fifteen pixels given, so the line has to come
        // apart after the stop rather than in front of it.
        await view.SetHtml(Page("width:15px", "漢字。漢字"));

        Assert.That(
            Runs(view),
            Is.EqualTo(new[] { "漢字。", "漢字" }),
            "the stop stands on the line before it, and the second pair comes after");
    }

    [Test]
    public void ALineThatBeginsWithAClosingMarkStillKnowsWhereItStands()
    {
        // The number test walks back over the marks a number is written between, and a piece of text
        // that opens with a closing mark - the emphasis of a page, the stop a line was broken in front
        // of - has nothing behind it to walk back to.
        Assert.That(
            Chunks("」漢字"),
            Is.EqualTo(new[] { "」", "漢", "字" }),
            "a piece of text that opens on a closing mark has nothing behind it to ask about");
        Assert.That(Chunks("。"), Is.EqualTo(new[] { "。" }), "and one of them alone is one piece");
    }

    private static String Page(String style, String content) =>
        "<html><body style='margin:0'><div style='margin:0;font-size:10px;line-height:1;" + style +
        "'>" + content + "</div></body></html>";

    private static HutuViewInternal CreateView(Int32 viewportWidth = 600)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    [Test]
    public void AnywhereEndsALineBetweenUnitsTheGlueWouldOtherwiseHold()
    {
        // CSS Text 3 §5.3: `line-break: anywhere` sets the Unicode rules aside, so the glue class of a
        // non-breaking hyphen no longer holds the line together - but a mark still keeps to its base,
        // because the boundary inside a character unit is not one a line can end at.
        Assert.That(
            Chunks("XX‑X", LineBreaks.WordBreaking.Anywhere),
            Is.EqualTo(new[] { "X", "X", "‑", "X" }),
            "every character unit boundary is an opportunity");

        Assert.That(
            Chunks("X́X", LineBreaks.WordBreaking.Anywhere),
            Is.EqualTo(new[] { "X́", "X" }),
            "a mark does not begin a line");

        Assert.That(
            LineBreaks.MayBreak('X', 0x2011, LineBreaks.WordBreaking.Anywhere),
            Is.True,
            "the pair across an inline edge is an opportunity too");
    }

    private static List<String> Chunks(String text) => TextChunker.Chunks(text).ToList();

    [Test]
    public void AScriptNeedingAnalysisFallsBackToBreakingBetweenItsLetters()
    {
        // Word boundaries in these scripts are not written, and an engine without the analysis to find
        // them is not allowed to overflow: it must assume an opportunity between each pair of typographic
        // letter units (CSS Text 3 §5.1). The marks of the same scripts stay on the letter they stand
        // over, under or beside, which is why the vowel here opens no place to break.
        var text = "\u0E21\u0E19\u0E38\u0E0A\u0E22";

        Assert.That(
            Chunks(text, LineBreaks.WordBreaking.Normal),
            Is.EqualTo(new[] { "\u0E21", "\u0E19\u0E38", "\u0E0A", "\u0E22" }),
            "the line may end between the letters, and not inside one");

        // `keep-all` asks for that licence to be withheld: only a space, or a break the page wrote,
        // comes between the letters then (§5.4).
        Assert.That(
            Chunks(text, LineBreaks.WordBreaking.KeepAll),
            Is.EqualTo(new[] { text }),
            "nothing but the marks' own rule holds the run together");
    }

    private static List<String> Chunks(String text, LineBreaks.WordBreaking word)
    {
        var chunks = new List<String>();

        foreach (var range in TextChunker.Ranges(text, word))
        {
            chunks.Add(text.Substring(range.Start, range.Length));
        }

        return chunks;
    }

    private static LayoutBox Text(HutuViewInternal view, String content) =>
        Walk(view).SingleOrDefault(n => n.Kind == LayoutKind.Text && n.Text == content)
            ?? throw new AssertionException($"The layout holds no text box for '{content}'.");

    private static List<String?> Runs(HutuViewInternal view) =>
        Walk(view).Where(n => n.Kind == LayoutKind.Text).Select(n => n.Text).ToList();

    private static IEnumerable<LayoutBox> Lines(HutuViewInternal view) =>
        Walk(view).Where(n => n.Kind == LayoutKind.Line);

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

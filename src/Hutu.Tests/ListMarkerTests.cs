using Hutu.Rendering;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers which item a list marks, and with what: the type that reaches an item from its list, the
/// counting a list does over its items, and the attributes that move that count about.
/// </summary>
/// <remarks>
/// These speak of the box tree rather than of pixels, because a marker is decided while the tree is
/// built: which item an item is has nothing to do with the containing block, and the painting that draws
/// the marker is covered where geometry is.
/// </remarks>
[TestFixture]
public sealed class ListMarkerTests
{
    [Test]
    public async Task AListMarksItsItemsWithTheTypeItsListAskedFor()
    {
        var markers = await Markers("", "<ul><li>one</li><li>two</li></ul><ol><li>one</li><li>two</li></ol>");

        Assert.That(markers, Is.EqualTo(new[] { "• ", "• ", "1. ", "2. " }),
            "a list marks its items, and the user-agent sheet's disc and decimal reach them through inheritance");
    }

    [Test]
    public async Task AnItemMayAskForAMarkerOfItsOwn()
    {
        var markers = await Markers("li.riven { list-style-type: lower-roman }",
            "<ol><li>one</li><li class='riven'>two</li><li>three</li></ol>");

        Assert.That(markers, Is.EqualTo(new[] { "1. ", "ii. ", "3. " }),
            "the item's own statement wins over the one it inherited");
    }

    [Test]
    public async Task AListThatSaysNoneMarksNothing()
    {
        var markers = await Markers("", "<ul style='list-style-type:none'><li>one</li><li>two</li></ul>");

        Assert.That(markers, Is.EqualTo(new String?[] { null, null }));
    }

    [Test]
    public async Task StartAndValueMoveTheCount()
    {
        var fromStart = await Markers("", "<ol start='7'><li>one</li><li>two</li></ol>");
        var fromValue = await Markers("", "<ol><li value='9'>one</li><li>two</li></ol>");

        Assert.Multiple(() =>
        {
            Assert.That(fromStart, Is.EqualTo(new[] { "7. ", "8. " }), "a list can start anywhere");

            // An item that states its own number does not just wear it - the count goes on from there,
            // which is how a list numbered from a source other than its own order stays in step.
            Assert.That(fromValue, Is.EqualTo(new[] { "9. ", "10. " }), "and the count carries on after it");
        });
    }

    [Test]
    public async Task AReversedListCountsDownFromItsLength()
    {
        var markers = await Markers("", "<ol reversed><li>one</li><li>two</li><li>three</li></ol>");

        Assert.That(markers, Is.EqualTo(new[] { "3. ", "2. ", "1. " }),
            "with no start stated, a reversed list begins at how many items it holds");
    }

    [Test]
    public async Task AnItemThrownAwayIsNotCounted()
    {
        var markers = await Markers("",
            "<ol><li>one</li><li style='display:none'>two</li><li>three</li></ol>");

        Assert.That(markers, Is.EqualTo(new[] { "1. ", "2. " }),
            "display none leaves the tree, and the numbering behind it");
    }

    [Test]
    public async Task ANestedListCountsOnItsOwn()
    {
        var markers = await Markers("",
            "<ul><li>one<ul><li>deep</li></ul></li><li>two</li></ul>");

        Assert.That(markers, Is.EqualTo(new[] { "• ", "• ", "• " }),
            "the inner list is a list of one, and the outer one of two");
    }

    [Test]
    public async Task AListKeepsRoomAtTheEdgeItsItemsBeginAt()
    {
        var laid = await LaidOut("", "<ul><li>one</li></ul>");
        var item = Marked(laid.Root);

        Assert.Multiple(() =>
        {
            // The gutter is what the reader gives a list at the edge its items begin at, so the item
            // starts 40px in without having asked - and that space is where its marker hangs.
            Assert.That(item.ContentBox.X, Is.EqualTo(40).Within(0.5), "the list keeps 40px at its inline start");
            Assert.That(item.MarkerBounds!.Value.Right, Is.EqualTo(item.ContentBox.X).Within(0.5),
                "the marker ends where the item's content begins, rather than pushing into it");
        });
    }

    [Test]
    public async Task TheSpaceTheMarkerCarriesIsTheGapBetweenItAndTheText()
    {
        var laid = await LaidOut("", "<ul><li>one</li></ul>");
        var item = Marked(laid.Root);
        var marker = item.Source.Marker!;
        var measurer = new FakeTextMeasurer();
        var font = item.Source.Style.GetFont();
        var glyph = measurer.MeasureWidth(marker.TrimEnd(), font);
        var box = measurer.MeasureWidth(marker, font);

        // A browser leaves about a third of the font between a bullet and the text it stands in front
        // of, and it leaves that room because the marker's content carries a space - not because the
        // marker box is set further off. The box keeps its edge on the item, so the space is inside it:
        // a padding given to the item then moves the whole marker, gap and all, which is what a browser
        // does too.
        Assert.Multiple(() =>
        {
            Assert.That(marker, Does.EndWith(" "), "the marker carries the space that gaps it from the text");
            Assert.That(box, Is.GreaterThan(glyph), "and the space is measured, not imagined");
            Assert.That(item.MarkerBounds!.Value.Width, Is.EqualTo(box).Within(0.5),
                "the rectangle drawn is the whole marker, space included");
            Assert.That(item.MarkerBounds.Value.Right, Is.EqualTo(item.ContentBox.X).Within(0.5),
                "standing on the edge the item begins at, so the glyph ends one space short of its text");
        });
    }

    [Test]
    public async Task TheMarkerStandsOnTheFirstLineItsItemReaches()
    {
        var laid = await LaidOut("", "<ul><li>one</li></ul>");
        var item = Marked(laid.Root);
        var line = FirstLine(laid.Root);

        Assert.That(
            item.MarkerBounds!.Value.Y + item.MarkerBaseline,
            Is.EqualTo(line.BorderBox.Y + line.Baseline).Within(0.5),
            "a bullet floats in the gutter, level with the text it marks");
    }

    [Test]
    public async Task AnItemThatOpensWithAListIsMarkedLevelWithWhatTheReaderSeesFirst()
    {
        var laid = await LaidOut("", "<ul><li><ul><li>deep</li></ul></li></ul>");
        var outer = Marked(laid.Root, item => item.Marker == "• " && item.Children.Count > 0);

        // The outer item holds no text of its own, so the first line it reaches is the inner list's -
        // which is where a browser puts the bullet, rather than at the top of an empty line of its own.
        Assert.That(
            outer.MarkerBounds!.Value.Y + outer.MarkerBaseline,
            Is.EqualTo(FirstLine(laid.Root).BorderBox.Y + FirstLine(laid.Root).Baseline).Within(0.5));
    }

    [Test]
    public async Task APaintedListCarriesItsMarkersAsText()
    {
        var laid = await LaidOut("", "<ul><li>one</li><li>two</li></ul>");
        var marks = laid.Commands.OfType<TextCommand>().Where(c => c.Text == "• ").ToList();

        Assert.That(marks, Has.Count.EqualTo(2), "each item is marked, and nothing else is");
        Assert.That(
            marks[0].Bounds.Y + marks[0].Baseline,
            Is.EqualTo(FirstLine(laid.Root).BorderBox.Y + FirstLine(laid.Root).Baseline).Within(0.5),
            "and the command carries the same place the layout worked out");
    }

    [Test]
    public async Task AListWithoutMarkersKeepsTheRoomItWasGiven()
    {
        var laid = await LaidOut("", "<ul style='list-style-type:none'><li>one</li></ul>");
        var item = FirstBlockWithElement(laid.Root, "li");

        Assert.Multiple(() =>
        {
            Assert.That(item.MarkerBounds, Is.Null, "nothing is drawn where a marker would have stood");
            // The gutter stays, which is what a browser does too: the space belongs to the list, and a
            // page that took back its markers did not say anything about the room around them.
            Assert.That(item.ContentBox.X, Is.EqualTo(40).Within(0.5));
        });
    }

    [Test]
    public async Task APageThatResetsItsListMarginsStillHasRoomForItsMarkers()
    {
        var laid = await LaidOut("", "<ul style='margin:0'><li>one</li></ul>");
        var item = Marked(laid.Root);

        // The gutter is a padding, which is what a browser makes it too: a page that zeroes its list
        // margins - and nearly every page does that to a list - takes away the space around the list,
        // not the space its markers stand in.
        Assert.Multiple(() =>
        {
            Assert.That(item.ContentBox.X, Is.EqualTo(40).Within(0.5), "the items still begin 40px in");
            Assert.That(item.MarkerBounds!.Value.X, Is.GreaterThan(0), "and the marker is on the page");
        });
    }

    [Test]
    public async Task AColumnIsMarkedAtItsHeadAndCentredAcrossItsWidth()
    {
        var laid = await LaidOut("", "<ul style='writing-mode:vertical-rl'><li>item</li></ul>");
        var item = Marked(laid.Root);
        var column = FirstLine(laid.Root);
        var marker = item.MarkerBounds!.Value;

        // Along a column the inline axis is vertical, so the same rule - the marker's end on the edge the
        // line begins at - puts it above the column rather than beside its text, and an upright character
        // sits centred across the width the column takes. This is what a browser draws.
        Assert.Multiple(() =>
        {
            Assert.That(marker.Bottom, Is.EqualTo(column.BorderBox.Y).Within(0.5),
                "the marker ends at the head of the column, its own space leaving the room below it");
            Assert.That(marker.X + marker.Width / 2, Is.EqualTo(column.BorderBox.X + column.BorderBox.Width / 2).Within(0.5),
                "and it stands across the middle of the column");
            Assert.That(marker.Height, Is.GreaterThan(0), "the marker runs down, not across");
            Assert.That(item.Source.Marker, Is.EqualTo("• "));
        });
    }

    [Test]
    public void ADisclosurePointsTheWayItsContentWouldOpen()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ListMarkers.For("disclosure-closed", null, 1), Is.EqualTo("▸ "),
                "a closed widget points toward the text it is hiding");
            Assert.That(ListMarkers.For("disclosure-open", null, 1), Is.EqualTo("▾ "),
                "and an open one points down into the text it let out");
            Assert.That(ListMarkers.For("disclosure-closed", null, 7), Is.EqualTo("▸ "),
                "neither is counted: the seventh item wears the same mark as the first");
        });
    }

    [Test]
    public void TheLetteringTurnsOverWhereTheAlphabetRunsOut()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ListMarkers.For("lower-latin", null, 26), Is.EqualTo("z. "));
            Assert.That(ListMarkers.For("lower-alpha", null, 27), Is.EqualTo("aa. "),
                "cyclic, the way the spec's alphabetic systems run - and the alias counts as the same system");
            Assert.That(ListMarkers.For("upper-latin", null, 28), Is.EqualTo("AB. "));
            Assert.That(ListMarkers.For("lower-roman", null, 14), Is.EqualTo("xiv. "));
            Assert.That(ListMarkers.For("upper-roman", null, 4), Is.EqualTo("IV. "));
            Assert.That(ListMarkers.For("lower-roman", null, 0), Is.Null,
                "there is no roman zero, and no marker is claimed rather than a wrong one");
            Assert.That(ListMarkers.For("decimal", null, -3), Is.EqualTo("-3. "),
                "a count below one is still a number, signed");
            Assert.That(ListMarkers.For("disc", "url(bullet.png)", 1), Is.Null,
                "an image marker waits for the painting that can draw one");
            Assert.That(ListMarkers.For("armenian", null, 1), Is.Null,
                "a style whose table we do not have claims no marker rather than a wrong one");
        });
    }

    /// <summary>
    /// The positional systems, checked against Unicode's own answer for a digit rather than against
    /// characters typed out here: a table whose zero sits at the wrong code point is exactly the mistake
    /// this can catch, and no expected string written from memory could.
    /// </summary>
    [Test]
    public void EachScriptCountsInItsOwnNumerals()
    {
        var scripts = new[]
        {
            "decimal", "arabic-indic", "bengali", "cambodian", "devanagari", "gujarati", "gurmukhi",
            "kannada", "lao", "malayalam", "mongolian", "myanmar", "oriya", "persian", "tamil",
            "telugu", "thai", "tibetan", "cjk-decimal",
        };

        foreach (var script in scripts)
        {
            var marker = ListMarkers.For(script, null, 121)!;
            var digits = marker.Substring(0, marker.Length - 2);

            Assert.Multiple(() =>
            {
                Assert.That(digits.Length, Is.EqualTo(3), $"{script} writes three digits for a hundred and twenty-one");
                Assert.That(IndexOf(digits[0]), Is.EqualTo(1), $"{script} begins with its own one");
                Assert.That(IndexOf(digits[1]), Is.EqualTo(2), $"{script} holds its own two");
                Assert.That(IndexOf(digits[2]), Is.EqualTo(1), $"{script} ends where it began");
            });

            // Unicode says which number a character is a digit of, which is the question worth asking of
            // a table built from a script's zero: the row has to be that script's ten consecutive digits
            // and nothing else. The CJK ideographs are not digits to Unicode, so they are read out of the
            // row they are made of.
            static Int32 IndexOf(Char digit)
            {
                var value = CharUnicodeInfo.GetDecimalDigitValue(digit);

                return value >= 0 ? value : "〇一二三四五六七八九".IndexOf(digit);
            }
        }
    }

    [Test]
    public void AHebrewCountIsAddedUpFromTheLargestWorth()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ListMarkers.For("hebrew", null, 1), Is.EqualTo("א. "));
            Assert.That(ListMarkers.For("hebrew", null, 11), Is.EqualTo("יא. "));

            // Fifteen and sixteen are written with the letters of טו and טז rather than the two that
            // would spell a name, which is the one rule in Hebrew numbering a reader looks for.
            Assert.That(ListMarkers.For("hebrew", null, 15), Is.EqualTo("טו. "));
            Assert.That(ListMarkers.For("hebrew", null, 16), Is.EqualTo("טז. "));
            Assert.That(ListMarkers.For("hebrew", null, 18), Is.EqualTo("יח. "));
            Assert.That(ListMarkers.For("hebrew", null, 25), Is.EqualTo("כה. "));
            Assert.That(ListMarkers.For("hebrew", null, 40), Is.EqualTo("מ. "),
                "forty is the open mem, not the final form that sits next to it in the block");
            Assert.That(ListMarkers.For("hebrew", null, 115), Is.EqualTo("קטו. "),
                "and the exception holds inside a larger number");
            Assert.That(ListMarkers.For("hebrew", null, 500), Is.EqualTo("תק. "),
                "past four hundred the hundreds are made of the same pieces");
            Assert.That(ListMarkers.For("hebrew", null, 999), Is.EqualTo("תתקצט. "));
            Assert.That(ListMarkers.For("hebrew", null, 1000), Is.EqualTo("1000. "),
                "beyond a thousand the count falls back to decimal");
        });
    }

    [Test]
    public void AChineseCountingStyleReadsTheNumberAsItIsSaid()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ListMarkers.For("simplified-chinese-informal", null, 3), Is.EqualTo("三. "));
            Assert.That(ListMarkers.For("simplified-chinese-informal", null, 10), Is.EqualTo("十. "),
                "a leading ten is read without its one");
            Assert.That(ListMarkers.For("simplified-chinese-informal", null, 11), Is.EqualTo("十一. "));
            Assert.That(ListMarkers.For("simplified-chinese-informal", null, 20), Is.EqualTo("二十. "));
            Assert.That(ListMarkers.For("simplified-chinese-informal", null, 105), Is.EqualTo("一百零五. "),
                "a skipped place is read out as a zero");
            Assert.That(ListMarkers.For("simplified-chinese-informal", null, 110), Is.EqualTo("一百一十. "),
                "but a place that only ends empty is not");
            Assert.That(ListMarkers.For("simplified-chinese-informal", null, 1001), Is.EqualTo("一千零一. "));
            Assert.That(ListMarkers.For("simplified-chinese-informal", null, 10000), Is.EqualTo("一万. "));
            Assert.That(ListMarkers.For("simplified-chinese-informal", null, 100000), Is.EqualTo("十万. "),
                "a group of ten myriad is said as ten myriad, not as one-times-ten myriad");
            Assert.That(ListMarkers.For("simplified-chinese-informal", null, 10000005), Is.EqualTo("一千万零五. "));
            Assert.That(ListMarkers.For("simplified-chinese-informal", null, 100000005), Is.EqualTo("一亿零五. "));
            Assert.That(ListMarkers.For("simplified-chinese-informal", null, 1000), Is.EqualTo("一千. "));
            Assert.That(ListMarkers.For("traditional-chinese-informal", null, 10000), Is.EqualTo("一萬. "));
            Assert.That(ListMarkers.For("cjk-ideographic", null, 5), Is.EqualTo("五. "),
                "the legacy name counts the traditional way");

            // The formal reading is the anti-fraud hand: it keeps the one of its ten, because a bare
            // 十 stands ready to be made into 二十 by one added stroke.
            Assert.That(ListMarkers.For("simplified-chinese-formal", null, 12), Is.EqualTo("壹拾贰. "));
            Assert.That(ListMarkers.For("traditional-chinese-formal", null, 12), Is.EqualTo("壹拾貳. "));

            Assert.That(ListMarkers.For("simplified-chinese-informal", null, -1), Is.Null,
                "there is no reading below zero, and none is claimed");
            Assert.That(ListMarkers.For("simplified-chinese-informal", null, 1_000_000_000), Is.EqualTo("十亿. "),
                "the hundred million group reads its ten the same way a plain ten does");
            Assert.That(ListMarkers.For("simplified-chinese-informal", null, int.MaxValue),
                Does.Contain("亿").And.Contain("万"),
                "and the largest number an item can hold still comes out of the groups it is read by");
        });
    }

    [Test]
    public async Task ALivePageCountsItsItemsInTheStyleEachListAskedFor()
    {
        var markers = await Markers(
            ".hebrew { list-style-type: hebrew } .cn { list-style-type: simplified-chinese-informal }",
            "<ol class='hebrew' start='14'><li>one</li><li>two</li></ol>" +
            "<ol class='cn'><li>one</li><li>two</li></ol>");

        Assert.That(markers, Is.EqualTo(new[] { "יד. ", "טו. ", "一. ", "二. " }),
            "the transition the showcase page was built to show: fourteen, then fifteen written the way it must be");
    }

    [Test]
    public void TheFixedListsRunOutAndTheCountCarriesOnInDecimal()
    {
        Assert.Multiple(() =>
        {
            // Two scripts named outright, because a table built from the wrong zero would still pass the
            // check above as a run of digits.
            Assert.That(ListMarkers.For("arabic-indic", null, 25), Is.EqualTo("٢٥. "));
            Assert.That(ListMarkers.For("thai", null, 7), Is.EqualTo("๗. "));

            Assert.That(ListMarkers.For("cjk-earthly-branch", null, 1), Is.EqualTo("子. "));
            Assert.That(ListMarkers.For("cjk-earthly-branch", null, 12), Is.EqualTo("亥. "),
                "twelve branches, and the twelfth item is the last of them");
            Assert.That(ListMarkers.For("cjk-earthly-branch", null, 13), Is.EqualTo("13. "),
                "beyond the list the count falls back to plain decimal, which is what the spec asks for");
            Assert.That(ListMarkers.For("cjk-heavenly-stem", null, 10), Is.EqualTo("癸. "));
            Assert.That(ListMarkers.For("cjk-heavenly-stem", null, 11), Is.EqualTo("11. "),
                "and the stems run out at ten");
        });
    }

    [Test]
    public async Task ANameTheParserHasNoKeywordForIsStillMarked()
    {
        // <c>upper-greek</c> reaches us as plain text rather than as a parsed style, which is why the
        // systems are looked up by the name the page wrote.
        var markers = await Markers("", "<ol style='list-style-type:upper-greek'><li>one</li><li>two</li></ol>");

        Assert.That(markers, Is.EqualTo(new[] { "Α. ", "Β. " }));
    }

    [Test]
    public async Task AStringMarkerIsUsedExactlyAsItsAuthorWroteIt()
    {
        var arrows = await Markers("", "<ul style='list-style-type:\"\u2192  \"'><li>one</li></ul>");
        var escaped = await Markers("", "<ul style='list-style-type:\"\\2192  \"'><li>one</li></ul>");

        // The room after a string marker is inside the string, so nothing is added: no period, and no
        // space of our own.
        Assert.Multiple(() =>
        {
            Assert.That(arrows, Is.EqualTo(new[] { "→  " }), "the string is the marker, spaces included");
            Assert.That(escaped, Is.EqualTo(new[] { "→ " }),
                "written as an escape, one of those spaces is the escape's own terminator rather than " +
                "a character of the marker - which is why an author writes two");
        });
    }

    [Test]
    public async Task AStyleWithNoTableHereMarksNothing()
    {
        // The two kana lists and the additive Caucasian and Ethiopian styles need tables that have to be
        // taken from the spec rather than written out of memory, so they are absent on purpose: a list
        // without its marks is a gap, and a wrong mark is a lie.
        var markers = await Markers("", "<ol style='list-style-type:hiragana-iroha'><li>one</li></ol>");

        Assert.That(markers, Is.EqualTo(new String?[] { null }),
            "a style with no table here spells no marker");
    }

    [Test]
    public async Task AnInsideMarkerSharesTheFirstLineWithItsText()
    {
        var laid = await LaidOut("", "<ul style='list-style-position:inside'><li>one</li></ul>");
        var item = FirstBlockWithElement(laid.Root, "li");

        // An inside marker is the item's first inline box, so it is laid out with the text and takes room
        // on the line - which is a different machinery from the marker that hangs beside an item.
        Assert.Multiple(() =>
        {
            Assert.That(item.Source.Marker, Is.Null, "the mark is not carried beside the item");
            Assert.That(item.MarkerBounds, Is.Null, "and no place is worked out for it after the lines");
            Assert.That(Texts(FirstLine(laid.Root)), Is.EqualTo(new[] { "• ", "one" }),
                "it is the first run of the item's own line");
        });
    }

    [Test]
    public async Task AnInsideMarkerBeforeABlockTakesALineOfItsOwn()
    {
        var laid = await LaidOut("", "<ul style='list-style-position:inside'><li><p>text</p></li></ul>");
        var lines = Lines(laid.Root);

        Assert.That(lines, Has.Count.GreaterThanOrEqualTo(2), "the marker and the paragraph are two lines");
        Assert.Multiple(() =>
        {
            Assert.That(Texts(lines[0]), Is.EqualTo(new[] { "•" }),
                "the first inline box in a block whose rest is blocks makes a line of its own - and the " +
                "space its marker carries closes that line, so it is taken away as any trailing space is");
            Assert.That(Texts(lines[1]), Is.EqualTo(new[] { "text" }));
        });
    }

    [Test]
    public async Task AnInsideMarkerIsNotPartOfWhatTheReaderCopies()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(900, 600);

        await view.SetHtml(
            "<html><body style='margin:0'><ul style='list-style-position:inside'><li>one</li></ul></body></html>");

        view.SelectAll();

        Assert.That(view.SelectedText, Is.EqualTo("one"),
            "the mark is laid out with the text but the reader never chose it, so a copy leaves it out");
    }

    private static async Task<List<String?>> Markers(String css, String body)
    {
        var items = new List<String?>();

        foreach (var box in await Items(css, body))
        {
            items.Add(box.Marker);
        }

        return items;
    }

    /// <summary>The list items of a document, in the order the tree holds them.</summary>
    private static async Task<List<Box>> Items(String css, String body)
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(900, 600);

        var document = await view.SetHtml(
            $"<html><head><style>{css}</style></head><body>{body}</body></html>");

        var tree = BoxBuilder.TryBuildFused(document, new HutuRenderDevice(900, 600), (IImageProvider?)null);

        Assert.That(tree, Is.Not.Null, "these documents have to stay on the fused path");

        var items = new List<Box>();
        Collect(tree!, items);

        return items;
    }

    private static void Collect(Box box, List<Box> into)
    {
        if (box.Element is not null &&
            box.Style.Display.Equals("list-item", StringComparison.OrdinalIgnoreCase))
        {
            into.Add(box);
        }

        foreach (var child in box.Children)
        {
            Collect(child, into);
        }
    }

    /// <summary>A document laid out and painted, as one page of it would be for the reader.</summary>
    private static async Task<Laid> LaidOut(String css, String body)
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(900, 600);

        await view.SetHtml($"<html><head><style>{css}</style></head><body style='margin:0'>{body}</body></html>");

        Assert.That(view.Layout, Is.Not.Null, "the document has to be laid out");
        Assert.That(view.Paint, Is.Not.Null, "and painted");

        return new Laid(view.Layout!.Root, view.Paint!.Commands.ToList());
    }

    /// <summary>The box of a list item that carries a marker, or a failed test when none does.</summary>
    private static LayoutBox Marked(LayoutBox root, Func<Box, Boolean>? wants = null)
    {
        var found = Find(root, box => box.Source.Marker is { Length: > 0 } && (wants is null || wants(box.Source)));

        Assert.That(found, Is.Not.Null, "an item of this list carries a marker");

        return found!;
    }

    private static LayoutBox FirstBlockWithElement(LayoutBox root, String tag)
    {
        var found = Find(root, box => box.Kind == LayoutKind.Block && box.Source.Element?.LocalName == tag);

        Assert.That(found, Is.Not.Null, $"<{tag}> was laid out as a block");

        return found!;
    }

    private static LayoutBox FirstLine(LayoutBox root)
    {
        var found = Find(root, box => box.Kind == LayoutKind.Line);

        Assert.That(found, Is.Not.Null, "a line was laid out");

        return found!;
    }

    /// <summary>The text of every run under a box, in the order the layout set them down.</summary>
    private static List<String> Texts(LayoutBox box)
    {
        var runs = new List<String>();

        CollectTexts(box, runs);

        return runs;
    }

    private static void CollectTexts(LayoutBox box, List<String> into)
    {
        if (box.Kind == LayoutKind.Text)
        {
            into.Add(box.Text!);
        }

        foreach (var child in box.Children)
        {
            CollectTexts(child, into);
        }
    }

    /// <summary>Every line box under a root, in document order.</summary>
    private static List<LayoutBox> Lines(LayoutBox root)
    {
        var lines = new List<LayoutBox>();

        CollectLines(root, lines);

        return lines;
    }

    private static void CollectLines(LayoutBox box, List<LayoutBox> into)
    {
        if (box.Kind == LayoutKind.Line)
        {
            into.Add(box);
        }

        foreach (var child in box.Children)
        {
            CollectLines(child, into);
        }
    }

    private static LayoutBox? Find(LayoutBox box, Func<LayoutBox, Boolean> wants)
    {
        if (wants(box))
        {
            return box;
        }

        foreach (var child in box.Children)
        {
            if (Find(child, wants) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The laid out tree and the commands it painted.</summary>
    private sealed record Laid(LayoutBox Root, List<PaintCommand> Commands);
}

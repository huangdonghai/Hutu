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
    public async Task AColumnIsNotMarkedYet()
    {
        var laid = await LaidOut("", "<ul style='writing-mode:vertical-rl'><li>item</li></ul>");
        var item = FirstBlockWithElement(laid.Root, "li");

        // Along a column the marker stands above it rather than beside it, which is a placement of its
        // own. The counting is done all the same, so what is missing is only where it is drawn - and
        // nothing is drawn where the layout said nothing.
        Assert.That(item.Source.Marker, Is.EqualTo("• "), "the item knows its marker");
        Assert.That(item.MarkerBounds, Is.Null, "and no rectangle is claimed for a column yet");
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
            Assert.That(ListMarkers.For("hebrew", null, 1), Is.Null,
                "a style with no table here claims no marker rather than a wrong one");
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
        var markers = await Markers("", "<ol style='list-style-type:simplified-chinese-informal'><li>one</li></ol>");

        Assert.That(markers, Is.EqualTo(new String?[] { null }),
            "the spoken counting styles are still ahead of us, and a missing marker is honest where a " +
            "decimal one would not be");
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

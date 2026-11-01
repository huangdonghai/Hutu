using Hutu.Rendering;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using Hutu.Skia;
using NUnit.Framework;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

[TestFixture]
public sealed class InlineNoteTests
{
    [Test]
    public async Task AShortNoteIsTwoTextLinesInsideOneInlineBox()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "ab<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>cdef</span>gh" +
            "</p></body></html>");

        var notes = Notes(view).ToList();
        var note = notes.Single();
        var rows = TextRuns(note).ToList();
        var line = OuterLines(view).Single();

        Assert.Multiple(() =>
        {
            Assert.That(rows.Select(box => box.Text), Is.EqualTo(new[] { "cd", "ef" }));
            Assert.That(note.BorderBox.Width, Is.EqualTo(10).Within(0.5));
            Assert.That(note.BorderBox.Height, Is.EqualTo(20).Within(0.5));
            Assert.That(line.BorderBox.Height, Is.EqualTo(20).Within(0.5), "the note does not grow the body line");
        });
    }

    [Test]
    public async Task AnAuthorsLineHeightGrowsTheBodyLine()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "x<span id='n' style='-hutu-note-placement:inline;font-size:16px;line-height:16px'>abcd</span>y" +
            "</p></body></html>");

        var note = Notes(view).Single();
        var line = OuterLines(view).Single();

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.Height, Is.EqualTo(32).Within(0.5), "2h where h is the authored line-height");
            Assert.That(line.BorderBox.Height, Is.EqualTo(36).Within(0.5),
                "the grown line keeps the body's 2px half-leading before and after the note");
            Assert.That(note.BorderBox.Y, Is.EqualTo(2).Within(0.5),
                "the note has a full 2px gap on each side");
        });
    }

    [Test]
    public async Task ExpandedNoteLinesKeepTheBodyGapBetweenThem()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:16px;line-height:16px'>" +
            "ab<br>cd</span></p></body></html>");

        var notes = Notes(view).ToList();
        Assert.That(notes, Has.Count.EqualTo(2));
        var gap = notes[1].BorderBox.Y - (notes[0].BorderBox.Y + notes[0].BorderBox.Height);

        Assert.That(gap, Is.EqualTo(4).Within(0.5),
            "two expanded note lines each contribute the body's 2px gap");
    }

    [Test]
    public async Task AFontSizeAloneUsesTheInheritedLineHeight()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "x<span id='n' style='-hutu-note-placement:inline;font-size:10px'>abcd</span>y" +
            "</p></body></html>");

        var note = Notes(view).Single();

        Assert.That(note.BorderBox.Height, Is.EqualTo(40).Within(0.5),
            "the note inherits the outer line-height instead of substituting L / 2");
    }

    [Test]
    public async Task NeitherSizeNorLineHeightUsesInheritedValues()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "x<span id='n' style='-hutu-note-placement:inline'>abcd</span>y" +
            "</p></body></html>");

        var note = Notes(view).Single();

        Assert.That(note.BorderBox.Height, Is.EqualTo(40).Within(0.5),
            "both sublines inherit the body's 16px font and 20px line-height");
    }

    [Test]
    public async Task AnAuthoredLineHeightAloneKeepsItsExactSize()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "x<span id='n' style='-hutu-note-placement:inline;line-height:16px'>abcd</span>y" +
            "</p></body></html>");

        var note = Notes(view).Single();

        Assert.That(note.BorderBox.Height, Is.EqualTo(32).Within(0.5), "2h; the missing font size still follows the fallback");
    }

    [Test]
    public async Task AnUnstatedLineHeightIsInheritedByBothSublines()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0;writing-mode:vertical-rl'><p style='margin:0;font-size:16px;line-height:32px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:12.8px'>abcd</span>" +
            "</p></body></html>");

        var note = Notes(view).Single();
        var rows = TextRuns(note).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.Width, Is.EqualTo(64).Within(0.5),
                "each subline inherits the 32px line-height, so the note band is 64px");
            Assert.That(rows[0].BorderBox.Width, Is.EqualTo(32).Within(0.5),
                "a subline keeps the inherited line band");
        });
    }

    [Test]
    public async Task TheNotePlacementOverridesDisplay()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "x<span id='n' style='display:block;-hutu-note-placement:inline;font-size:10px;line-height:10px'>abcd</span>y" +
            "</p></body></html>");

        var note = Notes(view).Single();

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.Height, Is.EqualTo(20).Within(0.5));
            Assert.That(
                Lines(view).Count(line => line.Source.Style.NotePlacement == NotePlacement.None),
                Is.EqualTo(1),
                "a block display does not become a block outside the line");
        });
    }

    [Test]
    public async Task TheNotePlacementIgnoresAbsolutePositioning()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "x<span id='n' style='position:absolute;left:100px;top:100px;" +
            "-hutu-note-placement:inline;font-size:10px;line-height:10px'>abcd</span>y" +
            "</p></body></html>");

        var note = Notes(view).Single();

        Assert.That(note.BorderBox.X, Is.EqualTo(5).Within(0.5),
            "the note stands after x instead of at the absolute offset");
    }

    [Test]
    public async Task TheNotePlacementIgnoresFloat()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "x<span id='n' style='float:left;width:100px;height:100px;" +
            "-hutu-note-placement:inline;font-size:10px;line-height:10px'>abcd</span>y" +
            "</p></body></html>");

        var note = Notes(view).Single();

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.X, Is.EqualTo(5).Within(0.5),
                "the note keeps its inline position");
            Assert.That(note.BorderBox.Width, Is.EqualTo(10).Within(0.5),
                "the declared float size does not replace the note's measured width");
        });
    }

    [Test]
    public async Task ALongNoteContinuesInTwoLinesOnTheNextOuterLine()
    {
        using var view = CreateView(25);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>abcdefghijkl</span>" +
            "</p></body></html>");

        var notes = Notes(view).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(notes, Has.Count.EqualTo(2));
            Assert.That(TextRuns(notes[0]).Select(box => box.Text), Is.EqualTo(new[] { "abcde", "fghij" }));
            Assert.That(TextRuns(notes[1]).Select(box => box.Text), Is.EqualTo(new[] { "k", "l" }));
            Assert.That(notes[0].BorderBox.Width, Is.EqualTo(25).Within(0.5));
            Assert.That(notes[1].BorderBox.Width, Is.EqualTo(5).Within(0.5));
        });
    }

    [Test]
    public async Task AOneFragmentNoteUsesItsWholeBoxModel()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px;" +
            "margin:1px 2px 3px 4px;padding:1px 2px;border:1px solid'>abcd</span>" +
            "</p></body></html>");

        var note = Notes(view).Single();
        var line = OuterLines(view).Single();

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.Width, Is.EqualTo(16).Within(0.5), "10 content + 4 padding + 2 border");
            Assert.That(note.BorderBox.Height, Is.EqualTo(24).Within(0.5), "20 content + 2 padding + 2 border");
            Assert.That(note.Margin.Left, Is.EqualTo(4).Within(0.5));
            Assert.That(note.Margin.Right, Is.EqualTo(2).Within(0.5));
            Assert.That(note.BorderBox.X, Is.EqualTo(4).Within(0.5));
            Assert.That(line.BorderBox.Height, Is.EqualTo(32).Within(0.5),
                "the note's block margins participate, with 2px before and after the note");
            Assert.That(note.BorderBox.Y - note.Margin.Top, Is.EqualTo(2).Within(0.5));
        });
    }

    [Test]
    public async Task SlicedInlineEdgesBelongToTheFirstAndLastFragments()
    {
        using var view = CreateView(24);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px;" +
            "margin:0 2px;padding:0 1px;border:1px solid'>abcdefghijkl</span>" +
            "</p></body></html>");

        var notes = Notes(view).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(notes, Has.Count.EqualTo(2));
            Assert.That(notes[0].BorderBox.Width, Is.EqualTo(22).Within(0.5), "first fragment keeps only its start edges");
            Assert.That(notes[1].BorderBox.Width, Is.EqualTo(12).Within(0.5), "last fragment keeps only its end edges");
            Assert.That(notes[0].Margin.Right, Is.Zero);
            Assert.That(notes[1].Margin.Left, Is.Zero);
        });
    }

    [Test]
    public async Task ClonedDecorationBelongsToEveryFragment()
    {
        using var view = CreateView(24);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px;" +
            "margin:0 2px;padding:1px;border:1px solid;box-decoration-break:clone'>abcdefgh</span>" +
            "</p></body></html>");

        var notes = Notes(view).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(notes.Count, Is.GreaterThan(1));
            Assert.That(notes.All(note => note.Margin.Left == 2 && note.Margin.Right == 2), Is.True);
            Assert.That(notes.All(note => note.Padding.Left == 1 && note.Padding.Right == 1), Is.True);
            Assert.That(notes.All(note => note.Border.Left == 1 && note.Border.Right == 1), Is.True);
        });
    }

    [Test]
    public async Task ARealBackendDrawsTheTwoNoteRows()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(200, 100);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-family:sans-serif;font-size:16px;line-height:40px'>" +
            "<span style='-hutu-note-placement:inline;font-size:16px;line-height:16px'>note</span>" +
            "</p></body></html>");

        var paint = view.Paint!;
        var width = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Width));
        var height = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Height));
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.White);
        new SkiaPaintExecutor(measurer).Execute(paint, surface.Canvas);
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        Assert.That(bitmap.Pixels.Any(pixel => pixel != SKColors.White), Is.True);
    }

    [Test]
    public async Task InAVerticalLineTheFirstSublineIsOnTheBlockStartSide()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0;writing-mode:vertical-rl'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "x<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>甲乙</span>y" +
            "</p></body></html>");

        var note = Notes(view).Single();
        var rows = TextRuns(note).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.Width, Is.EqualTo(20).Within(0.5), "two sublines across the block axis");
            Assert.That(note.BorderBox.Height, Is.EqualTo(10).Within(0.5));
            Assert.That(rows[0].BorderBox.X, Is.GreaterThan(rows[1].BorderBox.X), "vertical-rl begins on the right");
            Assert.That(rows.All(row => row.Flow.Mode == WritingMode.VerticalRl), Is.True);
            Assert.That(rows.All(row => row.Flow.Orientation == TextOrientation.Upright), Is.True,
                "CJK glyphs stay upright inside the note");
        });
    }

    [Test]
    public async Task AColumnNoteDoesNotGrowItsAutoHeightContainingBlock()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div id='column' style='writing-mode:vertical-rl;" +
            "width:200px;height:100px;margin:0;font-size:10px;line-height:10px;" +
            "text-align:justify;text-justify:inter-character'>" +
            "<p id='origin' style='margin:0'>" +
            "初命晉晉大夫魏斯趙籍韓虔爲諸侯" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "此溫公書法所由始也魏之先畢公高後與周同姓其苗裔曰畢萬始封於魏至魏舒始爲晉正卿" +
            "</span></p></div></body></html>");

        var column = Walk(view.Layout!.Root!)
            .Single(box => box.Kind == LayoutKind.Block &&
                box.Source.Element?.GetAttribute("id") == "column");
        var origin = Walk(view.Layout.Root)
            .Single(box => box.Kind == LayoutKind.Block &&
                box.Source.Element?.GetAttribute("id") == "origin");

        Assert.Multiple(() =>
        {
            Assert.That(column.BorderBox.Height, Is.EqualTo(100).Within(0.5));
            Assert.That(origin.BorderBox.Height, Is.EqualTo(100).Within(0.5),
                "an auto-height block fills its column instead of growing from overflowing note text");

            var lines = Walk(origin)
                .Where(box => box.Kind == LayoutKind.Line && box.Source.Style.NotePlacement == NotePlacement.None)
                .ToList();
            Assert.That(lines, Is.Not.Empty);
            Assert.That(
                lines.All(line => line.BorderBox.Bottom <= origin.BorderBox.Bottom + 0.5),
                Is.True,
                "no outer line reaches past the column's inline end");
        });
    }

    [Test]
    public async Task AHangingAdvanceIsNotReusedByLaterNoteFragments()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div id='column' style='writing-mode:vertical-rl;" +
            "width:200px;height:100px;margin:0;font-size:10px;line-height:10px;" +
            "text-align:justify;text-justify:inter-character;hanging-punctuation:force-end'>" +
            "<p style='margin:0'>" +
            "甲乙丙丁戊己庚辛壬癸子丑寅卯辰巳午，" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            new String('注', 43) +
            "</span></p></div></body></html>");

        var column = Walk(view.Layout!.Root!)
            .Single(box => box.Kind == LayoutKind.Block &&
                box.Source.Element?.GetAttribute("id") == "column");
        var fragments = Notes(view).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(fragments, Has.Count.GreaterThan(1));
            Assert.That(
                fragments.All(note => note.BorderBox.Bottom <= column.BorderBox.Bottom + 0.5),
                Is.True,
                "only the first fragment belongs on the line that charged the hanging punctuation");
        });
    }

    [Test]
    public async Task AFirstNoteFragmentUsesTheSettledLineExtent()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div id='column' style='writing-mode:vertical-rl;" +
            "width:200px;height:386px;margin:0;font-size:10px;line-height:10px;" +
            "text-align:justify;text-justify:inter-character;hanging-punctuation:force-end'>" +
            "<p id='origin' style='margin:0;text-indent:32px'>" +
            new String('甲', 57) + "，" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "此溫公書法所由始也魏之先畢公高後與周同姓其苗裔曰畢萬始封於魏至魏舒始爲晉正卿" +
            "</span></p></div></body></html>");

        var first = Notes(view).First();
        var text = String.Concat(TextRuns(first).Select(run => run.Text));

        Assert.That(text, Does.Contain("魏"),
            $"the first note fragment should use the full remaining column ({text})");
    }

    [Test]
    public async Task AOneCharacterNoteKeepsAnEmptySecondSubline()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "x<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>a</span>y" +
            "</p></body></html>");

        var note = Notes(view).Single();

        Assert.Multiple(() =>
        {
            Assert.That(TextRuns(note).Select(box => box.Text),
                Is.EqualTo(new[] { "a" }));
            Assert.That(note.BorderBox.Height, Is.EqualTo(20).Within(0.5));
        });
    }

    [Test]
    public async Task AnAuthoredWhiteSpaceModeSurvivesTheSingleLineConstraint()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px;" +
            "white-space:pre-wrap'>ab cd</span></p></body></html>");

        var runs = TextRuns(Notes(view).Single()).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(runs.Select(run => run.Text), Is.EqualTo(new[] { "ab", "cd" }));
            Assert.That(runs.All(run => run.Source.Style.WhiteSpace == "pre-wrap"), Is.True);
        });
    }

    [Test]
    public async Task ACollapsibleWhitespaceOnlyNoteDrawsNothing()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "x<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "   " +
            "</span>y</p></body></html>");

        Assert.That(Notes(view), Is.Empty);
    }

    [Test]
    public async Task SourceFormattingWhitespaceDoesNotBecomeAHardNoteBreak()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>\n" +
            "  &#x7532;\n" +
            "  <span>&#x4E59;</span>\n" +
            "</span></p></body></html>");

        var note = Notes(view).Single();

        Assert.That(TextRuns(note).Select(run => run.Text), Is.EqualTo(new[] { "甲", "乙" }));
    }

    [Test]
    public async Task ABreakEndsTheOuterFragment()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>ab<br>cd</span>" +
            "</p></body></html>");

        var notes = Notes(view).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(notes, Has.Count.EqualTo(2));
            Assert.That(TextRuns(notes[0]).Select(box => box.Text), Is.EqualTo(new[] { "a", "b" }));
            Assert.That(TextRuns(notes[1]).Select(box => box.Text), Is.EqualTo(new[] { "c", "d" }));
        });
    }

    [Test]
    public async Task CollapsibleWhitespaceAfterABreakAddsNoEmptyFragment()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "ab<br>\n   " +
            "</span></p></body></html>");

        Assert.That(Notes(view).ToList(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AProportionalFaceUsesTheWiderMeasuredSubline()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(400, 100);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-family:serif;font-size:16px;line-height:20px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:16px;line-height:16px'>Wide</span>" +
            "</p></body></html>");

        var note = Notes(view).Single();
        var font = note.Source.Style.GetFont();
        var expected = Math.Max(measurer.MeasureWidth("Wi", font), measurer.MeasureWidth("de", font));

        Assert.That(note.BorderBox.Width, Is.EqualTo(expected).Within(0.5));
    }

    [Test]
    public async Task TheTwoSublinesAreBalancedByMeasuredAdvance()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(400, 100);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-family:serif;" +
            "font-size:20px;line-height:40px'>" +
            "<span id='n' style='-hutu-note-placement:inline;line-height:20px'>WWWWiiii</span>" +
            "</p></body></html>");

        var rows = TextRuns(Notes(view).Single()).ToList();
        Assert.That(rows, Has.Count.EqualTo(2));
        var firstWidth = measurer.MeasureWidth(rows[0].Text!, rows[0].Font);
        var secondWidth = measurer.MeasureWidth(rows[1].Text!, rows[1].Font);

        Assert.Multiple(() =>
        {
            Assert.That(rows[0].Text!.Length, Is.LessThan(rows[1].Text!.Length),
                "the wider W glyphs leave more of the narrow i glyphs to the second subline");
            Assert.That(Math.Abs(firstWidth - secondWidth),
                Is.LessThan(measurer.MeasureWidth("W", rows[0].Font)),
                "the split is chosen by actual advance rather than by character count");
        });
    }

    [Test]
    public async Task ASublineNeverStartsWithAProhibitedComma()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "甲乙，丙丁" +
            "</span></p></body></html>");

        var rows = Walk(Notes(view).Single())
            .Where(box => box.Kind == LayoutKind.Line)
            .Select(row => String.Concat(TextRuns(row).Select(run => run.Text)))
            .ToList();

        Assert.That(rows.Any(row => row.StartsWith("，", StringComparison.Ordinal)), Is.False,
            "UAX #14 punctuation prohibition keeps the comma on the preceding subline");
    }

    [Test]
    public async Task AProhibitedBoundaryNearTheMiddleDoesNotForceOneSidedSplit()
    {
        using var view = CreateView(120);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "繻者，諡法所不載。史記註：「繻」，或作「繚」。繻，詢趨翻。" +
            "</span></p></body></html>");

        var notes = Notes(view).ToList();
        var rows = Walk(notes.Single()).Where(box => box.Kind == LayoutKind.Line).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(notes, Has.Count.EqualTo(1),
                "a balanced split past the punctuation prohibition lets the whole note fit");
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows.Select(row => row.BorderBox.Width), Is.All.LessThan(100),
                "the search does not fall back to putting the whole note in one subline");
        });
    }

    [Test]
    public async Task AnOuterNoteFragmentNeverStartsWithAProhibitedComma()
    {
        using var view = CreateView(10);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "甲乙，丙丁戊己庚" +
            "</span></p></body></html>");

        var fragments = Notes(view)
            .Select(note => String.Concat(TextRuns(note).Select(run => run.Text)))
            .ToList();

        Assert.That(fragments.Any(fragment => fragment.StartsWith("，", StringComparison.Ordinal)), Is.False,
            "an outer fragment boundary cannot move the comma to the next line's head");
    }

    [Test]
    public async Task TheBackendDrawsTheNoteBorderAroundBothRows()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(200, 100);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-family:sans-serif;font-size:16px;line-height:40px'>" +
            "<span style='-hutu-note-placement:inline;font-size:16px;line-height:16px;border:2px solid red'>abcd</span>" +
            "</p></body></html>");

        var paint = view.Paint!;
        var width = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Width));
        var height = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Height));
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.White);
        new SkiaPaintExecutor(measurer).Execute(paint, surface.Canvas);
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        Assert.That(
            bitmap.Pixels.Any(pixel => pixel.Red > 200 && pixel.Green < 60 && pixel.Blue < 60),
            Is.True,
            "the note's border box covers both sublines");
    }

    [Test]
    public async Task TheFirstHalfIsChosenInLogicalOrderForRightToLeftText()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "<span id='n' dir='rtl' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>ابجد</span>" +
            "</p></body></html>");

        var note = Notes(view).Single();
        var rows = TextRuns(note).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(rows.Select(box => box.Text), Is.EqualTo(new[] { "اب", "جد" }));
        });
    }

    [Test]
    public async Task AVerticalNoteOnlyContinuationLineKeepsTheStrutWidth()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0;writing-mode:vertical-rl;font-size:16px;line-height:24px'>" +
            "<p style='margin:0;height:25px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>abcdefghijkl</span>" +
            "</p></body></html>");

        var lines = Lines(view).Where(line => line.Source.Element?.LocalName == "p").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(lines.Count, Is.GreaterThan(1));
            Assert.That(lines.All(line => line.BorderBox.Width > 20), Is.True,
                "a continuation line holding only the note is still one strut wide");
        });
    }

    [Test]
    public async Task NestedInlineStylesStayOnTheirOwnRuns()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "a<span class='name' style='color:red;text-decoration:underline'>b</span>c" +
            "</span></p></body></html>");

        var note = Notes(view).Single();
        var runs = TextRuns(note).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(runs.Select(box => box.Text), Is.EqualTo(new[] { "a", "b", "c" }));
            Assert.That(runs[1].Source.Style.Color, Is.Not.EqualTo(runs[0].Source.Style.Color));
            Assert.That(runs[1].Source.Style.GetFont(), Is.EqualTo(note.Source.Style.GetFont()));
        });
    }

    [Test]
    public async Task OneNestedInlineWrapperSurvivesSeveralChildRuns()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "a<span id='decorated' style='background:red'>b<i>c</i>d</span>e" +
            "</span></p></body></html>");

        var note = Notes(view).Single();
        var wrappers = Walk(note)
            .Where(box => box.Kind == LayoutKind.Inline &&
                box.Source.Element?.GetAttribute("id") == "decorated")
            .ToList();

        Assert.That(wrappers, Has.Count.EqualTo(2),
            "the nested element keeps one fragment per subline rather than one per child run");
    }

    [Test]
    public async Task NestedLineHeightChangesOnlyItsOwnSublineHeight()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:20px'>" +
            "甲<span style='font-size:20px;line-height:40px'>乙</span>丙" +
            "</span></p></body></html>");

        var note = Notes(view).Single();
        var nested = TextRuns(note).Single(run => run.Text == "乙");
        var rows = Walk(note)
            .Where(box => box.Kind == LayoutKind.Line)
            .Select(box => box.BorderBox.Height)
            .OrderBy(height => height)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.Height, Is.EqualTo(60).Within(0.5),
                "the subline heights are the ordinary computed line boxes: 20px + 40px");
            Assert.That(rows, Is.EqualTo(new[] { 20.0, 40.0 }));
            Assert.That(nested.Font.Size, Is.EqualTo(20).Within(0.5));
            Assert.That(nested.Source.Style.LineHeight, Is.EqualTo(40).Within(0.5));
        });
    }

    [Test]
    public async Task AWholeSublineIsUprightWhenAnyOfItsTextIsCjk()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0;writing-mode:vertical-rl'>" +
            "<p style='margin:0;font-size:16px;line-height:20px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "甲乙<span id='latin'>A</span></span></p></body></html>");

        var latin = TextRuns(Notes(view).Single()).Single(run => run.Text == "A");

        Assert.That(latin.Flow.Orientation, Is.EqualTo(TextOrientation.Upright),
            "the subline's CJK makes its neighbouring Latin run upright too");
    }

    [Test]
    public async Task ASublineKeepsTheOrdinaryPunctuationAndAutospaceRules()
    {
        const String textRules =
            "font-family:serif;font-size:10px;line-height:20px;" +
            "text-autospace:normal;text-spacing-trim:trim-start;hanging-punctuation:first";

        var (ordinaryPunctuation, notePunctuation) = await ComparableRuns(
            "「A",
            "「A甲",
            textRules);
        var (ordinaryAutospace, noteAutospace) = await ComparableRuns(
            "A甲",
            "A甲B",
            textRules);

        Assert.Multiple(() =>
        {
            Assert.That(LayoutSignatures(notePunctuation), Is.EqualTo(LayoutSignatures(ordinaryPunctuation)));
            Assert.That(LayoutSignatures(noteAutospace), Is.EqualTo(LayoutSignatures(ordinaryAutospace)));
        });
    }

    [Test]
    public async Task NoteSublinesJustifyOnlyWhenTheNoteReachesTheLineEnd()
    {
        const String rules =
            "margin:0;font-size:16px;line-height:20px;text-align:justify;" +
            "text-justify:inter-character";
        const String note =
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "甲乙丙丁戊</span>";

        using var ending = CreateView(15);
        using var middle = CreateView(100);
        using var last = CreateView(15);

        await ending.SetHtml(
            $"<html><body style='margin:0'><p style='{rules}'>{note}X</p></body></html>");
        await middle.SetHtml(
            $"<html><body style='margin:0'><p style='{rules}'>{note}X</p></body></html>");
        await last.SetHtml(
            $"<html><body style='margin:0'><p style='{rules}'>{note}</p></body></html>");

        var endingRows = TextRuns(Notes(ending).Single()).ToList();
        var middleRows = TextRuns(Notes(middle).Single()).ToList();
        var lastRows = TextRuns(Notes(last).Single()).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(endingRows[1].BorderBox.Width, Is.EqualTo(15).Within(0.5),
                "the shorter subline is spread to the note's full measured width at the line end");
            Assert.That(middleRows[1].BorderBox.Width, Is.EqualTo(10).Within(0.5),
                "a note that still has following content keeps its natural subline widths");
            Assert.That(lastRows[1].BorderBox.Width, Is.EqualTo(10).Within(0.5),
                "an all-note last line keeps its natural ragged width");
        });
    }

    [Test]
    public async Task AContinuingAllNoteLineJustifiesButItsLastLineStaysRagged()
    {
        using var view = CreateView(25);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px;" +
            "text-align:justify;text-justify:inter-character'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "甲乙丙丁戊己庚辛壬癸子" +
            "</span></p></body></html>");

        var notes = Notes(view).ToList();
        var firstRows = Walk(notes[0]).Where(box => box.Kind == LayoutKind.Line).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(notes, Has.Count.GreaterThan(1));
            Assert.That(firstRows.Select(row => row.BorderBox.Width),
                Is.All.EqualTo(25).Within(0.5),
                "a non-last pure-note line fills the outer line before the note continues");
            Assert.That(notes[0].BorderBox.Width, Is.EqualTo(25).Within(0.5));
            Assert.That(notes[^1].BorderBox.Width, Is.EqualTo(5).Within(0.5),
                "the final pure-note line keeps its natural width");
            Assert.That(TextRuns(notes[^1]).All(run => run.Justification == 0), Is.True);
        });
    }

    [Test]
    public async Task AnAllNoteLastLineWithTrailingRoomStaysRagged()
    {
        using var view = CreateView(25);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px;" +
            "text-align:justify;text-justify:inter-character'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "甲乙丙丁戊" +
            "</span></p></body></html>");

        var note = Notes(view).Single();
        var rows = Walk(note).Where(box => box.Kind == LayoutKind.Line).ToList();
        var runs = TextRuns(note).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(note.BorderBox.Width, Is.EqualTo(15).Within(0.5),
                "the note box keeps its natural width on the last line");
            Assert.That(runs.Select(run => run.BorderBox.Width),
                Is.EqualTo(new[] { 15.0, 10.0 }).Within(0.5),
                "the last line is not justified and keeps the natural text widths");
            Assert.That(runs.All(run => run.Justification == 0), Is.True);
        });
    }

    [Test]
    public async Task TextAlignLastJustifyStillFillsAnAllNoteLastLine()
    {
        using var view = CreateView(25);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px;" +
            "text-align:justify;text-align-last:justify;text-justify:inter-character'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "甲乙丙丁戊" +
            "</span></p></body></html>");

        var note = Notes(view).Single();
        var rows = Walk(note).Where(box => box.Kind == LayoutKind.Line).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.Width, Is.EqualTo(25).Within(0.5));
            Assert.That(rows.Select(row => row.BorderBox.Width),
                Is.All.EqualTo(25).Within(0.5));
            Assert.That(
                TextRuns(note).Any(run => run.Justification != 0 && run.Justifies > 0),
                Is.True);
        });
    }

    [Test]
    public async Task AContinuingPureNoteLineFillsTheRoomLeftByItsDecorations()
    {
        using var view = CreateView(40);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px;" +
            "text-align:justify;text-justify:inter-character'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px;" +
            "margin-inline:2px;padding-inline:1px;border:1px solid;box-decoration-break:clone'>" +
            "甲乙丙丁戊己庚辛壬癸子丑寅卯" +
            "</span></p></body></html>");

        var notes = Notes(view).ToList();
        var firstRows = Walk(notes[0]).Where(box => box.Kind == LayoutKind.Line).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(notes, Has.Count.GreaterThan(1));
            Assert.That(firstRows, Has.Count.EqualTo(2));
            Assert.That(firstRows.Select(row => row.BorderBox.Width),
                Is.All.EqualTo(32).Within(0.5),
                "the sublines fill the room left inside the note's inline edges");
            Assert.That(notes[0].BorderBox.Width, Is.EqualTo(36).Within(0.5),
                "the note border box reaches the outer line edge after its margins");
        });
    }

    [Test]
    public async Task AnAllNoteFullLineHangsItsEndPunctuation()
    {
        using var view = CreateView(15);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px;" +
            "hanging-punctuation:force-end'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "甲乙丙丁。" +
            "</span></p></body></html>");

        var punctuation = TextRuns(Notes(view).Single()).Single(run => run.Text == "。");

        Assert.That(punctuation.HangSide, Is.Not.Zero,
            "force-end applies when the note fills the entire outer line");
    }

    [Test]
    public async Task ANoteAfterHangingPunctuationUsesTheChargedAdvanceOnce()
    {
        using var view = CreateView(35);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;width:35px;font-size:16px;" +
            "line-height:20px;text-align:justify;text-justify:inter-character;" +
            "hanging-punctuation:force-end'>" +
            "abc，<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "甲乙丙丁戊己庚辛壬癸" +
            "</span></p></body></html>");

        var notes = Notes(view).ToList();
        var first = notes[0];

        Assert.Multiple(() =>
        {
            Assert.That(notes, Has.Count.GreaterThan(1));
            Assert.That(first.BorderBox.Width, Is.EqualTo(15).Within(0.5),
                "the hanging punctuation's advance belongs to the text before the note");
            Assert.That(TextRuns(first).All(run => run.Justification == 0), Is.True,
                "the note must not be stretched by an advance that is already in the text");
        });
    }

    [Test]
    public async Task AnAllNoteWrappedLineWithResidualSpaceStillHangs()
    {
        using var view = CreateView(13);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px;" +
            "hanging-punctuation:force-end'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "甲乙丙。丁戊己庚" +
            "</span></p></body></html>");

        var notes = Notes(view).ToList();
        var punctuation = TextRuns(notes[0]).Single(run => run.Text == "。");

        Assert.Multiple(() =>
        {
            Assert.That(notes, Has.Count.GreaterThan(1));
            Assert.That(punctuation.HangSide, Is.Not.Zero,
                "a segment truncated for the next outer line is a line end even when a little space remains");
        });
    }

    [Test]
    public async Task NotePunctuationHangsOnlyWhenTheNoteReachesTheLineEnd()
    {
        const String rules =
            "margin:0;font-size:16px;line-height:20px;hanging-punctuation:last force-end";
        const String note =
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "甲乙。</span>";

        using var ending = CreateView(10);
        using var middle = CreateView(100);

        await ending.SetHtml(
            $"<html><body style='margin:0'><p style='{rules}'>{note}</p></body></html>");
        await middle.SetHtml(
            $"<html><body style='margin:0'><p style='{rules}'>{note}X</p></body></html>");

        var endingHangs = TextRuns(Notes(ending).Single()).Any(run => run.HangSide != 0);
        var middleHangs = TextRuns(Notes(middle).Single()).Any(run => run.HangSide != 0);

        Assert.Multiple(() =>
        {
            Assert.That(endingHangs, Is.True);
            Assert.That(middleHangs, Is.False);
        });
    }

    [Test]
    public async Task ASublineOpeningPunctuationHangsOnlyAtTheOuterLineEnd()
    {
        const String rules =
            "margin:0;font-size:16px;line-height:20px;hanging-punctuation:first";
        const String note =
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "「甲乙</span>";

        using var ending = CreateView(10);
        using var middle = CreateView(100);

        await ending.SetHtml(
            $"<html><body style='margin:0'><p style='{rules}'>{note}</p></body></html>");
        await middle.SetHtml(
            $"<html><body style='margin:0'><p style='{rules}'>{note}X</p></body></html>");

        var endingHangs = TextRuns(Notes(ending).Single()).Any(run => run.HangSide != 0);
        var middleHangs = TextRuns(Notes(middle).Single()).Any(run => run.HangSide != 0);

        Assert.Multiple(() =>
        {
            Assert.That(endingHangs, Is.True);
            Assert.That(middleHangs, Is.False);
        });
    }

    [Test]
    public async Task NestedInlineBackgroundAndEdgesPaintOnTheirOwnWrapper()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            "<span id='n' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "a<span style='background:red;border:1px solid;margin-inline:2px;padding:1px'>b</span>c" +
            "</span></p></body></html>");

        var note = Notes(view).Single();
        var wrappers = Walk(note).Where(box => box.Kind == LayoutKind.Inline).ToList();
        var decorated = wrappers.Single(box => box.Source.Style.BackgroundColor is not null);

        Assert.Multiple(() =>
        {
            Assert.That(decorated.Margin.Left, Is.EqualTo(2).Within(0.5));
            Assert.That(decorated.Margin.Right, Is.EqualTo(2).Within(0.5));
            Assert.That(decorated.Padding.Left, Is.EqualTo(1).Within(0.5));
            Assert.That(decorated.Border.Left, Is.EqualTo(1).Within(0.5));
        });
    }

    [Test]
    public async Task TheBackendDrawsANestedInlineBackgroundInsideTheNote()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(200, 100);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-family:sans-serif;font-size:16px;line-height:40px'>" +
            "<span style='-hutu-note-placement:inline;font-size:16px;line-height:16px'>" +
            "a<span style='background:red'>b</span>c</span></p></body></html>");

        var paint = view.Paint!;
        var width = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Width));
        var height = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Height));
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.White);
        new SkiaPaintExecutor(measurer).Execute(paint, surface.Canvas);
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        Assert.That(
            bitmap.Pixels.Any(pixel => pixel.Red > 200 && pixel.Green < 60 && pixel.Blue < 60),
            Is.True);
    }

    private static HutuViewInternal CreateView(Int32 viewportWidth)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static IEnumerable<LayoutBox> Notes(HutuViewInternal view) =>
        Walk(view.Layout!.Root!)
            .Where(box => box.Kind == LayoutKind.Inline &&
                box.Source.Style.NotePlacement == NotePlacement.Inline &&
                box.Source.Element?.GetAttribute("id") == "n");

    private static IEnumerable<LayoutBox> Lines(HutuViewInternal view) =>
        Walk(view.Layout!.Root!).Where(box => box.Kind == LayoutKind.Line);

    private static IEnumerable<LayoutBox> OuterLines(HutuViewInternal view) =>
        Lines(view).Where(box => box.Source.Style.NotePlacement == NotePlacement.None);

    private static async Task<(List<LayoutBox> Ordinary, List<LayoutBox> Note)> ComparableRuns(
        String ordinary,
        String noteText,
        String rules)
    {
        using var ordinaryView = CreateView(400);
        using var noteView = CreateView(400);

        await ordinaryView.SetHtml(
            $"<html><body style='margin:0'><p style='margin:0;{rules}'>{ordinary}</p></body></html>");
        await noteView.SetHtml(
            $"<html><body style='margin:0'><p style='margin:0;font-size:16px;line-height:20px'>" +
            $"<span id='n' style='-hutu-note-placement:inline;{rules}'>{noteText}</span>" +
            "</p></body></html>");

        var ordinaryRuns = TextRuns(OuterLines(ordinaryView).Single()).ToList();
        var firstSubline = Lines(noteView)
            .First(line => line.Source.Style.NotePlacement == NotePlacement.Inline);
        var noteRuns = TextRuns(firstSubline).ToList();

        return (ordinaryRuns, noteRuns);
    }

    private static String LayoutSignatures(IEnumerable<LayoutBox> runs) =>
        String.Join("\n", runs.Select(LayoutSignature));

    private static String LayoutSignature(LayoutBox run) =>
        $"{run.Text}|{run.BorderBox.X:R}|{run.BorderBox.Width:R}|" +
        $"{run.Trims}|{run.AutoSpaces}|{run.AutoSpaceRoom:R}|{run.Flow.Orientation}";

    private static IEnumerable<LayoutBox> TextRuns(LayoutBox note) =>
        Walk(note).Where(box => box.Kind == LayoutKind.Text);

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

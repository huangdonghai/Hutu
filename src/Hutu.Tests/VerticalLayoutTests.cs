using Hutu.Rendering.Layout;
using Hutu.Skia;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Vertical writing asserted through whole documents: the columns the block children stack into, the
/// way a line runs down one, the orientation each character is turned to, and the ruby annotations set
/// beside their base.
/// </summary>
[TestFixture]
public sealed class VerticalLayoutTests
{
    /// <summary>A column of a known size, set in a font the fake measurer gives 10px em boxes.</summary>
    private const String Column = "writing-mode:vertical-rl;width:300px;height:200px;font-size:10px;line-height:1;margin:0";

    [Test]
    public async Task ColumnStacksBlockChildrenRightToLeft()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='" + Column + "'>" +
            "<div style='width:30px;height:20px;margin:0'></div>" +
            "<div style='width:30px;height:20px;margin:0'></div>" +
            "</div></body></html>");

        var boxes = Blocks(view, "div").ToList();

        Assert.That(boxes[0].BorderBox.X, Is.EqualTo(0));
        Assert.That(boxes[0].BorderBox.Width, Is.EqualTo(300), "the declared width is the extent the columns stack along");
        Assert.That(boxes[0].BorderBox.Height, Is.EqualTo(200), "the declared height is how far each column runs");

        Assert.That(boxes[1].BorderBox.X, Is.EqualTo(270), "the first column sits against the right edge");
        Assert.That(boxes[2].BorderBox.X, Is.EqualTo(240), "the next one is the one to its left");
        Assert.That(boxes[1].BorderBox.Y, Is.EqualTo(0));
        Assert.That(boxes[1].BorderBox.Width, Is.EqualTo(30));
        Assert.That(boxes[1].BorderBox.Height, Is.EqualTo(20));
    }

    [Test]
    public async Task ColumnLaysItsChildrenOutToTheRightInVerticalLr()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-lr;width:300px;height:200px;margin:0'>" +
            "<div style='width:30px;height:20px;margin:0'></div>" +
            "<div style='width:30px;height:20px;margin:0'></div>" +
            "</div></body></html>");

        var boxes = Blocks(view, "div").ToList();

        Assert.That(boxes[1].BorderBox.X, Is.EqualTo(0));
        Assert.That(boxes[2].BorderBox.X, Is.EqualTo(30));
    }

    [Test]
    public async Task ColumnBreaksTextIntoLinesThatAdvanceLeftward()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:60px;height:40px;margin:0;font-size:10px;line-height:1'>" +
            "<p style='margin:0'>ああああああああ</p>" +
            "</div></body></html>");

        var lines = Lines(view).ToList();

        Assert.That(lines.Count, Is.EqualTo(2), "eight characters at 10px each do not fit down a 40px column at once");
        Assert.That(lines[0].BorderBox.Height, Is.EqualTo(40), "a line runs the whole height it was given, so it is taller than it is wide");
        Assert.That(lines[0].BorderBox.Width, Is.EqualTo(10));
        Assert.That(lines[0].BorderBox.X, Is.EqualTo(50));
        Assert.That(lines[1].BorderBox.X, Is.EqualTo(40), "the line after it is the one to its left");

        // The baseline is 8px into the em box, and a column measures it from its left edge.
        Assert.That(lines[0].Baseline, Is.EqualTo(2));
    }

    [Test]
    public async Task MixedOrientationKeepsIdeographsUprightAndTurnsLatin()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:200px;height:200px;margin:0;font-size:10px;line-height:1'>" +
            "<p style='margin:0'>あA</p>" +
            "</div></body></html>");

        var cjk = Text(view, "あ");
        var latin = Text(view, "A");

        Assert.That(cjk.Flow.IsVertical, Is.True);
        Assert.That(cjk.Flow.Orientation, Is.EqualTo(TextOrientation.Upright));
        Assert.That(cjk.BorderBox.Height, Is.EqualTo(10), "an upright character takes a whole em down the column");

        Assert.That(latin.Flow.Orientation, Is.EqualTo(TextOrientation.Sideways));
        Assert.That(latin.BorderBox.Height, Is.EqualTo(5), "a turned run takes the width it would have had in a line");
    }

    [Test]
    public async Task UprightStandsLatinCharactersUpOneUnderTheOther()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:200px;height:200px;margin:0;font-size:10px;line-height:1;text-orientation:upright'>" +
            "<p style='margin:0'>AB</p>" +
            "</div></body></html>");

        var latin = Text(view, "AB");

        Assert.That(latin.Flow.Orientation, Is.EqualTo(TextOrientation.Upright));
        Assert.That(latin.BorderBox.Height, Is.EqualTo(20), "each letter is given an em of its own instead of its own width");
    }

    [Test]
    public async Task SidewaysTurnsIdeographsWithTheRest()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:200px;height:200px;margin:0;font-size:10px;line-height:1;text-orientation:sideways'>" +
            "<p style='margin:0'>ああ</p>" +
            "</div></body></html>");

        var cjk = Text(view, "ああ");

        Assert.That(cjk.Flow.Orientation, Is.EqualTo(TextOrientation.Sideways));
        Assert.That(cjk.BorderBox.Height, Is.EqualTo(10), "the pair advances by the width of the characters rather than their em boxes");
    }

    [Test]
    public async Task PunctuationTakesTheEmTheLayoutMeasuredForIt()
    {
        // The form a column draws its punctuation in is picked at paint time, so the step the layout
        // measured has to stay the one em a wide character takes: that is what keeps every character
        // after a mark exactly where the layout put it.
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:200px;height:200px;margin:0;font-size:10px;line-height:1'>" +
            "<p style='margin:0'>あ、い。う</p>" +
            "</div></body></html>");

        var run = Text(view, "あ、い。う");

        Assert.That(run.Flow.Orientation, Is.EqualTo(TextOrientation.Upright));
        Assert.That(run.BorderBox.Height, Is.EqualTo(50), "five upright characters, the marks among them, take five ems");
    }

    [Test]
    public async Task CombiningUprightSetsShortRunsAsASingleEm()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:200px;height:200px;margin:0;font-size:10px;line-height:1'>" +
            "<p style='margin:0'>あ<span style='text-combine-upright:all'>2026</span></p>" +
            "</div></body></html>");

        var digits = Text(view, "2026");

        Assert.That(digits.Flow.CombinesUpright, Is.True, "the run was asked to stand as one upright box");
        Assert.That(digits.BorderBox.Height, Is.EqualTo(10), "four digits are squeezed into the em the characters around them take");
    }

    [Test]
    public async Task RubySetsItsReadingOnTheFarSideOfTheColumn()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:80px;height:60px;margin:0;font-size:10px;line-height:1'>" +
            "<p style='margin:0'><ruby>春<rt>はる</rt></ruby></p>" +
            "</div></body></html>");

        var baseText = Text(view, "春");
        var annotation = Text(view, "はる");

        Assert.That(annotation.BorderBox.X, Is.EqualTo(75), "the reading is set against the edge the column started at");
        Assert.That(annotation.BorderBox.Right, Is.EqualTo(80));
        Assert.That(annotation.BorderBox.X, Is.GreaterThan(baseText.BorderBox.X), "the reading is on the block-start side of the base");
        Assert.That(annotation.Font.Size, Is.LessThan(baseText.Font.Size), "a reading is set smaller than what it annotates");
        Assert.That(Lines(view).First().BorderBox.Width, Is.EqualTo(15), "the line grew by the room the reading needed");

        // The base stands in the orientation its own character resolves to, so an ideograph under a
        // reading is drawn upright exactly as one without it would be.
        Assert.That(baseText.Flow.Orientation, Is.EqualTo(TextOrientation.Upright));
        Assert.That(annotation.Flow.Orientation, Is.EqualTo(TextOrientation.Upright));
    }

    [Test]
    public async Task RubyBaseSharesTheColumnLineOfTheCharactersAroundIt()
    {
        // The reading adds a strip at the block-start end of the line, and the base keeps the rest -
        // but the rest is centred on the band, so the base is drawn on the very line its neighbours
        // stand on rather than half a reading off it.
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:80px;height:60px;margin:0;font-size:10px;line-height:1'>" +
            "<p style='margin:0'>春<ruby>夏<rt>なつ</rt></ruby>秋</p>" +
            "</div></body></html>");

        var before = Text(view, "春");
        var baseText = Text(view, "夏");
        var after = Text(view, "秋");
        var annotation = Text(view, "なつ");

        Assert.That(Centre(baseText), Is.EqualTo(Centre(before)).Within(0.01), "the base is on the line the character before it stands on");
        Assert.That(Centre(baseText), Is.EqualTo(Centre(after)).Within(0.01), "and on the one the character after it stands on");
        Assert.That(Centre(annotation), Is.GreaterThan(Centre(baseText)), "the reading is set on the block-start side of the base");
    }


    [Test]
    public async Task RubyBaseIsTurnedWhenItsOwnCharactersAreLatin()
    {
        // The base is oriented by its own text rather than by the fact that it carries a reading, so
        // a Latin base is still turned.
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:80px;height:60px;margin:0;font-size:10px;line-height:1'>" +
            "<p style='margin:0'><ruby>Ab<rt>えー</rt></ruby></p>" +
            "</div></body></html>");

        Assert.That(Text(view, "Ab").Flow.Orientation, Is.EqualTo(TextOrientation.Sideways));
    }

    [Test]
    public async Task RubySetsItsReadingAboveTheBaseInHorizontalWriting()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:1'><ruby>春<rt>はる</rt></ruby></p></body></html>");

        var baseText = Text(view, "春");
        var annotation = Text(view, "はる");

        Assert.That(annotation.BorderBox.Y, Is.EqualTo(0), "the reading takes the top of the line");
        Assert.That(annotation.BorderBox.Bottom, Is.EqualTo(baseText.BorderBox.Y));
        Assert.That(
            baseText.BorderBox.Y,
            Is.EqualTo(5),
            "the room a reading takes is its own line height - half the size and set solid, which is what `rt` asks for - and not the height of the font it is set in");
    }

    [Test]
    public async Task RubyPositionUnderSetsTheReadingOnTheOtherSide()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:1;ruby-position:under'><ruby>春<rt>はる</rt></ruby></p></body></html>");

        var baseText = Text(view, "春");
        var annotation = Text(view, "はる");

        Assert.That(baseText.BorderBox.Y, Is.EqualTo(0), "the base stays where it would have been without a reading");
        Assert.That(annotation.BorderBox.Y, Is.EqualTo(baseText.BorderBox.Bottom));
    }

    [Test]
    public async Task HorizontalTextIgnoresTextOrientation()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:1;text-orientation:sideways'>あA</p></body></html>");

        var line = Lines(view).Single();

        Assert.That(line.Flow.IsVertical, Is.False);
        Assert.That(line.BorderBox.Height, Is.EqualTo(10), "a line is still as tall as one em box");
        Assert.That(Text(view, "あA").BorderBox.Width, Is.EqualTo(10), "and advances by the width of its characters");
    }

    [Test]
    public async Task AnInlineBlockInAColumnHugsTheColumnItsContentNeeds()
    {
        using var view = CreateView(400);

        // The label a book sets beside a note: two characters and the padding under them, placed as
        // one piece in the line. Nothing about the line bounds it, so it is one column wide with the
        // two characters one under the other - and its own padding is part of the height it asks for.
        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;height:300px;margin:0;font-size:10px;line-height:1'>" +
            "<p style='margin:0'><span style='display:inline-block;padding-bottom:0.1em'>あい</span></p>" +
            "</div></body></html>");

        var label = Blocks(view, "span").Single();

        Assert.That(label.BorderBox.Width, Is.EqualTo(10), "one column, so the two characters are set one under the other");
        Assert.That(label.BorderBox.Height, Is.EqualTo(21), "and the padding under them is part of what that column holds");
    }

    [Test]
    public async Task AnInlineBlockInAColumnStandsOnTheSameColumnLine()
    {
        using var view = CreateView(400);

        // A label set smaller than the text around it - which is what a book does with the 索隱 and 集解
        // marks it sets beside a note. Its baseline is the baseline of the line inside it, so its text
        // has to land on the same column line as the text it stands among.
        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;height:300px;margin:0;font-size:20px;line-height:1'>" +
            "<p style='margin:0'>あ<span style='display:inline-block;font-size:10px'>い</span>う</p>" +
            "</div></body></html>");

        var line = Lines(view).First();
        var label = Blocks(view, "span").Single();
        var inner = label.Children.Single(child => child.Kind == LayoutKind.Line);

        Assert.That(
            ColumnLine(inner),
            Is.EqualTo(ColumnLine(line)).Within(0.01),
            "the text inside the label stands on the column line of the text beside it");
    }

    [Test]
    public async Task ACharacterOfThePlanesAboveTheBmpStandsInOneCell()
    {
        using var view = CreateView(400);

        // U+20000 is one character of the text and two of the string's. A column that stepped by the
        // string's characters gave it two cells of the measurer's ten pixels, where the ideograph is
        // given the one em box it is drawn in.
        var ideographs = Char.ConvertFromUtf32(0x20000) + Char.ConvertFromUtf32(0x20001);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='" + Column + "'>" + ideographs + "</div></body></html>");

        Assert.That(Text(view, ideographs).BorderBox.Height, Is.EqualTo(20), "two characters, two cells");
    }

    [Test]
    public void AUprightRunIsDecomposedIntoOneCellPerCodePoint()
    {
        using var measurer = new SkiaTextMeasurer();

        var text = Char.ConvertFromUtf32(0x20000) + Char.ConvertFromUtf32(0x2A6B2);

        Assert.That(
            measurer.DecomposeUpright(text, new FontStyle("SimSun", 16, 400, false)).Count,
            Is.EqualTo(2),
            "the pair of characters above the BMP is asked of the font as the one character it is");
    }

    [Test]
    public async Task AJustifiedColumnSpreadsTheWordsItStandsBetween()
    {
        // Ahem sets Latin at one em and carries no CJK glyph, so the ideographs go to a face that does
        // and stand upright in cells of the em: the turned run spreads by the glyph positions it is
        // drawn with, and the upright cells by the step between them. Five cells of twenty in a column
        // of a hundred and ten leave ten units of room, which goes between the words - and, where the
        // text is Chinese and writes no spaces, between its characters as well.
        var turned = InkColumn("ab cd ef");
        var spaced = InkColumn(Han + Han + " " + Han + Han + " " + Han);
        var written = InkColumn(string.Concat(Han, Zi, Han, Zi, Han, Zi, Han));

        Assert.That(turned, Is.EqualTo(new[] { (0, 35), (66, 105) }), "the turned run stands at 66");
        Assert.That(spaced.Max(r => r.End), Is.EqualTo(108), "the spaced line reaches the edge");

        // Chromium, measured on the same page, sets the five characters at 0, 23, 45, 68, 90: four
        // seams sharing the ten units of room at two and a half each, which is where the ink ends.
        Assert.That(written.Max(r => r.End), Is.EqualTo(108), "and so does the line with nothing to space it");
        Assert.That(
            InkColumn(string.Concat(Han, Zi, Han, Zi, Han, Zi, Han), align: "justify", expand: "inter-word")
                .Max(r => r.End),
            Is.EqualTo(98),
            "a line asked to expand only between words is left where it was");
    }

    [Test]
    public async Task AJustifiedColumnLeavesTheLineThatEndsTheBlockAlone()
    {
        // The line that ends the block is not spread (CSS Text 3 §5.7.1), so a column given room for the
        // whole of the text draws the same whether it asks for justification or not - which is the
        // control that keeps the other case honest: the shift there is the spread, and not the page.
        var plain = InkColumn("ab cd ef", height: 200, align: "left");
        var justified = InkColumn("ab cd ef", height: 200, align: "justify");
        var upright = InkColumn(string.Concat(Han, Zi, Han, Zi, Han, Zi, Han), height: 120, align: "left");
        var spread = InkColumn(string.Concat(Han, Zi, Han, Zi, Han, Zi, Han), height: 120, align: "justify");

        Assert.That(justified, Is.EqualTo(plain), "the last line is left as it was written");
        Assert.That(spread, Is.EqualTo(upright), "and so is a column of Chinese");
    }

    [Test]
    public void AJustifiedColumnSpreadsTheLineThatEndsItAsSoonAsTheBlockAsks()
    {
        // The same arithmetic a row shows, measured in Chromium on a column of five cells whose last line
        // is four: left to `auto` the line that ends the block keeps its ragged end, and `justify` puts
        // the room it left - thirty of a hundred and ten - whole into the one place inside it. The ink of
        // the last word stands at fifty-six unspread and eighty-six spread, and the column above it is
        // spread either way.
        var ragged = InkLastColumn("ab cd ef g", "auto");
        var spread = InkLastColumn("ab cd ef g", "justify");

        Assert.That(ragged, Is.EqualTo(new[] { (0, 35), (56, 75) }), "the line that ends the column is left ragged");
        Assert.That(spread, Is.EqualTo(new[] { (0, 35), (86, 105) }), "and reaches its edge once it is spread");
    }

    [Test]
    public void AColumnMovesItsLastLineWhereTheBlockTellsIt()
    {
        // A positional value is not an addition to the spread but a replacement for it, and along a
        // column `right` is the bottom the text runs out to: the thirty of room the line leaves is
        // shared before and after it for `center`, and given whole to the front of it for `right`.
        var centered = InkLastColumn("ab cd ef g", "center");
        var ended = InkLastColumn("ab cd ef g", "right");

        Assert.That(centered, Is.EqualTo(new[] { (11, 50), (71, 90) }), "fifteen of the room behind the line");
        Assert.That(ended, Is.EqualTo(new[] { (26, 65), (86, 105) }), "and all thirty, with the ink on the edge");
    }

    /// <summary>The ideographs the tests below are written with, named by their code points.</summary>
    private static readonly String Han = Char.ConvertFromUtf32(0x6F22);

    private static readonly String Zi = Char.ConvertFromUtf32(0x5B57);

    /// <summary>Draws five cells' worth of text in a column of a hundred and ten, and reads its ink.</summary>
    private static List<(Int32 Start, Int32 End)> InkColumn(
        String content, Int32 height = 110, String align = "justify", String expand = "auto",
        String last = "auto") => InkBand(content, height, align, expand, last, 0, 120);

    /// <summary>Draws the same page and reads the ink of the column the content ends in, and no other.</summary>
    /// <remarks>
    /// A block a hundred and twenty across fills its columns from the right, so the second of two stands
    /// eighty pixels along. The line that ends the block is the one <c>text-align-last</c> speaks for,
    /// and its ink is what has to be looked at - the column above it is spread whatever the property says.
    /// </remarks>
    private static List<(Int32 Start, Int32 End)> InkLastColumn(
        String content, String last, String align = "justify") =>
        InkBand(content, 110, align, "auto", last, 80, 100);

    private static List<(Int32 Start, Int32 End)> InkBand(
        String content, Int32 height, String align, String expand, String last, Int32 left, Int32 right)
    {
        using var measurer = new Hutu.Skia.SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(300, 300);

        view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:120px;height:" + height +
            "px;font-family:Ahem;font-size:20px;line-height:1;text-align:" + align +
            ";text-justify:" + expand + ";text-align-last:" + last + "'>" + content +
            "</div></body></html>").GetAwaiter().GetResult();

        return InkedRows(view, measurer, left, right);
    }

    /// <summary>Gets the rows that carry ink between two edges of the block, as the runs they fall into.</summary>
    private static List<(Int32 Start, Int32 End)> InkedRows(
        HutuViewInternal view, Hutu.Skia.SkiaTextMeasurer measurer, Int32 left, Int32 right)
    {
        var paint = view.Paint!;
        using var surface = SkiaSharp.SKSurface.Create(
            new SkiaSharp.SKImageInfo((Int32)paint.Bounds.Width, (Int32)paint.Bounds.Height));
        surface.Canvas.Clear(SkiaSharp.SKColors.White);
        new Hutu.Skia.SkiaPaintExecutor(measurer).Execute(paint, surface.Canvas);

        using var image = surface.Snapshot();
        using var bitmap = SkiaSharp.SKBitmap.FromImage(image);

        var rows = new List<(Int32 Start, Int32 End)>();

        for (var y = 0; y < bitmap.Height; y++)
        {
            var inked = false;

            for (var x = left; x < Math.Min(right, bitmap.Width); x++)
            {
                if (bitmap.GetPixel(x, y) != SkiaSharp.SKColors.White)
                {
                    inked = true;
                    break;
                }
            }

            if (!inked)
            {
                continue;
            }

            if (rows.Count > 0 && rows[^1].End == y - 1)
            {
                rows[^1] = (rows[^1].Start, y);
            }
            else
            {
                rows.Add((y, y));
            }
        }

        return rows;
    }

    private static Double ColumnLine(LayoutBox box) => box.BorderBox.X + box.Baseline;

    [Test]
    public async Task AColumnKeepsTheMarginsItsChildrenLeaveAlongIt()
    {
        using var view = CreateView(400);

        // Twenty glyphs of ten pixels are two hundred of column; ten and twenty of margin leave a
        // hundred and seventy of it, so the text is cut there and reaches the second column.
        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:100px;height:200px;margin:0;font-size:10px;line-height:1'>" +
            "<div style='margin:10px 0 20px 0'>" + new String('あ', 20) + "</div>" +
            "</div></body></html>");

        var kid = Blocks(view, "div").Last();

        Assert.That(
            kid.BorderBox.Y,
            Is.EqualTo(10).Within(0.5),
            "the margin that opens a child is across the column, so it is where the child begins down it");
        Assert.That(
            kid.BorderBox.Height,
            Is.EqualTo(170).Within(0.5),
            "and the two margins together are what the child has to run down, not the whole column");
    }

    [Test]
    public async Task ABlockInAColumnFillsItTheWayOneInARowFillsItsWidth()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;height:600px;margin:0;font-size:10px;line-height:1'>" +
            "<p style='margin:0'>あ</p>" +
            "</div></body></html>");

        var paragraph = Blocks(view, "p").Single();

        Assert.That(
            paragraph.BorderBox.Height,
            Is.EqualTo(600),
            "an automatic inline size fills the column, the way an automatic width fills a row");
        Assert.That(
            paragraph.BorderBox.Width,
            Is.EqualTo(10),
            "while the block size is the one the content asks for, which is one character of column");
    }

    [Test]
    public async Task AShortColumnPageIsStillAsTallAsTheViewport()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0;writing-mode:vertical-rl'>" +
            "<p style='margin:0;font-size:10px;line-height:1'>あ</p>" +
            "</body></html>");

        Assert.That(
            view.Layout!.Height,
            Is.EqualTo(600).Within(0.5),
            "a page of columns is a page whatever it holds, so one short chapter does not make a short document");
    }

    [Test]
    public async Task AShortColumnPageStandsAgainstTheSideItReadsFrom()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0;writing-mode:vertical-rl'>" +
            "<p style='margin:0;font-size:10px;line-height:1'>あ</p>" +
            "</body></html>");

        Assert.That(
            Blocks(view, "body").Single().BorderBox.Right,
            Is.EqualTo(400).Within(0.5),
            "a page begins at the edge it reads from, so one that does not fill the viewport stands against the right one");
        Assert.That(
            view.Layout!.Width,
            Is.EqualTo(400).Within(0.5),
            "and the document is a viewport wide however little it holds, as a row's is a viewport tall");
    }

    [Test]
    public async Task AColumnPageKeepsTheMarginItIsGivenOnTheSideItReadsFrom()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html style='margin:0 30px 0 0'><body style='margin:0;writing-mode:vertical-rl'>" +
            "<p style='margin:0;font-size:10px;line-height:1'>あ</p>" +
            "</body></html>");

        Assert.That(
            view.Layout!.Root.BorderBox.Right,
            Is.EqualTo(370).Within(0.5),
            "the page's right margin is the one on the side it reads from, so it is what sets the page in from that edge");
    }

    [Test]
    public async Task AColumnPageKeepsTheMarginThePageIsGiven()
    {
        using var view = CreateView(400);

        // No margin is declared here: the eight pixels come from the default sheet's `body` rule, which
        // a column has to read as the inset at the top that a row would read at the left. Sixty glyphs
        // of ten pixels are the whole six hundred of the viewport, so where the text is cut is what
        // says whether the margin took the sixteen off it.
        await view.SetHtml(
            "<html><body style='writing-mode:vertical-rl'>" +
            "<p style='margin:0;font-size:10px;line-height:1'>" + new String('あ', 60) + "</p>" +
            "</body></html>");

        var body = Blocks(view, "body").Single();

        Assert.That(body.BorderBox.Y, Is.EqualTo(8).Within(0.5), "the page is inset from the top by its margin");
        Assert.That(
            Lines(view).Count(),
            Is.EqualTo(2),
            "and the margins are what the text stops short of, so the last glyphs go to a second column");
    }

    [Test]
    public async Task ABodyOfColumnsMakesTheWholePageOne()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0;writing-mode:vertical-rl'>" +
            "<p style='margin:0;font-size:10px;line-height:1'>" + new String('あ', 70) + "</p>" +
            "</body></html>");

        var root = view.Layout!.Root;
        var lines = Lines(view).ToList();

        Assert.That(root.Vertical, Is.True, "the page runs the way its body does");
        Assert.That(
            root.BorderBox.Height,
            Is.EqualTo(600),
            "the page box is the viewport, so it is as tall as the viewport is and the columns are cut to it");
        Assert.That(lines.Count, Is.EqualTo(2), "seventy characters do not fit down one 600px column at ten pixels each");
        Assert.That(lines[0].BorderBox.Height, Is.EqualTo(600), "a line runs the whole height of the page");
    }

    private static HutuViewInternal CreateView(int viewportWidth)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static IEnumerable<LayoutBox> Blocks(HutuViewInternal view, String tag)
    {
        var root = view.Layout?.Root;

        if (root is null)
        {
            yield break;
        }

        foreach (var node in Walk(root))
        {
            if (node.Kind == LayoutKind.Block &&
                node.Source.Element is { } element &&
                String.Equals(element.LocalName, tag, StringComparison.OrdinalIgnoreCase))
            {
                yield return node;
            }
        }
    }

    private static IEnumerable<LayoutBox> Lines(HutuViewInternal view)
    {
        var root = view.Layout?.Root;

        return root is null
            ? Enumerable.Empty<LayoutBox>()
            : Walk(root).Where(node => node.Kind == LayoutKind.Line);
    }

    /// <summary>Finds the one text box drawn from the given characters, which is how a run is picked out.</summary>
    private static LayoutBox Text(HutuViewInternal view, String text)
    {
        var root = view.Layout?.Root;

        if (root is null)
        {
            throw new AssertionException("The document was not laid out.");
        }

        return Walk(root).FirstOrDefault(node => node.Kind == LayoutKind.Text && node.Text == text)
            ?? throw new AssertionException($"The layout holds no text box for '{text}'.");
    }

    /// <summary>
    /// Gets the block-axis centre of a box, which is the column line its glyph is drawn on: an upright
    /// character is centred across the box it was given, so two characters on one line share a centre.
    /// </summary>
    private static Double Centre(LayoutBox box) => box.BorderBox.X + (box.BorderBox.Width / 2);

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

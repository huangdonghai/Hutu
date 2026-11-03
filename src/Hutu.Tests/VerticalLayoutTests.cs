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
    public async Task AnOrthogonalChildReadsItsPercentageMarginsAgainstTheContainersInlineSize()
    {
        using var view = CreateView(400);

        // The container is horizontal, two hundred wide and one hundred tall. The child runs a column,
        // but its percentage margins still answer to the containing block's inline size - the width -
        // so each is twenty and its own inline size is the height left over (CSS 2.1 section 8.3).
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:200px;height:100px;font-size:10px;line-height:1;margin:0'>" +
            "<div style='writing-mode:vertical-lr;margin:10%'>XX</div>" +
            "</div></body></html>");

        var boxes = Blocks(view, "div").ToList();

        Assert.That(boxes[1].BorderBox.X, Is.EqualTo(20), "a tenth of the container's width");
        Assert.That(boxes[1].BorderBox.Height, Is.EqualTo(60), "and the inline size is what the margins leave");
    }

    [Test]
    public async Task AnOrthogonalChildBreaksAgainstTheContainersMaxHeight()
    {
        using var view = CreateView(400);

        // The container's height comes from nothing, so the column inside it is given no inline room to
        // break against. The fallback is the smallest of the container's own inner max size floored by its
        // min size and the scrollport's inner size (CSS Writing Modes 3 §7.3.1) - here twenty pixels, which
        // is four characters of the fake face, so fifteen units of text come to four columns of it.
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:300px;max-height:20px;font-size:10px;line-height:1;margin:0'>" +
            "<div style='writing-mode:vertical-rl'>a a a a a a a a</div>" +
            "</div></body></html>");

        var boxes = Blocks(view, "div").ToList();

        Assert.That(boxes[1].BorderBox.Height, Is.EqualTo(20), "the column runs as far as the container's max height");
        Assert.That(boxes[1].BorderBox.Width, Is.EqualTo(40), "and the rest of the text begins the next column");
    }

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
            "<p style='margin:0;text-autospace:no-autospace'>あA</p>" +
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
        // The reading adds a strip at the block-start end of the line, and the base stays where it would
        // have been without one: against the block-end edge, on the very line its neighbours stand on
        // rather than half a reading off it.
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:80px;height:60px;margin:0;font-size:10px;line-height:1'>" +
            "<p style='margin:0'>春<ruby>夏<rt>なつ</rt></ruby>秋</p>" +
            "</div></body></html>");

        var before = Text(view, "春");
        var baseText = Text(view, "夏");
        var after = Text(view, "秋");
        var annotation = Text(view, "なつ");

        Assert.That(baseText.BorderBox.X, Is.EqualTo(before.BorderBox.X).Within(0.01), "the base keeps the block-end edge the character before it stands against");
        Assert.That(baseText.BorderBox.X, Is.EqualTo(after.BorderBox.X).Within(0.01), "and the edge the character after it stands against");
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

    /// <summary>
    /// A ruby is a sequence of pairs, and each reading is set over the base it follows: two pairs in one
    /// element take two readings, each centred on its own base, rather than one reading spread across the
    /// whole word (CSS Ruby 1 §3.1).
    /// </summary>
    [Test]
    public async Task EachReadingIsSetOverTheBaseItFollows()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:1'>" +
            "<ruby>ab<rt>x</rt>cd<rt>y</rt></ruby></p></body></html>");

        var first = Text(view, "ab");
        var second = Text(view, "cd");
        var firstReading = Text(view, "x");
        var secondReading = Text(view, "y");

        Assert.Multiple(() =>
        {
            Assert.That(second.BorderBox.X, Is.GreaterThanOrEqualTo(first.BorderBox.Right), "and reads in order");
            Assert.That(
                firstReading.BorderBox.X + (firstReading.BorderBox.Width / 2),
                Is.EqualTo(first.BorderBox.X + (first.BorderBox.Width / 2)).Within(0.01),
                "the first reading stands over its own base");
            Assert.That(
                secondReading.BorderBox.X + (secondReading.BorderBox.Width / 2),
                Is.EqualTo(second.BorderBox.X + (second.BorderBox.Width / 2)).Within(0.01),
                "and so does the second");
        });
    }

    /// <summary>
    /// <c>text-emphasis</c> marks each character of a run at half the size of the text, and the marks are
    /// paint rather than content: they stand just outside the text's content edge and take no room in the
    /// line, which keeps the height the page asked for (CSS Text Decoration 3 §3.3, as Chrome draws it).
    /// </summary>
    [Test]
    public async Task EmphasisMarksTheTextWithoutTakingRoom()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:4;" +
            "text-emphasis-style: filled dot'>ab</p></body></html>");

        var run = Text(view, "ab");

        Assert.Multiple(() =>
        {
            Assert.That(run.Marks, Is.Not.Null, "the page asked for marks");
            Assert.That(run.Marks!.Count, Is.EqualTo(2), "one over each character");
            Assert.That(run.Marks[0].Text, Is.EqualTo(Char.ConvertFromUtf32(0x2022)));
            Assert.That(run.Marks[0].Font.Size, Is.EqualTo(5), "half the size of the text it marks");
            Assert.That(run.Marks[0].Band.Height, Is.EqualTo(5), "a strip its own size");
            Assert.That(run.Marks[0].Baseline, Is.InRange(1, 6), "and the glyph set solid inside it");
            Assert.That(
                run.Marks[0].Band.Y,
                Is.EqualTo(10),
                "the mark stands just above the text, in the leading the line already has");
            Assert.That(
                run.BorderBox.Height,
                Is.EqualTo(40),
                "the line keeps the height the page asked for; the mark takes nothing from it");
        });
    }

    /// <summary>
    /// The fill and the shape of <c>text-emphasis-style</c> answer to the value as a whole rather than to
    /// the order the two words were written in: <c>dot open</c> asks for the same open dot as
    /// <c>open dot</c> does, and a quoted token is a string the page wants drawn - never the keyword it
    /// happens to spell (CSS Text Decoration 4 §5.1's <c>[ [ filled | open ] || &lt;shape&gt; ]</c>, and
    /// the borrowed pages that write each pair both ways).
    /// </summary>
    [Test]
    public async Task TheFillOfAStyleAnswersWhicheverSideTheShapeWasWritten()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='margin:0;font-size:10px;line-height:4;text-emphasis-style:open dot'>aa</p>" +
            "<p style='margin:0;font-size:10px;line-height:4;text-emphasis-style:dot open'>ab</p>" +
            "<p style='margin:0;font-size:10px;line-height:4;text-emphasis-style:circle open'>ac</p>" +
            "<p style='margin:0;font-size:10px;line-height:4;text-emphasis-style:\"filled\"'>ad</p></body></html>");

        Assert.Multiple(() =>
        {
            Assert.That(
                Text(view, "ab").Marks![0].Text,
                Is.EqualTo(Text(view, "aa").Marks![0].Text),
                "the shape reads the fill that was written beside it, whichever side it stood on");
            Assert.That(
                Text(view, "aa").Marks![0].Text,
                Is.EqualTo(Char.ConvertFromUtf32(0x25E6)),
                "and an open dot is the white one, not the bullet a filled dot draws");
            Assert.That(
                Text(view, "ac").Marks![0].Text,
                Is.EqualTo(Char.ConvertFromUtf32(0x25CB)),
                "the same holds for the next shape the pages write both ways");
            Assert.That(
                Text(view, "ad").Marks![0].Text,
                Is.EqualTo("filled"),
                "a quoted token is the string the page wants drawn, not the keyword it spells");
        });
    }

    /// <summary>
    /// The two halves of <c>text-emphasis-position</c> answer different flows: a line puts its marks on
    /// the edge <c>over</c> or <c>under</c> names, whatever side stands beside them, and a column reads
    /// only the side - which a value that names none still gives it, because the initial value of the
    /// property is <c>over right</c> (CSS Text Decoration 4 §5.2).
    /// </summary>
    [Test]
    public async Task TheSideOfAPositionAnswersOnlyAColumn()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:4;" +
            "text-emphasis:filled dot;text-emphasis-position:over right'>ab</p>" +
            "<p style='margin:0;font-size:10px;line-height:4;writing-mode:vertical-rl;" +
            "text-emphasis:filled dot;text-emphasis-position:right under'>cd</p>" +
            "<p style='margin:0;font-size:10px;line-height:4;writing-mode:vertical-rl;" +
            "text-emphasis:filled dot;text-emphasis-position:under'>ef</p></body></html>");

        var line = Text(view, "ab");
        var column = Text(view, "cd");
        var unnamed = Text(view, "ef");

        Assert.Multiple(() =>
        {
            Assert.That(
                line.Marks![0].Band.Y,
                Is.EqualTo(10),
                "the over edge puts the marks just above the text, whatever side stands beside them");
            Assert.That(
                column.Marks![0].Band.X,
                Is.EqualTo(25),
                "the marks stand just past the right of the text, the side the value named, and not to the left under would have put them");
            Assert.That(
                unnamed.Marks![0].Band.X,
                Is.EqualTo(25),
                "a column takes the side the initial value names when the page wrote only an edge");
        });
    }

    /// <summary>
    /// The marks of a marked column stand just outside the text on the side the page named, and they take
    /// no room: the strip the page reads is the line's own, so the column is as wide as the line-height it
    /// asked for. The strip is taken from the side the marks physically stand on rather than the edge of
    /// the line the writing mode happens to call the end. The ascent of a turned glyph reaches to its
    /// right whichever way the columns march, so marks standing to the right ask for that ascent in
    /// <c>vertical-lr</c> as much as in <c>vertical-rl</c> - even though the right edge is where a
    /// left-to-right page of columns ends - and the baseline, which the drawing reads off the box, stands
    /// at the descent in from the left in the two modes (CSS Text Decoration 4 §5.2, CSS Writing Modes 3
    /// §5.1).
    /// </summary>
    [Test]
    public async Task AMarkedColumnKeepsItsStripOnTheSideTheMarksStand()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='margin:0;font-size:10px;line-height:4;writing-mode:vertical-rl;" +
            "text-emphasis:filled dot;text-emphasis-position:right'>aa</p>" +
            "<p style='margin:0;font-size:10px;line-height:4;writing-mode:vertical-lr;" +
            "text-emphasis:filled dot;text-emphasis-position:right'>bb</p>" +
            "<p style='margin:0;font-size:10px;line-height:4;writing-mode:vertical-rl;" +
            "text-emphasis:filled dot;text-emphasis-position:left'>cc</p>" +
            "<p style='margin:0;font-size:10px;line-height:4;writing-mode:vertical-lr;" +
            "text-emphasis:filled dot;text-emphasis-position:left'>dd</p></body></html>");

        // Forty for the line the page asked for; the marks take nothing from it and stand just outside
        // the text. The glyphs are ten wide in the middle of that line, so their content band runs fifteen
        // to twenty-five across: a column marked to the right puts the strip at twenty-five and one marked
        // to the left at ten. The baseline reads from the left edge in both modes, the descent in, which
        // is seventeen.
        var rightInRightwardsColumns = Text(view, "aa");
        var rightInLeftwardsColumns = Text(view, "bb");
        var leftInRightwardsColumns = Text(view, "cc");
        var leftInLeftwardsColumns = Text(view, "dd");

        Assert.Multiple(() =>
        {
            Assert.That(rightInLeftwardsColumns.Baseline, Is.EqualTo(rightInRightwardsColumns.Baseline),
                "the way the columns march says nothing about which side the marks stand on");
            Assert.That(leftInLeftwardsColumns.Baseline, Is.EqualTo(leftInRightwardsColumns.Baseline),
                "and it says nothing about the other side either");
            Assert.That(rightInRightwardsColumns.Baseline, Is.EqualTo(17),
                "the baseline is the descent in from the left edge");
            Assert.That(leftInRightwardsColumns.Baseline, Is.EqualTo(17),
                "a mark to the left does not move the text it marks");
            Assert.That(rightInRightwardsColumns.BorderBox.Width, Is.EqualTo(40),
                "the column is as wide as the line it was asked for; the marks take no room");
            Assert.That(rightInRightwardsColumns.Marks![0].Band.X, Is.EqualTo(25),
                "marks standing to the right fill the room just past the content edge on that side");
            Assert.That(leftInRightwardsColumns.Marks![0].Band.X, Is.EqualTo(10),
                "marks standing to the left fill the room just before it on theirs");
        });
    }

    /// <summary>
    /// What nothing was said about leaves every stop of a CJK sentence unmarked, wide or narrow: the
    /// marks stand over the letters and the digits, and no mark stands over <c>。</c> or <c>，</c>
    /// (CSS Text Decoration 4 §5.4.1, as text-emphasis-punctuation-2 draws it).
    /// </summary>
    [Test]
    public async Task EmphasisMarksLeaveEveryStopOfASentenceUnmarked()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:2;" +
            "text-emphasis:filled circle'>" + Char.ConvertFromUtf32(0x6625) + Char.ConvertFromUtf32(0x3002) +
            Char.ConvertFromUtf32(0x590f) + Char.ConvertFromUtf32(0xFF0C) + Char.ConvertFromUtf32(0x79CB) +
            "</p></body></html>");

        var run = Text(view, Char.ConvertFromUtf32(0x6625) + Char.ConvertFromUtf32(0x3002) +
            Char.ConvertFromUtf32(0x590f) + Char.ConvertFromUtf32(0xFF0C) + Char.ConvertFromUtf32(0x79CB));

        Assert.That(run.Marks, Is.Not.Null, "the page asked for marks");

        // Each character of the run is five wide, so the second and third marked letters stand at twelve
        // and a half and twenty-two and a half along - one stop in between each. Counting only the marked
        // characters would put them at the second and third place, which is where the stops themselves sit.
        var centres = run.Marks!.Select(mark => mark.Band.X + (mark.Band.Width / 2)).ToList();

        Assert.That(
            String.Join(", ", centres),
            Is.EqualTo("2.5, 12.5, 22.5"),
            "each mark stands over the letter it marks, with the stop it follows keeping its own room");
        Assert.That(
            run.Marks!.Count,
            Is.EqualTo(3),
            "one mark over each of the three letters, and none over the two stops");
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
            "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:1;text-orientation:sideways;text-autospace:no-autospace'>あA</p></body></html>");

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

        Assert.That(turned, Is.EqualTo(new[] { (0, 39), (70, 109) }), "the turned run stands at 70");
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

        Assert.That(ragged, Is.EqualTo(new[] { (0, 39), (60, 79) }), "the line that ends the column is left ragged");
        Assert.That(spread, Is.EqualTo(new[] { (0, 39), (90, 109) }), "and reaches its edge once it is spread");
    }

    [Test]
    public void AColumnMovesItsLastLineWhereTheBlockTellsIt()
    {
        // A positional value is not an addition to the spread but a replacement for it, and along a
        // column `right` is the bottom the text runs out to: the thirty of room the line leaves is
        // shared before and after it for `center`, and given whole to the front of it for `right`.
        var centered = InkLastColumn("ab cd ef g", "center");
        var ended = InkLastColumn("ab cd ef g", "right");

        Assert.That(centered, Is.EqualTo(new[] { (15, 54), (75, 94) }), "fifteen of the room behind the line");
        Assert.That(ended, Is.EqualTo(new[] { (30, 69), (90, 109) }), "and all thirty, with the ink on the edge");
    }

    [Test]
    public void AColumnGivesItsCellsTheRoomBetweenThem()
    {
        // word-spacing along a column is the same arithmetic it is along a line: the cell after the
        // separator stands that much further down. Chromium, measured on this page - two ideographs of
        // twenty with a space between them in a column a hundred and ten tall - sets the cells at 0, 20
        // and 40, and at 0, 20 and 46 once the room is asked for, the space keeping its own em and
        // carrying the room on top of it. Ahem has no CJK glyph, so the characters go to a face that
        // does and stand one to a cell, their ink a pixel in from each edge of it.
        var spaced = InkColumn(string.Concat(Han, " ", Han), align: "left", spacing: "word-spacing:6px;");
        var plain = InkColumn(string.Concat(Han, " ", Han), align: "left");

        Assert.That(plain, Is.EqualTo(new[] { (1, 18), (41, 58) }), "the cells stepped by their em");
        Assert.That(spaced, Is.EqualTo(new[] { (1, 18), (47, 64) }), "and the last one carried down");
    }

    [Test]
    public void AColumnDrawsTheRoomBetweenTheWordsOfATurnedRun()
    {
        // The other orientation the column sets text in, and the case where the room has to be paid
        // inside one run rather than between two boxes: "ab c" is one turned piece, so the word after
        // the space is drawn by the same blob as the words before it, and the six goes between the
        // glyphs the way it does along a line - which is what Chromium measures as it does for any
        // language the column carries sideways.
        var spaced = InkColumn("ab c", align: "left", spacing: "word-spacing:6px;");
        var plain = InkColumn("ab c", align: "left");

        Assert.That(plain, Is.EqualTo(new[] { (0, 39), (60, 79) }), "the run stepped by its own advances");
        Assert.That(spaced, Is.EqualTo(new[] { (0, 39), (66, 85) }), "and the word carried down the column");
    }

    /// <summary>The ideographs the tests below are written with, named by their code points.</summary>
    private static readonly String Han = Char.ConvertFromUtf32(0x6F22);

    private static readonly String Zi = Char.ConvertFromUtf32(0x5B57);

    /// <summary>Draws five cells' worth of text in a column of a hundred and ten, and reads its ink.</summary>
    private static List<(Int32 Start, Int32 End)> InkColumn(
        String content, Int32 height = 110, String align = "justify", String expand = "auto",
        String last = "auto", String spacing = "") => InkBand(content, height, align, expand, last, 0, 120, spacing);

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
        String content, Int32 height, String align, String expand, String last, Int32 left, Int32 right,
        String spacing = "")
    {
        using var measurer = new Hutu.Skia.SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(300, 300);

        view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:120px;height:" + height +
            "px;font-family:Ahem;font-size:20px;line-height:1;text-align:" + align +
            ";text-justify:" + expand + ";text-align-last:" + last + ";" + spacing + "'>" + content +
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
    public async Task AnAutoHeightBlockDoesNotGrowForOverflowingText()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;" +
            "width:200px;height:100px;margin:0;font-size:10px;line-height:1'>" +
            "<p id='overflow' style='margin:0;white-space:nowrap'>" +
            new String('あ', 20) +
            "</p></div></body></html>");

        var paragraph = Blocks(view, "p").Single();

        Assert.That(
            paragraph.BorderBox.Height,
            Is.EqualTo(100),
            "overflowing content does not enlarge the block's automatic inline size");
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

    [Test]
    public async Task AFloatInAColumnStandsAtTheBlockStartAndLeavesTheLinesAlone()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='" + Column + "'>" +
            "<div id='f' style='float:left;width:40px;height:30px'></div>" +
            "<div id='b' style='width:60px;height:40px'></div>" +
            "</div></body></html>");

        var floated = Bounds(view, "f");
        var block = Bounds(view, "b");

        // A float in a column is put against the block-start edge - the right one in vertical-rl - and
        // opened at the line's start edge: Chrome measures 205-50 for a 50-wide float in a column whose
        // content ends at 205 (CSS 2.1 §9.5, remapped onto the column's axes).
        Assert.That(floated.X, Is.EqualTo(260), "pinned to the right edge, its width taken off");
        Assert.That(floated.Y, Is.EqualTo(0), "and standing at the start of the line");
        Assert.That(floated.Width, Is.EqualTo(40));
        Assert.That(floated.Height, Is.EqualTo(30));

        // The in-flow box does not step aside for it: a column's lines are not shortened by a float,
        // which is what Chrome draws and what the borrowed pairs expect.
        Assert.That(block.X, Is.EqualTo(240), "the first column, the float or no float");
        Assert.That(block.Y, Is.EqualTo(0));
    }

    [Test]
    public async Task AFloatPinnedToTheEndOfTheLineStandsAtTheBottom()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='" + Column + "'>" +
            "<div id='f' style='float:right;width:40px;height:30px'></div>" +
            "</div></body></html>");

        var floated = Bounds(view, "f");

        Assert.That(floated.X, Is.EqualTo(260), "against the block-start edge");
        Assert.That(floated.Y, Is.EqualTo(170), "and pinned to the line's end");
    }

    /// <summary>
    /// Two floats that name the same side of a column stand one after another along the line, every one
    /// of them against the block-start edge. Measured in Chrome: three <c>float: right</c> boxes of
    /// 40x30 in a 300x200 column come to y=170, 140 and 110, all at x=260.
    /// </summary>
    [Test]
    public async Task TwoFloatsOnOneSideStackAlongTheColumn()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='" + Column + "'>" +
            "<div id='a' style='float:left;width:40px;height:30px'></div>" +
            "<div id='b' style='float:left;width:40px;height:30px'></div>" +
            "<div id='c' style='float:right;width:40px;height:30px'></div>" +
            "<div id='d' style='float:right;width:40px;height:30px'></div>" +
            "</div></body></html>");

        Assert.That(Bounds(view, "a").X, Is.EqualTo(260), "the first takes the block-start band");
        Assert.That(Bounds(view, "b").X, Is.EqualTo(260), "and so does the second: they do not step aside");
        Assert.That(Bounds(view, "a").Y, Is.EqualTo(0), "the two of the head side march down the line");
        Assert.That(Bounds(view, "b").Y, Is.EqualTo(30));
        Assert.That(Bounds(view, "c").Y, Is.EqualTo(170), "and the two of the foot side march up from its end");
        Assert.That(Bounds(view, "d").Y, Is.EqualTo(140));
    }

    /// <summary>
    /// A paragraph written <c>unicode-bidi: plaintext</c> takes the way its line runs from the text on it
    /// rather than from the <c>dir</c> attribute or the <c>direction</c> property: the first character of
    /// strong direction settles it, and a paragraph whose text holds none keeps the direction it was
    /// given (UAX#9 §3.4, CSS Display 3 §5.3.7).
    /// </summary>
    [Test]
    public async Task APlaintextParagraphRunsItsColumnTheWayItsTextReads()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='writing-mode:vertical-rl;width:100px;height:200px;font-size:10px;" +
            "line-height:1;unicode-bidi:plaintext;direction:rtl'>ab</div>" +
            "<div style='writing-mode:vertical-rl;width:100px;height:200px;font-size:10px;" +
            "line-height:1;unicode-bidi:plaintext;direction:ltr'>\u0645\u0631</div>" +
            "<div style='writing-mode:vertical-rl;width:100px;height:200px;font-size:10px;" +
            "line-height:1;direction:rtl'>12</div>" +
            "</body></html>");

        // The three columns stand one under the other in the page's own flow, each two hundred tall. The
        // fake measurer gives a character five pixels, so a run of two inks ten of them - at the head of
        // its column where the line reads down, at the foot where it runs up.
        Assert.That(Text(view, "ab").BorderBox.Y, Is.EqualTo(0), "Latin decides left-to-right");
        Assert.That(Text(view, "\u0645\u0631").BorderBox.Bottom, Is.EqualTo(400), "Arabic decides right-to-left");
        Assert.That(Text(view, "12").BorderBox.Bottom, Is.EqualTo(600), "digits decide nothing");
    }

    private static Rect Bounds(HutuViewInternal view, String id)
    {
        var element = view.Document!.GetElementById(id);
        Assert.That(element, Is.Not.Null, $"no element with id '{id}'");
        var bounds = view.GetElementBounds(element!);
        Assert.That(bounds, Is.Not.Null, $"'{id}' generated no box");
        return bounds!.Value;
    }

    private static HutuViewInternal CreateView(int viewportWidth)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    /// <summary>
    /// Vertically-set Latin characters stand one per line box, so a run of six of them is six boxes down a
    /// column; sideways-set ones turn with the column and lie along it, so the same run is one long box.
    /// That difference is how the orientation a run was given can be read off the geometry, without asking
    /// the resolver.
    /// </summary>
    private static Double ColumnRun(HutuViewInternal view, String text) =>
        Text(view, text).BorderBox.Height;

    /// <summary>
    /// <c>text-orientation: initial</c> asks for the property's initial value, which is <c>mixed</c> - not
    /// "the value my parent came to". Read as a declaration this reader did not know, an arm written to
    /// start over kept the <c>upright</c> its parent asked for, and the Latin run it held stood one
    /// character per line rather than lying along the column (CSS Writing Modes 3 §5.1). Measured in Chrome
    /// on wpt's <c>text-orientation-initial</c>.
    /// </summary>
    [Test]
    public async Task TheInitialOrientationKeywordStartsOverRatherThanInheriting()
    {
        using var view = CreateView(400);

        // Six upright characters at a ten-pixel font take sixty pixels of column; the same six set
        // sideways along the column take thirty, one character's advance each.
        await view.SetHtml(
            "<html><body style='margin:0;font-size:10px;line-height:1'>" +
            "<div style='writing-mode:vertical-rl;text-orientation:upright'>vertical</div>" +
            "</body></html>");

        var upright = ColumnRun(view, "vertical");

        await view.SetHtml(
            "<html><body style='margin:0;font-size:10px;line-height:1'>" +
            "<div style='writing-mode:vertical-rl;text-orientation:upright'>" +
            "<div style='text-orientation:initial'>vertical</div>" +
            "</div>" +
            "</body></html>");

        var reset = ColumnRun(view, "vertical");

        Assert.That(
            upright > reset,
            Is.True,
            $"an upright run stands one character per box ({upright}) while the reset one lies along the column ({reset})");
    }

    /// <summary>
    /// A sideways mode turns every glyph by itself, so there is no upright run left for
    /// <c>text-combine-upright</c> to make: the property has no effect there (CSS Writing Modes 3 §5.1),
    /// and the run is set exactly as it would be without it. Measured in Chrome on wpt's
    /// <c>text-combine-upright-sideways-001</c> and <c>-002</c>, whose reference pages drop the property.
    /// </summary>
    [Test]
    public async Task CombiningUprightHasNoEffectInASidewaysMode()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0;font-size:10px;line-height:1'>" +
            "<div style='writing-mode:sideways-rl'>AB1234CD</div>" +
            "</body></html>");

        var plain = ColumnRun(view, "AB1234CD");

        await view.SetHtml(
            "<html><body style='margin:0;font-size:10px;line-height:1'>" +
            "<div style='writing-mode:sideways-rl'>AB<span style='text-combine-upright:all'>1234</span>CD</div>" +
            "</body></html>");

        var combined = Text(view, "1234").BorderBox.Height;

        Assert.That(
            combined,
            Is.EqualTo(20),
            $"the four digits keep their own advances ({plain} for the whole run) rather than being compressed into one em");
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

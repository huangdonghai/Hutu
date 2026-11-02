using Hutu.Rendering;
using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the shared interline-note flow: notes are built between body lines by the ordinary line
/// composer, retain their own inline styles, and push the body line they belong to.
/// </summary>
[TestFixture]
public sealed class InterlineNoteTests
{
    [Test]
    public async Task AnInterlineNoteStandsAboveItsBodyLineAndPushesItDown()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "ab<span style='-hutu-note-placement:interline;font-size:10px;line-height:10px'>XY</span>cd"));

        var note = Text(view, "XY");
        var before = Text(view, "ab");
        var after = Text(view, "cd");

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.Y, Is.EqualTo(0), "the note occupies the line-over side");
            Assert.That(before.BorderBox.Y, Is.EqualTo(10), "the body line is pushed below the note");
            Assert.That(after.BorderBox.Y, Is.EqualTo(10));
            Assert.That(note.BorderBox.X, Is.EqualTo(10), "the note starts at the declaration point");
        });
    }

    [Test]
    public async Task SeveralInterlineNotesShareOneFlowAndPushOnlyWhenTheyCollide()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:interline;font-size:10px;line-height:10px'>NNNN</span>" +
            "bb<span style='-hutu-note-placement:interline;font-size:10px;line-height:10px'>MMMM</span>cc"));

        var first = Text(view, "NNNN");
        var second = Text(view, "MMMM");
        var body = Text(view, "cc");

        Assert.Multiple(() =>
        {
            Assert.That(first.BorderBox.Y, Is.EqualTo(0));
            Assert.That(second.BorderBox.Y, Is.EqualTo(first.BorderBox.Y), "both notes stand in one flow line");
            Assert.That(first.BorderBox.X, Is.EqualTo(10), "the first note starts at its declaration point");
            Assert.That(second.BorderBox.X, Is.EqualTo(first.BorderBox.Right), "the colliding note follows it");
            Assert.That(body.BorderBox.Y, Is.EqualTo(10), "one note line pushes the body once");
        });
    }

    [Test]
    public async Task WhenNoBodySlotRemainsTheNoteFallsBackInsideTheLastSlot()
    {
        using var view = CreateView(40);

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:interline;font-size:10px;line-height:10px'>" +
            "甲乙丙丁戊己庚辛壬癸子丑</span>bb"));

        var body = Text(view, "bb");
        var noteRuns = Walk(view.Layout!.Root!)
            .Where(box => box.Kind == LayoutKind.Text && box.Text is { Length: > 0 } text &&
                text.All(character => "甲乙丙丁戊己庚辛壬癸子丑".Contains(character)))
            .ToList();
        Assert.Multiple(() =>
        {
            Assert.That(noteRuns, Has.Count.EqualTo(2), "the string is split over two note lines");
            Assert.That(noteRuns[0].BorderBox.Y, Is.EqualTo(0));
            Assert.That(noteRuns[1].BorderBox.Y, Is.EqualTo(10));
            Assert.That(body.BorderBox.Y, Is.EqualTo(20), "both note lines push the body");
        });
    }

    [Test]
    public async Task AnOverlongNoteContinuesInTheNextBodyLinesNoteSlot()
    {
        using var view = CreateView(40);

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:interline;font-size:10px;line-height:10px'>" +
            "甲乙丙丁戊己庚辛壬癸子丑</span>bbbbbbbbbb"));

        var noteRuns = Walk(view.Layout!.Root!)
            .Where(box => box.Kind == LayoutKind.Text &&
                box.Text is { Length: > 0 } text &&
                text.All(character => "甲乙丙丁戊己庚辛壬癸子丑".Contains(character)))
            .OrderBy(box => box.BorderBox.Y)
            .ToList();
        var bodyRuns = Walk(view.Layout!.Root!)
            .Where(box => box.Kind == LayoutKind.Text &&
                box.Text is { Length: > 0 } text &&
                text.All(character => character == 'b'))
            .OrderBy(box => box.BorderBox.Y)
            .ToList();
        var firstBody = Text(view, "aa");

        Assert.Multiple(() =>
        {
            Assert.That(noteRuns, Has.Count.EqualTo(2));
            Assert.That(bodyRuns, Has.Count.EqualTo(1));
            Assert.That(noteRuns[0].BorderBox.Y, Is.LessThan(firstBody.BorderBox.Y));
            Assert.That(firstBody.BorderBox.Y, Is.LessThan(noteRuns[1].BorderBox.Y));
            Assert.That(noteRuns[1].BorderBox.Y, Is.LessThan(bodyRuns[0].BorderBox.Y),
                "the continuation uses the next body line's interlinear slot");
        });
    }

    [Test]
    public async Task TheFirstBodyLineIndentReservesRoomFromTheNoteFlow()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:interline;font-size:10px;line-height:10px;" +
            "text-indent:100px'>NNNNNN</span>bb",
            "text-indent:30px"));

        var note = Text(view, "NNNNNN");

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.X, Is.EqualTo(40), "the note starts at the indented declaration point");
            Assert.That(note.BorderBox.X, Is.LessThan(140), "the note's own text-indent is ignored");
        });
    }

    [Test]
    public async Task BodyAlignmentDoesNotMoveAnAnchoredInterlineNote()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aaaaaaaaaa<span style='-hutu-note-placement:interline;font-size:10px;" +
            "line-height:10px'>NN</span>bb",
            "text-align:center"));

        var note = Text(view, "NN");
        var body = Text(view, "aaaaaaaaaa");

        Assert.Multiple(() =>
        {
            Assert.That(body.BorderBox.X, Is.EqualTo(70), "the body line itself is centred");
            Assert.That(note.BorderBox.X, Is.EqualTo(120), "the note remains at its declaration point");
        });
    }

    [Test]
    public async Task InterlineNoteDecorationsFollowInlineBoxRules()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:interline;font-size:10px;line-height:10px;" +
            "margin-inline-start:10px;margin-block-start:40px;padding-inline-end:5px;" +
            "border-inline-start:1px solid black'>NN</span>bb"));

        var note = Text(view, "NN");
        var body = Text(view, "bb");

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.X, Is.EqualTo(21), "the inline margin and border stand before the text");
            Assert.That(body.BorderBox.Y, Is.EqualTo(10), "the block-axis margin does not grow the note line");
        });
    }

    [Test]
    public async Task InterlineNoteUsesTheBodyWritingMode()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:20px;" +
            "writing-mode:vertical-rl;width:100px;height:100px'>" +
            "春<span style='-hutu-note-placement:interline;writing-mode:horizontal-tb;direction:rtl;" +
            "font-size:10px;line-height:10px'>注</span>夏</p></body></html>");

        var note = Text(view, "注");
        var body = Text(view, "春");

        Assert.Multiple(() =>
        {
            Assert.That(note.Flow.IsVertical, Is.True, "the note does not establish a horizontal flow of its own");
            Assert.That(note.Flow.Direction, Is.EqualTo(TextDirection.LeftToRight),
                "the note's own direction is ignored in favour of the body direction");
            Assert.That(note.BorderBox.X, Is.GreaterThan(body.BorderBox.X),
                "vertical-rl puts the note on the line-over side, to the right");
        });
    }

    [Test]
    public async Task InterlineNoteKeepsItsComputedFontSizeAndLineHeight()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:interline;font-size:20px;line-height:30px'>NN</span>bb"));

        var note = Text(view, "NN");
        var body = Text(view, "bb");

        Assert.Multiple(() =>
        {
            Assert.That(note.Font.Size, Is.EqualTo(20));
            Assert.That(body.BorderBox.Y, Is.EqualTo(25), "the note keeps the larger of the two half-leadings");
        });
    }

    [Test]
    public async Task TheTargetBodyLineSitsAtTheLargerHalfLeadingGap()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:interline;font-size:10px;line-height:30px'>NN</span>bb"));

        var note = Text(view, "NN");
        var body = Text(view, "bb");
        var noteLine = LineContaining(view, note);
        var bodyLine = LineContaining(view, body);

        Assert.Multiple(() =>
        {
            Assert.That(noteLine.BorderBox.Height, Is.EqualTo(30));
            Assert.That(bodyLine.BorderBox.Y, Is.EqualTo(25),
                "G_note=10 and G_body=5 leave the larger gap above the body line");
            Assert.That(
                bodyLine.BorderBox.Y - (noteLine.BorderBox.Y + noteLine.BorderBox.Height),
                Is.EqualTo(-5),
                "the two line boxes overlap by the smaller leading");
        });
    }

    [Test]
    public async Task AnInlineNoteOverlapsTheInterlineNoteAtItsFirstSublineGap()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:interline;font-size:10px;line-height:40px'>NN</span>" +
            "bb<span style='-hutu-note-placement:inline;font-size:10px;line-height:30px'>XY</span>cc",
            "font-size:16px;line-height:20px;"));

        var noteLine = LineContaining(view, Text(view, "NN"));
        var bodyLine = LineContaining(view, Text(view, "bb"));
        var inlineFirst = Walk(view.Layout!.Root!)
            .Where(box => box.Kind == LayoutKind.Line &&
                box.Source.Style.NotePlacement == NotePlacement.Inline &&
                Walk(box).Any(child => child.Kind == LayoutKind.Text && child.Text == "X"))
            .OrderBy(box => box.BorderBox.Y)
            .First();

        Assert.Multiple(() =>
        {
            Assert.That(noteLine.BorderBox.Bottom - inlineFirst.BorderBox.Y,
                Is.EqualTo(10).Within(0.5),
                $"the first subline boundary overlaps the interline note by min(10, 15); " +
                $"noteY={noteLine.BorderBox.Y}, noteBottom={noteLine.BorderBox.Bottom}, " +
                $"bodyY={bodyLine.BorderBox.Y}, firstY={inlineFirst.BorderBox.Y}");
            Assert.That(bodyLine.BorderBox.Y, Is.EqualTo(28).Within(0.5),
                "the body line moves by the 2px centring offset plus the 10px overlap");
        });
    }

    [Test]
    public async Task TheGapBeforeAnInterlineNoteRemainsTheOrdinaryBodyGap()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aa<br>bb<span style='-hutu-note-placement:interline;font-size:10px;line-height:30px'>NN</span>cc"));

        var firstBody = LineContaining(view, Text(view, "aa"));
        var note = LineContaining(view, Text(view, "NN"));
        var targetBody = LineContaining(view, Text(view, "cc"));

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.Y, Is.EqualTo(firstBody.BorderBox.Bottom),
                "the boundary before the note is not compressed");
            Assert.That(targetBody.BorderBox.Y, Is.EqualTo(note.BorderBox.Bottom - 5),
                "only the boundary between the note and its target line is tightened");
        });
    }

    [Test]
    public async Task NoteLinesAdvanceThroughSuccessiveBodySlots()
    {
        using var view = CreateView(40);

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:interline;font-size:10px;line-height:30px'>" +
            "甲乙丙丁戊己庚辛壬癸子丑</span>bbbbbbbbbbbbbbbbbbbb"));

        var bodyRuns = Walk(view.Layout!.Root!)
            .Where(box => box.Kind == LayoutKind.Text &&
                box.Text is { Length: > 0 } text &&
                text.All(character => character == 'b'))
            .OrderBy(box => box.BorderBox.Y)
            .ToList();
        var noteLines = Walk(view.Layout!.Root!)
            .Where(box => box.Kind == LayoutKind.Line &&
                Walk(box).Any(child => child.Kind == LayoutKind.Text &&
                    child.Text is { Length: > 0 } text &&
                    text.All(character => "甲乙丙丁戊己庚辛壬癸子丑".Contains(character))))
            .OrderBy(box => box.BorderBox.Y)
            .ToList();
        var firstBody = LineContaining(view, Text(view, "aa"));
        var secondBody = LineContaining(view, bodyRuns[0]);

        Assert.Multiple(() =>
        {
            Assert.That(noteLines, Has.Count.EqualTo(2));
            Assert.That(noteLines[0].BorderBox.Y, Is.LessThan(firstBody.BorderBox.Y));
            Assert.That(firstBody.BorderBox.Y, Is.LessThan(noteLines[1].BorderBox.Y));
            Assert.That(noteLines[1].BorderBox.Y, Is.EqualTo(firstBody.BorderBox.Bottom));
            Assert.That(secondBody.BorderBox.Y, Is.EqualTo(noteLines[1].BorderBox.Bottom - 5));
        });
    }

    [Test]
    public async Task EachInterlineNoteLineAvoidsFloatsAtItsOwnBlockPosition()
    {
        using var view = CreateView(60);

        await view.SetHtml(Page(
            "<div style='float:left;width:20px;height:5px'></div>" +
            "aa<span style='-hutu-note-placement:interline;font-size:10px;line-height:20px'>" +
            "甲乙丙丁戊己庚辛壬癸子丑</span>bb"));

        var noteRuns = Walk(view.Layout!.Root!)
            .Where(box => box.Kind == LayoutKind.Text && box.Text is { Length: > 0 } text &&
                text.All(character => "甲乙丙丁戊己庚辛壬癸子丑".Contains(character)))
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(noteRuns, Has.Count.EqualTo(2));
            Assert.That(noteRuns[0].BorderBox.X, Is.GreaterThanOrEqualTo(20), "the first note line clears the float");
            Assert.That(noteRuns[1].BorderBox.X, Is.EqualTo(0), "the float has ended for the second note line");
        });
    }

    [Test]
    public async Task AnInterlineNoteInsideAnInlineNoteIsHoistedToTheBodyFlow()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "甲<span id='outer' style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "乙<span style='-hutu-note-placement:interline;font-size:10px;line-height:10px'>注</span>丙" +
            "</span>丁"));

        var hoistedRuns = Walk(view.Layout!.Root!)
            .Where(box => box.Kind == LayoutKind.Text && box.Text == "注")
            .ToList();
        var hoistedPositions = String.Join(
            "; ",
            hoistedRuns.Select(run => $"{run.BorderBox.X},{run.BorderBox.Y}"));
        Assert.That(hoistedRuns, Has.Count.EqualTo(1), hoistedPositions);
        var hoisted = hoistedRuns[0];
        var first = Text(view, "乙");
        var second = Text(view, "丙");
        var outer = Walk(view.Layout!.Root!)
            .Single(box => box.Kind == LayoutKind.Inline && box.Source.Element?.Id == "outer");

        Assert.Multiple(() =>
        {
            Assert.That(hoisted.BorderBox.Y, Is.EqualTo(0), "the nested note is laid above the body line");
            Assert.That(first.BorderBox.Y, Is.Not.EqualTo(hoisted.BorderBox.Y));
            Assert.That(second.BorderBox.Y, Is.Not.EqualTo(first.BorderBox.Y), "the outer note keeps two sublines");
            Assert.That(Walk(outer).Any(box => ReferenceEquals(box, hoisted)), Is.False,
                "the hoisted note is not laid inside the inline note");
        });
    }

    [Test]
    public async Task BodyJustifySpreadsOnlyANoteLineThatNaturallyReachedTheEnd()
    {
        using var view = CreateView(30);

        await view.SetHtml(Page(
            "甲<span style='-hutu-note-placement:interline;font-size:10px;line-height:10px'>" +
            "aa aa aa</span>乙",
            "text-align:justify;text-align-last:justify"));

        var noteRuns = Walk(view.Layout!.Root!)
            .Where(box => box.Kind == LayoutKind.Text && box.Text is { Length: > 0 } text &&
                text.All(character => character is 'a' or ' '))
            .OrderBy(box => box.BorderBox.Y)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(noteRuns, Has.Count.EqualTo(2));
            Assert.That(noteRuns[0].BorderBox.Right, Is.EqualTo(30).Within(0.01), "the non-last note line is justified");
            Assert.That(noteRuns[1].BorderBox.Right, Is.LessThan(30), "the final note line stays ragged");
        });
    }

    [Test]
    public async Task AMarkerOnlyInlineNoteStillHoistsItsInterlineChild()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "甲<span style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "<span style='-hutu-note-placement:interline;font-size:10px;line-height:10px'>注</span>" +
            "</span>乙"));

        var note = Text(view, "注");
        var body = Text(view, "乙");

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.Y, Is.EqualTo(0));
            Assert.That(body.BorderBox.Y, Is.EqualTo(10));
        });
    }

    [Test]
    public async Task AnInterlineNoteOnALaterLineIsInsertedAtThatLine()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aa<br>bb<span style='-hutu-note-placement:interline;font-size:10px;line-height:10px'>" +
            "NN</span>cc"));

        var first = Text(view, "aa");
        var note = Text(view, "NN");
        var second = Text(view, "cc");

        Assert.Multiple(() =>
        {
            Assert.That(first.BorderBox.Y, Is.EqualTo(0));
            Assert.That(note.BorderBox.Y, Is.EqualTo(20), "the note follows the first body line");
            Assert.That(second.BorderBox.Y, Is.EqualTo(30), "only the annotated line and what follows move");
        });
    }

    [Test]
    public async Task ThePlacementOverridesBlockDisplay()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aa<span style='display:block;-hutu-note-placement:interline;font-size:10px;line-height:10px'>" +
            "NN</span>bb"));

        var note = Text(view, "NN");
        var body = Text(view, "bb");

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.Y, Is.EqualTo(0), "the block display does not make an ordinary block");
            Assert.That(body.BorderBox.Y, Is.EqualTo(10));
        });
    }

    [Test]
    public async Task AnExpandedInlineNoteOnTheTargetLineIsNotOverlapped()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:interline;font-size:10px;line-height:10px'>NN</span>" +
            "<span style='-hutu-note-placement:inline;font-size:10px;line-height:20px'>XY</span>bb"));

        var interline = Text(view, "NN");
        var inline = Text(view, "X");

        Assert.Multiple(() =>
        {
            Assert.That(interline.BorderBox.Bottom, Is.LessThanOrEqualTo(10),
                "the interline note remains outside the expanded body line");
            Assert.That(inline.BorderBox.Y, Is.GreaterThanOrEqualTo(10));
        });
    }

    [Test]
    public async Task AStructuredNoteContributesItsOwnRunMetricsToTheFlow()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:interline;font-size:10px;line-height:10px'>" +
            "甲<span style='font-size:20px;line-height:30px'>乙</span>丙</span>bb"));

        var inner = Text(view, "乙");
        var body = Text(view, "bb");

        Assert.Multiple(() =>
        {
            Assert.That(inner.Font.Size, Is.EqualTo(20));
            Assert.That(body.BorderBox.Y, Is.EqualTo(30), "the tallest run sets the note line's height");
        });
    }

    [Test]
    public async Task AnAnchorAtASoftWrapBelongsToTheLineBeforeIt()
    {
        using var view = CreateView(10);

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:interline;font-size:10px;line-height:10px'>NN</span>bb"));

        var note = Text(view, "NN");
        var first = Text(view, "aa");
        var second = Text(view, "bb");

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.Y, Is.EqualTo(0));
            Assert.That(first.BorderBox.Y, Is.EqualTo(10));
            Assert.That(second.BorderBox.Y, Is.EqualTo(30), "the body still breaks after the anchor's line");
        });
    }

    [Test]
    public async Task NoteDirectionDoesNotReverseTheSharedFlow()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aa<span id='first' style='-hutu-note-placement:interline;direction:rtl;font-size:10px;" +
            "line-height:10px'>اب</span>bb<span id='second' style='-hutu-note-placement:interline;" +
            "direction:rtl;font-size:10px;line-height:10px'>جد</span>cc"));

        var runs = Walk(view.Layout!.Root!)
            .Where(box => box.Kind == LayoutKind.Text && box.Text is "اب" or "جد")
            .OrderBy(box => box.Source.Element?.Id == "first" ? 0 : 1)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(runs, Has.Count.EqualTo(2));
            Assert.That(runs[0].BorderBox.X, Is.LessThan(runs[1].BorderBox.X));
        });
    }

    private static HutuViewInternal CreateView(Int32 width = 200)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(width, 200);
        return view;
    }

    private static String Page(String content, String rules = "") =>
        "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:20px;" +
        rules +
        "'>" +
        content +
        "</p></body></html>";

    private static LayoutBox Text(HutuViewInternal view, String text) =>
        Walk(view.Layout!.Root!)
            .Single(box => box.Kind == LayoutKind.Text && box.Text == text);

    private static LayoutBox LineContaining(HutuViewInternal view, LayoutBox target) =>
        Walk(view.Layout!.Root!)
            .First(box => box.Kind == LayoutKind.Line && Walk(box).Any(child => ReferenceEquals(child, target)));

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

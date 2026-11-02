using Hutu.Rendering;
using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>Covers page-margin notes as natural flowing blocks outside the body flow.</summary>
[TestFixture]
public sealed class MarginNoteTests
{
    [Test]
    public async Task AMarginNoteStartsAtTheRootMarginAndDeclarationLine()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:margin;-hutu-note-side:inline-start;" +
            "font-size:10px;line-height:10px'>NN</span>bb",
            "margin:0 0 0 20px"));

        var note = Text(view, "NN");
        var body = Text(view, "aa");

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.X, Is.EqualTo(0), "the note stands in the left root margin");
            Assert.That(note.BorderBox.Y, Is.EqualTo(body.BorderBox.Y),
                "the note's block start is the declaration line's block start");
        });
    }

    [Test]
    public async Task AMarginNoteUsesTheDeclarationBlocksMargin()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html style='margin:0 0 0 20px'>" +
            "<body style='margin:0 0 0 10px;font-size:10px;line-height:20px'>" +
            "<p style='margin:0 0 0 30px'>aa" +
            "<span style='-hutu-note-placement:margin;-hutu-note-side:inline-start;" +
            "font-size:10px;line-height:10px'>NN</span>bb</p></body></html>");

        var note = Walk(view.Layout!.Root!)
            .Single(box => box.Kind == LayoutKind.Block &&
                box.Source.Style.NotePlacement == NotePlacement.Margin);

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.X, Is.EqualTo(0));
            Assert.That(note.BorderBox.Width, Is.EqualTo(60),
                "the root, body and paragraph margins form one clear band");
        });
    }

    [Test]
    public async Task AnEndMarginNoteUsesTheDeclarationBlocksMargin()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html style='margin:0 20px 0 0'>" +
            "<body style='margin:0 10px 0 0;font-size:10px;line-height:20px'>" +
            "<p style='margin:0 30px 0 0'>aa" +
            "<span style='-hutu-note-placement:margin;-hutu-note-side:inline-end;" +
            "font-size:10px;line-height:10px'>NN</span>bb</p></body></html>");

        var note = Walk(view.Layout!.Root!)
            .Single(box => box.Kind == LayoutKind.Block &&
                box.Source.Style.NotePlacement == NotePlacement.Margin);

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.X, Is.EqualTo(140));
            Assert.That(note.BorderBox.Width, Is.EqualTo(60),
                "the inline-end side mirrors the start-side chain of clear margins");
        });
    }

    [Test]
    public async Task AutoUsesTheLargerRootMarginSide()
    {
        using var view = CreateView(120);

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:margin;font-size:10px;line-height:10px'>NN</span>bb",
            "margin:0 30px 0 10px"));

        var note = Text(view, "NN");
        var body = Text(view, "aa");

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.X, Is.GreaterThan(body.BorderBox.Right),
                "auto chooses the wider right root margin");
        });
    }

    [Test]
    public async Task TheNoteUsesItsOwnTextIndentAndTextAlign()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:margin;-hutu-note-side:inline-start;" +
            "font-size:10px;line-height:10px;text-indent:5px;text-align:right'>NN</span>bb",
            "margin:0 0 0 40px"));

        var note = Text(view, "NN");

        Assert.That(note.BorderBox.X, Is.EqualTo(30),
            "text-indent and right alignment use the note's own computed values");
    }

    [Test]
    public async Task NotesStackByDeclarationLineBeforeDomOrder()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:margin;-hutu-note-side:inline-start;" +
            "font-size:10px;line-height:10px'>NN</span><br>" +
            "bb<span style='-hutu-note-placement:margin;-hutu-note-side:inline-start;" +
            "font-size:10px;line-height:10px'>MM</span>",
            "margin:0 0 0 20px"));

        var first = Text(view, "NN");
        var second = Text(view, "MM");

        Assert.That(second.BorderBox.Y, Is.GreaterThanOrEqualTo(first.BorderBox.Bottom));
    }

    [Test]
    public async Task AMarginNoteInsideAnInlineNoteIsHoistedToThePageMargin()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "甲<span style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>" +
            "乙<span style='-hutu-note-placement:margin;-hutu-note-side:inline-start;" +
            "font-size:10px;line-height:10px'>注</span>丙</span>丁",
            "margin:0 0 0 20px"));

        var noteRuns = Walk(view.Layout!.Root!)
            .Where(box => box.Kind == LayoutKind.Text && box.Text == "注")
            .ToList();
        Assert.That(
            noteRuns,
            Has.Count.EqualTo(1),
            String.Join("; ", noteRuns.Select(run => $"{run.BorderBox.X},{run.BorderBox.Y}")));
        var note = noteRuns[0];
        var outer = Walk(view.Layout!.Root!)
            .Single(box => box.Kind == LayoutKind.Inline &&
                box.Source.Element?.GetAttribute("style")?.Contains("-hutu-note-placement:inline") == true);

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.X, Is.EqualTo(0), "the hoisted note uses the page margin");
            Assert.That(Walk(outer).Any(box => ReferenceEquals(box, note)), Is.False,
                "the hoisted note is not laid inside the inline note");
        });
    }

    [Test]
    public async Task AMarginNoteInsideAnInterlineNoteUsesTheSameBodyLine()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:interline;font-size:10px;line-height:10px'>" +
            "XY<span style='-hutu-note-placement:margin;-hutu-note-side:inline-start;" +
            "font-size:10px;line-height:10px'>注</span>ZZ</span>bb",
            "margin:0 0 0 20px"));

        var note = Text(view, "注");
        var body = Text(view, "aa");

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.X, Is.EqualTo(0), "the nested edge note is in the root margin");
            Assert.That(note.BorderBox.Y, Is.EqualTo(body.BorderBox.Y),
                "the edge note uses the body line carrying the interline slot");
        });
    }

    [Test]
    public async Task VerticalTextPlacesTheEdgeNoteInTheTopMargin()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html style='writing-mode:vertical-rl;margin:20px 0 0 0'>" +
            "<body style='margin:0;font-size:10px;line-height:20px'>" +
            "<p style='margin:0;width:80px;height:100px'>" +
            "aa<span style='-hutu-note-placement:margin;-hutu-note-side:inline-start;" +
            "font-size:10px;line-height:10px'>NN</span>bb</p></body></html>");

        var note = Text(view, "NN");
        var body = Text(view, "aa");

        Assert.Multiple(() =>
        {
            Assert.That(note.Flow.IsVertical, Is.True);
            Assert.That(note.BorderBox.Y, Is.LessThan(body.BorderBox.Y),
                "vertical-rl puts the note in the top margin");
        });
    }

    [Test]
    public async Task AVerticalMarginNoteUsesTheDeclarationBlocksMargin()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html style='writing-mode:vertical-rl;margin:20px 0 0 0'>" +
            "<body style='margin:0;font-size:10px;line-height:20px'>" +
            "<p style='margin:0;margin-inline-start:30px;width:80px;height:100px'>aa" +
            "<span style='-hutu-note-placement:margin;-hutu-note-side:inline-start;" +
            "font-size:10px;line-height:10px'>NN</span>bb</p></body></html>");

        var note = Walk(view.Layout!.Root!)
            .Single(box => box.Kind == LayoutKind.Block &&
                box.Source.Style.NotePlacement == NotePlacement.Margin);

        Assert.Multiple(() =>
        {
            Assert.That(note.Flow.IsVertical, Is.True);
            Assert.That(note.BorderBox.Y, Is.EqualTo(0));
            Assert.That(note.BorderBox.Height, Is.EqualTo(50),
                "the vertical page uses both the root and paragraph inline margins");
        });
    }

    [Test]
    public async Task AVerticalMarginNoteWrapsAcrossColumns()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html style='writing-mode:vertical-rl;margin-inline-start:40px;margin-inline-end:10px'>" +
            "<body style='margin:0;font-size:10px;line-height:20px'>" +
            "<p style='margin:0;width:80px;height:100px'>" +
            "aa<span style='-hutu-note-placement:margin;-hutu-note-side:inline-start;" +
            "font-size:10px;line-height:10px;margin-inline-start:8px'>甲乙丙丁戊己庚辛壬癸</span>bb</p>" +
            "</body></html>");

        var note = Walk(view.Layout!.Root!)
            .Single(box => box.Kind == LayoutKind.Block &&
                box.Source.Style.NotePlacement == NotePlacement.Margin);
        var noteRuns = Walk(note)
            .Where(box => box.Kind == LayoutKind.Text && box.Text is { Length: > 0 })
            .ToList();
        var noteLines = note.Children
            .Where(box => box.Kind == LayoutKind.Line)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.Width, Is.GreaterThan(10),
                "the narrow top margin wraps the vertical note into columns");
            Assert.That(noteRuns.Select(run => run.BorderBox.X).Distinct().Count(), Is.GreaterThan(1),
                "the note occupies more than one column");
            Assert.That(noteLines.Select(line => line.BorderBox.Y).Distinct().Count(), Is.EqualTo(1),
                "the note's inline-start margin applies to every column");
        });
    }

    [Test]
    public async Task AMarginNoteFlowsNaturallyWithoutShrinkingItsBox()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:margin;-hutu-note-side:inline-start;" +
            "font-size:10px;line-height:10px'>甲乙丙丁戊己庚辛壬癸</span>bb",
            "margin:0 0 0 20px"));

        var note = Walk(view.Layout!.Root!)
            .Single(box => box.Kind == LayoutKind.Block &&
                box.Source.Style.NotePlacement == NotePlacement.Margin);
        var noteRuns = Walk(note)
            .Where(box => box.Kind == LayoutKind.Text && box.Text is { Length: > 0 })
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.Height, Is.GreaterThan(20),
                "the narrow margin wraps the content into several natural-flow lines");
            Assert.That(noteRuns.Select(run => run.BorderBox.Y).Distinct().Count(), Is.GreaterThan(1),
                "the text runs stand on more than one line");
        });
    }

    [Test]
    public async Task NotesInsideAMarginNoteRemainOrdinaryContent()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:margin;-hutu-note-side:inline-start;" +
            "font-size:10px;line-height:10px'>XX" +
            "<span style='-hutu-note-placement:margin;-hutu-note-side:inline-end" +
            ";font-size:10px;line-height:10px'>YY</span>" +
            "<span style='-hutu-note-placement:interline;font-size:10px;line-height:10px'>ZZ</span>" +
            "<span style='-hutu-note-placement:inline;font-size:10px;line-height:10px'>WW</span>" +
            "</span>bb",
            "margin:0 0 0 40px"));

        var marginBlocks = Walk(view.Layout!.Root!)
            .Where(box => box.Kind == LayoutKind.Block &&
                box.Source.Style.NotePlacement == NotePlacement.Margin)
            .ToList();

        Assert.That(marginBlocks, Has.Count.EqualTo(1),
            "nested note definitions do not create another margin-note group");

        foreach (var text in new[] { "YY", "ZZ", "WW" })
        {
            Assert.That(Walk(marginBlocks[0]).Any(box => box.Kind == LayoutKind.Text && box.Text == text),
                Is.True,
                $"{text} is ordinary content inside the outer note");
        }
    }

    [Test]
    public async Task AMarginNoteBoxUsesTheNoteLineBlockSize()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html style='margin:0 0 0 40px'>" +
            "<body style='margin:0;font-size:0;line-height:0'>" +
            "<p style='margin:0;font-size:0;line-height:0'>" +
            "<span style='-hutu-note-placement:margin;-hutu-note-side:inline-start;" +
            "color:#f00;background:#f00;font-size:10px;line-height:10px'>X</span>" +
            "</p></body></html>");

        var note = Walk(view.Layout!.Root!)
            .Single(box => box.Kind == LayoutKind.Block &&
                box.Source.Style.NotePlacement == NotePlacement.Margin);

        Assert.That(note.BorderBox.Height, Is.EqualTo(10),
            $"note box {note.BorderBox}");
    }

    [Test]
    public async Task AMarginNoteHonorsAnExplicitInlineSize()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "aa<span style='-hutu-note-placement:margin;-hutu-note-side:inline-start;" +
            "width:12px;font-size:10px;line-height:10px'>NN</span>bb",
            "margin:0 0 0 40px"));

        var note = Walk(view.Layout!.Root!)
            .Single(box => box.Kind == LayoutKind.Block &&
                box.Source.Style.NotePlacement == NotePlacement.Margin);

        Assert.That(note.BorderBox.Width, Is.EqualTo(12));
    }

    [Test]
    public async Task AMarginNoteDoesNotMoveTheBodyLine()
    {
        using var withNote = CreateView();
        using var withoutNote = CreateView();

        await withNote.SetHtml(Page(
            "aa<span style='-hutu-note-placement:margin;-hutu-note-side:inline-start;" +
            "font-size:10px;line-height:10px'>NN</span>bb",
            "margin:0 0 0 40px"));
        await withoutNote.SetHtml(Page("aabb", "margin:0 0 0 40px"));

        var withBody = Walk(withNote.Layout!.Root!)
            .First(box => box.Kind == LayoutKind.Text &&
                box.Text is { Length: > 0 } text && text.Contains("aa", StringComparison.Ordinal));
        var withoutBody = Walk(withoutNote.Layout!.Root!)
            .First(box => box.Kind == LayoutKind.Text &&
                box.Text is { Length: > 0 } text && text.Contains("aa", StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.That(withBody.BorderBox.X, Is.EqualTo(withoutBody.BorderBox.X));
            Assert.That(withBody.BorderBox.Y, Is.EqualTo(withoutBody.BorderBox.Y));
        });
    }

    [Test]
    public async Task RootLogicalMarginsFollowTheBodyWritingMode()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html style='margin-inline-start:40px;margin-inline-end:10px'>" +
            "<body style='margin:0;font-size:10px;line-height:20px;writing-mode:vertical-rl'>" +
            "<p style='margin:0;width:80px;height:100px'>" +
            "aa<span style='-hutu-note-placement:margin;-hutu-note-side:inline-start;" +
            "font-size:10px;line-height:10px'>甲乙丙丁戊己庚辛壬癸</span>bb</p>" +
            "</body></html>");

        var note = Walk(view.Layout!.Root!)
            .Single(box => box.Kind == LayoutKind.Block &&
                box.Source.Style.NotePlacement == NotePlacement.Margin);
        var noteRuns = Walk(note)
            .Where(box => box.Kind == LayoutKind.Text && box.Text is { Length: > 0 })
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(note.BorderBox.Width, Is.GreaterThan(10),
                "the root's logical margin is read as the vertical page's top margin");
            Assert.That(noteRuns.Select(run => run.BorderBox.X).Distinct().Count(), Is.GreaterThan(1),
                $"the note wraps across vertical columns: {note.BorderBox.Width}x{note.BorderBox.Height}; " +
                $"{String.Join(" | ", noteRuns.Select(run => $"{run.BorderBox.X},{run.BorderBox.Y}"))}");
            Assert.That(view.Layout.Height, Is.EqualTo(200).Within(0.5),
                "the note band comes out of the viewport instead of extending the document below it");
        });
    }

    private static HutuViewInternal CreateView(Int32 width = 200)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(width, 200);
        return view;
    }

    private static String Page(String content, String rootRules) =>
        "<html style='writing-mode:horizontal-tb;" + rootRules + "'>" +
        "<body style='margin:0;font-size:10px;line-height:20px'>" +
        "<p style='margin:0;font-size:10px;line-height:20px'>" + content + "</p>" +
        "</body></html>";

    private static LayoutBox Text(HutuViewInternal view, String text) =>
        Walk(view.Layout!.Root!)
            .Single(box => box.Kind == LayoutKind.Text && box.Text == text);

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

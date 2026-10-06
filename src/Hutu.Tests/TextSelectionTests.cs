using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers resolving a point in a laid-out document to a character, and turning a selection back into
/// the rectangles to highlight and the text to copy.
/// </summary>
/// <remarks>
/// The fake measurer makes every character exactly five pixels wide, so the expected index, rectangle
/// and string of every case can be written down instead of being derived from the code under test.
/// </remarks>
[TestFixture]
public sealed class TextSelectionTests
{
    private const String SingleLine =
        "<html><body style='margin:0'><div>abcde fghij</div></body></html>";

    /// <summary>A box narrow enough that the text wraps after the fifth character.</summary>
    private const String TwoLines =
        "<html><body style='margin:0'><div style='width:30px'>abcde fghij</div></body></html>";

    private const String Words =
        "<html><body style='margin:0'><div style='width:200px'>ab cd</div></body></html>";

    /// <summary>Two runs on one line, so a line is wider than a single run.</summary>
    private const String InlineElements =
        "<html><body style='margin:0'><div style='width:200px'>ab <b>cd</b></div></body></html>";

    [Test]
    public async Task APointInsideARunResolvesToTheNearestCharacter()
    {
        using var document = await LoadAsync(SingleLine);

        var run = document.Selector.Runs[0];

        // The characters are five pixels wide, so the boundary between the second and the third is at
        // ten: twelve belongs to the third character and seventeen to the fourth.
        Assert.That(document.Selector.HitTest(12, Centre(run))!.Value.Index, Is.EqualTo(2));
        Assert.That(document.Selector.HitTest(17, Centre(run))!.Value.Index, Is.EqualTo(3));
        Assert.That(document.Selector.HitTest(12, Centre(run))!.Value.Box, Is.SameAs(run));
    }

    [Test]
    public async Task APointPastEitherEndOfTheLineIsClampedToIt()
    {
        using var document = await LoadAsync(SingleLine);

        var run = document.Selector.Runs[0];

        Assert.That(document.Selector.HitTest(-5, Centre(run))!.Value.Index, Is.EqualTo(0));
        Assert.That(document.Selector.HitTest(1000, Centre(run))!.Value.Index, Is.EqualTo(11));
    }

    [Test]
    public async Task EachLineOfWrappedTextResolvesToItsOwnRun()
    {
        using var document = await LoadAsync(TwoLines);

        var first = document.Selector.Runs[0];
        var second = document.Selector.Runs[1];

        Assert.That(first.Text, Is.EqualTo("abcde"));
        Assert.That(second.Text, Is.EqualTo("fghij"));
        Assert.That(second.BorderBox.Y, Is.EqualTo(first.BorderBox.Bottom));

        Assert.That(document.Selector.HitTest(12, 1)!.Value.Box, Is.SameAs(first));
        Assert.That(document.Selector.HitTest(12, second.BorderBox.Y + 1)!.Value.Box, Is.SameAs(second));
    }

    [Test]
    public async Task APointBelowTheLastLineResolvesToTheNearestOne()
    {
        using var document = await LoadAsync(TwoLines);

        var second = document.Selector.Runs[1];

        Assert.That(
            document.Selector.HitTest(12, second.BorderBox.Bottom + 2000)!.Value.Box,
            Is.SameAs(second));
    }

    [Test]
    public async Task ARangeInsideOneRunCoversTheCharactersBetweenItsBoundaries()
    {
        using var document = await LoadAsync(SingleLine);

        var run = document.Selector.Runs[0];
        var rects = document.Selector.Rects(Selection(run, 1, 4));

        Assert.That(rects, Has.Count.EqualTo(1));
        Assert.That(rects[0].X, Is.EqualTo(5));
        Assert.That(rects[0].Width, Is.EqualTo(15));
        Assert.That(rects[0].Y, Is.EqualTo(run.BorderBox.Y));
        Assert.That(rects[0].Height, Is.EqualTo(run.BorderBox.Height));
    }

    [Test]
    public async Task ARangeAcrossLinesProducesOneRectanglePerLine()
    {
        using var document = await LoadAsync(TwoLines);

        var first = document.Selector.Runs[0];
        var second = document.Selector.Runs[1];

        var rects = document.Selector.Rects(new TextSelection(
            new TextPosition(first, 3),
            new TextPosition(second, 2)));

        Assert.That(rects, Has.Count.EqualTo(2));

        // The first line is covered from the third character to its end, the second from its start.
        Assert.That(rects[0].X, Is.EqualTo(15));
        Assert.That(rects[0].Width, Is.EqualTo(first.BorderBox.Right - 15));
        Assert.That(rects[0].Y, Is.EqualTo(first.BorderBox.Y));
        Assert.That(rects[1].X, Is.EqualTo(second.BorderBox.X));
        Assert.That(rects[1].Width, Is.EqualTo(10));
        Assert.That(rects[1].Y, Is.EqualTo(second.BorderBox.Y));
    }

    [Test]
    public async Task ASelectionMadeBackwardsCoversTheSameCharacters()
    {
        using var document = await LoadAsync(TwoLines);

        var first = document.Selector.Runs[0];
        var second = document.Selector.Runs[1];

        var forward = document.Selector.Rects(new TextSelection(
            new TextPosition(first, 3),
            new TextPosition(second, 2)));

        var backward = document.Selector.Rects(new TextSelection(
            new TextPosition(second, 2),
            new TextPosition(first, 3)));

        Assert.That(
            backward.Select(rect => (rect.X, rect.Y, rect.Width, rect.Height)),
            Is.EqualTo(forward.Select(rect => (rect.X, rect.Y, rect.Width, rect.Height))));

        Assert.That(
            document.Selector.Text(new TextSelection(new TextPosition(second, 2), new TextPosition(first, 3))),
            Is.EqualTo("de\nfg"));
    }

    [Test]
    public async Task TheSelectedTextJoinsItsLinesWithOneBreakEach()
    {
        using var document = await LoadAsync(TwoLines);

        var first = document.Selector.Runs[0];
        var second = document.Selector.Runs[1];

        Assert.That(
            document.Selector.Text(new TextSelection(new TextPosition(first, 3), new TextPosition(second, 2))),
            Is.EqualTo("de\nfg"));
    }

    [Test]
    public async Task AWholeRunIsSelectedWhenItIsCoveredEntirely()
    {
        using var document = await LoadAsync(TwoLines);

        var first = document.Selector.Runs[0];
        var second = document.Selector.Runs[1];

        Assert.That(
            document.Selector.Text(new TextSelection(new TextPosition(first, 0), new TextPosition(second, 5))),
            Is.EqualTo("abcde\nfghij"));
    }

    [Test]
    public async Task AWordIsTheChunkThePositionSitsIn()
    {
        using var document = await LoadAsync(Words);

        var run = document.Selector.Runs[0];

        Assert.That(run.Text, Is.EqualTo("ab cd"));
        Assert.That(document.Selector.Text(document.Selector.Word(new TextPosition(run, 1))), Is.EqualTo("ab"));
        Assert.That(document.Selector.Text(document.Selector.Word(new TextPosition(run, 2))), Is.EqualTo(" "));
        Assert.That(document.Selector.Text(document.Selector.Word(new TextPosition(run, 4))), Is.EqualTo("cd"));

        // A click past the last character takes the word it follows rather than nothing at all.
        Assert.That(document.Selector.Text(document.Selector.Word(new TextPosition(run, 5))), Is.EqualTo("cd"));
    }

    [Test]
    public async Task ACjkWordIsASingleCharacter()
    {
        using var document = await LoadAsync(
            "<html><body style='margin:0'><div>你好世界</div></body></html>");

        var run = document.Selector.Runs[0];

        Assert.That(run.Text, Is.EqualTo("你好世界"));
        Assert.That(document.Selector.Text(document.Selector.Word(new TextPosition(run, 1))), Is.EqualTo("好"));
    }

    [Test]
    public async Task ALineIsEveryRunSharingALineBox()
    {
        using var document = await LoadAsync(InlineElements);

        var runs = document.Selector.Runs;

        Assert.That(runs, Has.Count.EqualTo(2));

        var line = document.Selector.Line(new TextPosition(runs[1], 1));

        Assert.That(line.Anchor.Box, Is.SameAs(runs[0]));
        Assert.That(line.Anchor.Index, Is.EqualTo(0));
        Assert.That(line.Caret.Box, Is.SameAs(runs[1]));
        Assert.That(line.Caret.Index, Is.EqualTo(2));
        Assert.That(document.Selector.Text(line), Is.EqualTo("ab cd"));
    }

    [Test]
    public async Task EverythingSelectableIsTheWholeDocument()
    {
        using var document = await LoadAsync(InlineElements);

        Assert.That(document.Selector.Text(document.Selector.All()), Is.EqualTo("ab cd"));
    }

    [Test]
    public async Task TextPinnedToTheViewportIsNotSelectable()
    {
        using var document = await LoadAsync(
            "<html><body style='margin:0'>" +
            "<div style='position:fixed;top:0;left:0'>fixed</div>" +
            "<div>flow</div>" +
            "</body></html>");

        Assert.That(document.Selector.Runs, Has.Count.EqualTo(1));
        Assert.That(document.Selector.Runs[0].Text, Is.EqualTo("flow"));
    }

    [Test]
    public async Task ADocumentWithoutTextHasNothingToSelect()
    {
        using var document = await LoadAsync("<html><body style='margin:0'></body></html>");

        Assert.That(document.Selector.Runs, Is.Empty);
        Assert.That(document.Selector.HitTest(10, 10), Is.Null);
        Assert.That(document.Selector.All(), Is.Null);
        Assert.That(document.Selector.Rects(null), Is.Empty);
    }

    [Test]
    public async Task APositionThatNamesNoRunSelectsNothing()
    {
        using var document = await LoadAsync(SingleLine);

        var nowhere = new TextSelection(default, default);

        Assert.That(document.Selector.Rects(nowhere), Is.Empty);
        Assert.That(document.Selector.Text(nowhere), Is.Empty);
        Assert.That(document.Selector.Line(default).IsEmpty, Is.True);
    }

    [Test]
    public async Task APointDownAColumnResolvesToTheNearestCharacter()
    {
        using var document = await LoadAsync(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:100px;height:200px;font-size:10px;line-height:1'>東京都</div></body></html>");

        var run = document.Selector.Runs[0];
        var x = run.BorderBox.X + run.BorderBox.Width / 2;
        var top = run.BorderBox.Y;

        Assert.That(run.Flow.IsVertical, Is.True);
        Assert.That(run.BorderBox.Height, Is.EqualTo(30), "three ideographs take one em each down the column");

        // Each ideograph takes a ten-pixel em box, so its boundaries fall every ten pixels down the column.
        Assert.That(document.Selector.HitTest(x, top + 4)!.Value.Index, Is.EqualTo(0));
        Assert.That(document.Selector.HitTest(x, top + 12)!.Value.Index, Is.EqualTo(1));
        Assert.That(document.Selector.HitTest(x, top + 28)!.Value.Index, Is.EqualTo(3));
        Assert.That(document.Selector.HitTest(x, top + 1000)!.Value.Index, Is.EqualTo(3));
    }

    [Test]
    public async Task ARangeDownAColumnSpansTheWidthOfTheColumn()
    {
        using var document = await LoadAsync(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:100px;height:200px;font-size:10px;line-height:1'>東京都</div></body></html>");

        var run = document.Selector.Runs[0];
        var rects = document.Selector.Rects(Selection(run, 1, 2));

        Assert.That(rects, Has.Count.EqualTo(1));
        Assert.That(rects[0].X, Is.EqualTo(run.BorderBox.X), "the highlight is as wide as the column");
        Assert.That(rects[0].Width, Is.EqualTo(run.BorderBox.Width));
        Assert.That(rects[0].Y, Is.EqualTo(run.BorderBox.Y + 10), "it starts at the second em down");
        Assert.That(rects[0].Height, Is.EqualTo(10));
    }

    [Test]
    public async Task APointIsResolvedAgainstTheColumnItSitsIn()
    {
        using var document = await LoadAsync(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:100px;height:20px;font-size:10px;line-height:1'>東東東東東</div></body></html>");

        var first = document.Selector.Runs[0];
        var second = document.Selector.Runs[1];

        Assert.That(second.BorderBox.X, Is.LessThan(first.BorderBox.X), "the next line of the column runs to the left");
        Assert.That(
            document.Selector.HitTest(second.BorderBox.X + 1, second.BorderBox.Y + 1)!.Value.Box,
            Is.SameAs(second));
    }

    private static TextSelection Selection(LayoutBox run, Int32 from, Int32 to) =>
        new(new TextPosition(run, from), new TextPosition(run, to));

    private static Double Centre(LayoutBox run) => run.BorderBox.Y + run.BorderBox.Height / 2;

    /// <summary>
    /// Lays a document out at a fixed viewport and selects over it. The view is kept alive for the
    /// length of the test because the layout it holds is what the selector reads.
    /// </summary>
    private static async Task<Document> LoadAsync(String html)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);
        await view.SetHtml(html);

        return new Document(view, new TextSelector(view.Layout!, new FakeTextMeasurer()));
    }

    private sealed class Document : IDisposable
    {
        private readonly HutuViewInternal _view;

        public Document(HutuViewInternal view, TextSelector selector)
        {
            _view = view;
            Selector = selector;
        }

        public TextSelector Selector { get; }

        public void Dispose() => _view.Dispose();
    }
}

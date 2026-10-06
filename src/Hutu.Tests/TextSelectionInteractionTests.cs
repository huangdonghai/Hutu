using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the selection as a reader drives it: a drag, a double click, a triple click, and the
/// highlight each of them puts into the paint list.
/// </summary>
/// <remarks>
/// The pointer itself belongs to the host, so every case here calls the same document-coordinate API
/// the host calls. The fake measurer makes each character exactly five pixels wide, so both the point
/// a gesture would report and the rectangle a highlight should cover can be written down as literals.
/// </remarks>
[TestFixture]
public sealed class TextSelectionInteractionTests
{
    private const String SingleLine =
        "<html><body style='margin:0'><div>abcde fghij</div></body></html>";

    /// <summary>A box narrow enough that the text wraps after the fifth character.</summary>
    private const String TwoLines =
        "<html><body style='margin:0'><div style='width:30px'>abcde fghij</div></body></html>";

    private const String Words =
        "<html><body style='margin:0'><div style='width:200px'>ab cd</div></body></html>";

    /// <summary>Two runs on one line, so selecting the line has to span both of them.</summary>
    private const String InlineElements =
        "<html><body style='margin:0'><div style='width:200px'>ab <b>cd</b></div></body></html>";

    [Test]
    public async Task ADragSelectsTheCharactersBetweenItsEnds()
    {
        using var view = await LoadAsync(SingleLine);

        var line = Centre(view, 0);

        view.BeginSelection(12, line);
        view.ExtendSelection(27, line);

        // Twelve falls on the third character and twenty-seven on the sixth, so the range covers the
        // third, fourth and fifth and stops there.
        Assert.That(view.HasSelection, Is.True);
        Assert.That(view.SelectedText, Is.EqualTo("cde"));
    }

    [Test]
    public async Task TheSameCharactersAreSelectedWhicheverWayTheDragGoes()
    {
        using var view = await LoadAsync(SingleLine);

        var line = Centre(view, 0);

        view.BeginSelection(27, line);
        view.ExtendSelection(12, line);

        Assert.That(view.SelectedText, Is.EqualTo("cde"));
    }

    [Test]
    public async Task ADragAcrossTwoLinesSelectsBothFragments()
    {
        using var view = await LoadAsync(TwoLines);

        var second = Runs(view)[1];
        var line = second.BorderBox.Y + second.BorderBox.Height / 2;

        view.BeginSelection(12, 1);
        view.ExtendSelection(12, line);

        Assert.That(view.SelectedText, Is.EqualTo("cde\nfg"));
    }

    [Test]
    public async Task TheHighlightIsPaintedBeforeTheRunItCovers()
    {
        using var view = await LoadAsync(SingleLine);

        var run = Runs(view)[0];
        var line = run.BorderBox.Y + run.BorderBox.Height / 2;

        view.BeginSelection(12, line);
        view.ExtendSelection(27, line);

        var commands = view.Paint!.Commands;
        var highlight = HighlightIndex(commands);

        Assert.That(highlight, Is.GreaterThanOrEqualTo(0), "the selection should have been highlighted");

        // The fill covers the selected characters only, and spans the whole line box so the highlight
        // reads as a band rather than as a box around the glyphs.
        Assert.That(commands[highlight].Bounds.X, Is.EqualTo(10));
        Assert.That(commands[highlight].Bounds.Width, Is.EqualTo(15));
        Assert.That(commands[highlight].Bounds.Y, Is.EqualTo(run.BorderBox.Y));
        Assert.That(commands[highlight].Bounds.Height, Is.EqualTo(run.BorderBox.Height));

        // Sitting immediately before the run is what puts the highlight over the background and under
        // the glyphs - the only order that leaves the text readable.
        Assert.That(commands[highlight + 1], Is.TypeOf<TextCommand>());
        Assert.That(((TextCommand)commands[highlight + 1]).Text, Is.EqualTo(run.Text));
    }

    [Test]
    public async Task EachLineOfASelectionGetsItsOwnHighlight()
    {
        using var view = await LoadAsync(TwoLines);

        var runs = Runs(view);
        var second = runs[1];

        view.BeginSelection(12, 1);
        view.ExtendSelection(12, second.BorderBox.Y + second.BorderBox.Height / 2);

        var highlights = view.Paint!.Commands
            .Where(command => command is FillCommand fill && fill.Color == Painter.DefaultHighlightColor)
            .ToList();

        Assert.That(highlights, Has.Count.EqualTo(2));
        Assert.That(highlights[0].Bounds.Y, Is.EqualTo(runs[0].BorderBox.Y));
        Assert.That(highlights[1].Bounds.Y, Is.EqualTo(second.BorderBox.Y));
    }

    [Test]
    public async Task APlainClickSelectsNothing()
    {
        using var view = await LoadAsync(SingleLine);

        var run = Runs(view)[0];

        view.BeginSelection(12, run.BorderBox.Y + run.BorderBox.Height / 2);

        // A press puts the caret down but covers nothing, which is what makes a click clear rather
        // than select.
        Assert.That(view.HasSelection, Is.False);
        Assert.That(view.SelectedText, Is.Empty);
        Assert.That(HighlightIndex(view.Paint!.Commands), Is.EqualTo(-1));
    }

    [Test]
    public async Task ClearingTheSelectionRemovesTheHighlight()
    {
        using var view = await LoadAsync(SingleLine);

        var line = Centre(view, 0);

        view.BeginSelection(12, line);
        view.ExtendSelection(27, line);
        Assert.That(view.HasSelection, Is.True);

        view.ClearSelection();

        Assert.That(view.Selection, Is.Null);
        Assert.That(view.SelectedText, Is.Empty);
        Assert.That(HighlightIndex(view.Paint!.Commands), Is.EqualTo(-1));
    }

    [Test]
    public async Task ADocumentWithoutTextHasNothingToSelect()
    {
        using var view = await LoadAsync("<html><body style='margin:0'></body></html>");

        view.BeginSelection(10, 10);
        view.ExtendSelection(20, 20);

        Assert.That(view.Selection, Is.Null);
        Assert.That(view.SelectedText, Is.Empty);
    }

    [Test]
    public async Task ADoubleClickSelectsTheWordUnderThePointer()
    {
        using var view = await LoadAsync(Words);

        var line = Centre(view, 0);

        view.SelectWord(7, line);
        Assert.That(view.SelectedText, Is.EqualTo("ab"));

        view.SelectWord(27, line);
        Assert.That(view.SelectedText, Is.EqualTo("cd"));
    }

    [Test]
    public async Task ATripleClickSelectsEveryRunOfTheLine()
    {
        using var view = await LoadAsync(InlineElements);

        var run = Runs(view)[1];

        view.SelectLine(27, run.BorderBox.Y + run.BorderBox.Height / 2);

        Assert.That(view.SelectedText, Is.EqualTo("ab cd"));
    }

    [Test]
    public async Task SelectAllTakesTheWholeDocument()
    {
        using var view = await LoadAsync(TwoLines);

        view.SelectAll();

        Assert.That(view.SelectedText, Is.EqualTo("abcde\nfghij"));
    }

    [Test]
    public async Task ASelectionIsAnnouncedOncePerChange()
    {
        using var view = await LoadAsync(SingleLine);

        var changes = 0;
        view.SelectionChanged += () => changes++;

        var line = Centre(view, 0);

        view.BeginSelection(12, line);
        view.BeginSelection(12, line);
        view.ExtendSelection(27, line);
        view.ClearSelection();

        // Repeating a point that puts the caret in the same place selects the same thing, so it is
        // not a change and nothing is announced for it.
        Assert.That(changes, Is.EqualTo(3));
    }

    [Test]
    public async Task ARelayoutDropsTheSelection()
    {
        using var view = await LoadAsync(SingleLine);

        var line = Centre(view, 0);

        view.BeginSelection(12, line);
        view.ExtendSelection(27, line);
        Assert.That(view.HasSelection, Is.True);

        // A position names a box of the layout it was taken from, so the selection cannot outlive that
        // layout: a resize lays out again and the highlight goes with the old boxes.
        Assert.That(view.SetViewportSize(300, 300), Is.True);

        Assert.That(view.Selection, Is.Null);
        Assert.That(view.SelectedText, Is.Empty);
        Assert.That(HighlightIndex(view.Paint!.Commands), Is.EqualTo(-1));
    }

    [Test]
    public async Task ReloadingTheDocumentDropsTheSelection()
    {
        using var view = await LoadAsync(SingleLine);

        var line = Centre(view, 0);

        view.BeginSelection(12, line);
        view.ExtendSelection(27, line);

        await view.SetHtml(SingleLine);

        Assert.That(view.SelectedText, Is.Empty);
        Assert.That(HighlightIndex(view.Paint!.Commands), Is.EqualTo(-1));
    }

    [Test]
    public async Task ASelectionChangeFromARelayoutIsAnnounced()
    {
        using var view = await LoadAsync(SingleLine);

        var line = Centre(view, 0);

        view.BeginSelection(12, line);
        view.ExtendSelection(27, line);

        var changes = 0;
        view.SelectionChanged += () => changes++;

        view.SetViewportSize(300, 300);

        // The host has to hear about the drop too, or it would keep drawing a highlight that is no
        // longer part of the paint list.
        Assert.That(changes, Is.EqualTo(1));
    }

    /// <summary>Finds the selection highlight, or -1 when nothing is highlighted.</summary>
    private static Int32 HighlightIndex(IReadOnlyList<PaintCommand> commands)
    {
        for (var index = 0; index < commands.Count; index++)
        {
            if (commands[index] is FillCommand fill && fill.Color == Painter.DefaultHighlightColor)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>The runs of the laid-out document, in document order.</summary>
    private static IReadOnlyList<LayoutBox> Runs(HutuViewInternal view) =>
        new TextSelector(view.Layout!, new FakeTextMeasurer()).Runs;

    /// <summary>The vertical middle of a run, which is where a gesture on its line lands.</summary>
    private static Double Centre(HutuViewInternal view, Int32 run)
    {
        var box = Runs(view)[run].BorderBox;

        return box.Y + box.Height / 2;
    }

    /// <summary>
    /// Lays a document out at a fixed viewport. The view is returned rather than wrapped because the
    /// layout it holds is what every call under test reads.
    /// </summary>
    private static async Task<HutuViewInternal> LoadAsync(String html)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);
        await view.SetHtml(html);

        return view;
    }
}

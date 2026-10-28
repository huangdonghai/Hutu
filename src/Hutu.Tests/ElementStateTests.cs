using Hutu.Rendering;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The states a reading gesture gives the document, asserted through what they paint and through what
/// the pointer is told to look like.
/// </summary>
/// <remarks>
/// The documents are real ones because a state only means anything once style computation has seen it:
/// marking an element as hovered is worth nothing unless the rules it selects are the ones that reach
/// the painter.
/// </remarks>
[TestFixture]
public sealed class ElementStateTests
{
    [Test]
    public async Task HoveringALinkPaintsItInItsHoverColour()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            "a { color: #0000ff } a:hover { color: #ff0000 }" +
            "</style><a href='next.htm'>link</a></body></html>");

        var run = TextRun(view, "link");

        Assert.That(PaintedColour(view, "link"), Is.EqualTo(0xFF0000FFu), "the colour it declares to begin with");

        var moved = view.PointerMoved(run.BorderBox.X + 1, run.BorderBox.Y + 1);

        Assert.That(moved.Changed, Is.True, "a state changed, so the document has to be built again");
        Assert.That(PaintedColour(view, "link"), Is.EqualTo(0xFFFF0000u), "the hover rule is what is drawn now");
    }

    [Test]
    public async Task StayingWithinAnElementChangesNothing()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            "a { color: #0000ff } a:hover { color: #ff0000 }" +
            "</style><a href='next.htm'>link</a></body></html>");

        var run = TextRun(view, "link");

        Assert.That(view.PointerMoved(run.BorderBox.X + 1, run.BorderBox.Y + 1).Changed, Is.True);

        // A pointer that has not left the element it was over changes no state, so nothing about the
        // drawing moved and the document is left alone.
        Assert.That(view.PointerMoved(run.BorderBox.X + 2, run.BorderBox.Y + 1).Changed, Is.False);
    }

    [Test]
    public async Task LeavingTheElementTakesTheHoverWithIt()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            "a { color: #0000ff } a:hover { color: #ff0000 }" +
            "</style><a href='next.htm'>link</a></body></html>");

        var run = TextRun(view, "link");

        view.PointerMoved(run.BorderBox.X + 1, run.BorderBox.Y + 1);
        Assert.That(PaintedColour(view, "link"), Is.EqualTo(0xFFFF0000u), "hovered");

        // Nothing is here: below the only line of the document, so the link is over nothing at all.
        Assert.That(view.PointerMoved(1, run.BorderBox.Bottom + 30).Changed, Is.True);
        Assert.That(PaintedColour(view, "link"), Is.EqualTo(0xFF0000FFu), "no longer hovered");

        view.PointerMoved(run.BorderBox.X + 1, run.BorderBox.Y + 1);
        Assert.That(view.PointerExited(), Is.True);
        Assert.That(PaintedColour(view, "link"), Is.EqualTo(0xFF0000FFu), "a pointer that left is over nothing");
    }

    [Test]
    public async Task HoveringADescendantHoversWhatItSitsIn()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            "div:hover { color: #008000 }" +
            "</style><div><span>text</span></div></body></html>");

        var run = TextRun(view, "text");

        Assert.That(PaintedColour(view, "text"), Is.EqualTo(0xFF000000u), "black to begin with");

        view.PointerMoved(run.BorderBox.X + 1, run.BorderBox.Y + 1);

        // The span declares nothing, so the green it inherits can only come from the div the pointer
        // is also inside - which is what proves the whole chain is marked and not just the leaf.
        Assert.That(PaintedColour(view, "text"), Is.EqualTo(0xFF008000u));
    }

    [Test]
    public async Task PressingMarksWhatTheButtonWentDownOn()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            "a { color: #0000ff } a:active { color: #ff0000 }" +
            "</style><a href='next.htm'>link</a></body></html>");

        var run = TextRun(view, "link");

        Assert.That(view.PointerPressed(run.BorderBox.X + 1, run.BorderBox.Y + 1), Is.True);
        Assert.That(PaintedColour(view, "link"), Is.EqualTo(0xFFFF0000u), "the active rule is what is drawn");

        Assert.That(view.PointerReleased(), Is.True);
        Assert.That(PaintedColour(view, "link"), Is.EqualTo(0xFF0000FFu), "letting go takes it away again");

        Assert.That(view.PointerReleased(), Is.False, "a second release has nothing left to take away");
    }

    [Test]
    public async Task ThePointerAnswersForWhatIsUnderIt()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='cursor:pointer'><span>hand</span></div>" +
            "<div>plain</div>" +
            "</body></html>");

        var hand = TextRun(view, "hand");
        var plain = TextRun(view, "plain");

        Assert.That(
            view.PointerMoved(hand.BorderBox.X + 1, hand.BorderBox.Y + 1).Cursor,
            Is.EqualTo(CursorKind.Pointer),
            "an inherited declaration reaches the text inside it");

        Assert.That(
            view.PointerMoved(plain.BorderBox.X + 1, plain.BorderBox.Y + 1).Cursor,
            Is.EqualTo(CursorKind.Text),
            "nothing declared, so text asks for the text pointer");

        Assert.That(
            view.PointerMoved(1, plain.BorderBox.Bottom + 30).Cursor,
            Is.EqualTo(CursorKind.Default),
            "nothing is here, so the ordinary arrow stands");
    }

    [Test]
    public async Task ALinkAsksForTheHandOverAllOfItself()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='font-size:20px'><a href='next.htm'>plain link</a></div>" +
            "<div style='font-size:20px'><a href='next.htm'><span>wrapped</span></a></div>" +
            "</body></html>");

        var plain = TextRun(view, "plain link");
        var wrapped = TextRun(view, "wrapped");

        Assert.That(
            view.PointerMoved(plain.BorderBox.X + 1, plain.BorderBox.Y + 1).Cursor,
            Is.EqualTo(CursorKind.Pointer),
            "a link the point is on itself asks for the hand");

        Assert.That(
            view.PointerMoved(wrapped.BorderBox.X + 1, wrapped.BorderBox.Y + 1).Cursor,
            Is.EqualTo(CursorKind.Pointer),
            "and so do the words it wraps, which are as much the link as the anchor box is");
    }

    [Test]
    public async Task TextSetDownAColumnAsksForATurnedPointer()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='writing-mode:vertical-rl;width:40px;height:40px;font-size:10px;line-height:1'>中</div>" +
            "<div>line</div>" +
            "</body></html>");

        var column = TextRun(view, "中");
        var line = TextRun(view, "line");

        Assert.That(
            view.PointerMoved(column.BorderBox.X + 1, column.BorderBox.Y + 1).Cursor,
            Is.EqualTo(CursorKind.VerticalText),
            "a column is read with an I-beam turned on its side");

        Assert.That(
            view.PointerMoved(line.BorderBox.X + 1, line.BorderBox.Y + 1).Cursor,
            Is.EqualTo(CursorKind.Text),
            "while a line keeps the upright one");
    }

    [Test]
    public async Task AskingWhatAPointAsksForIsNotMovingToIt()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='writing-mode:vertical-rl;width:40px;height:40px;font-size:10px;line-height:1'>中</div>" +
            "<div style='cursor:pointer'>line</div>" +
            "</body></html>");

        var column = TextRun(view, "中");
        var line = TextRun(view, "line");

        Assert.That(
            view.CursorAt(column.BorderBox.X + 1, column.BorderBox.Y + 1),
            Is.EqualTo(CursorKind.VerticalText),
            "a column asks for the I-beam turned on its side");

        Assert.That(
            view.CursorAt(line.BorderBox.X + 1, line.BorderBox.Y + 1),
            Is.EqualTo(CursorKind.Pointer),
            "a box that declares a pointer is answered for with it, whatever lies under the point");

        Assert.That(
            view.CursorAt(1, line.BorderBox.Bottom + 30),
            Is.EqualTo(CursorKind.Default),
            "and a point with nothing under it asks for the ordinary arrow");

        Assert.That(
            view.PointerMoved(column.BorderBox.X + 1, column.BorderBox.Y + 1).Changed,
            Is.False,
            "no :hover rule mentions the pointer, so marking it rebuilds nothing");

        _ = view.CursorAt(line.BorderBox.X + 1, line.BorderBox.Y + 1);

        Assert.That(
            view.PointerMoved(column.BorderBox.X + 1, column.BorderBox.Y + 1).Changed,
            Is.False,
            "and asking about a point elsewhere did not move it");
    }

    [Test]
    public async Task TheNearestTitleIsTheHint()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div title='outer'><span>first</span><span title='inner'>second</span></div>" +
            "<div><span>bare</span></div>" +
            "</body></html>");

        var first = TextRun(view, "first");
        var second = TextRun(view, "second");
        var bare = TextRun(view, "bare");

        Assert.That(
            view.PointerMoved(first.BorderBox.X + 1, first.BorderBox.Y + 1).Hint,
            Is.EqualTo("outer"),
            "a child that names none shows the one it sits in");

        Assert.That(
            view.PointerMoved(second.BorderBox.X + 1, second.BorderBox.Y + 1).Hint,
            Is.EqualTo("inner"),
            "and names its own where it has one");

        Assert.That(
            view.PointerMoved(bare.BorderBox.X + 1, bare.BorderBox.Y + 1).Hint,
            Is.Null,
            "nothing here names one at all");
    }

    [Test]
    public async Task AHoverRelayoutKeepsTheSelection()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            "span:hover { color: #ff0000 }" +
            "</style><p style='margin:0'>hello world</p><div><span>other</span></div></body></html>");

        var paragraph = TextRun(view, "hello world");
        var other = TextRun(view, "other");

        view.BeginSelection(paragraph.BorderBox.X, paragraph.BorderBox.Y + 1);
        view.ExtendSelection(paragraph.BorderBox.Right - 1, paragraph.BorderBox.Y + 1);

        Assert.That(view.SelectedText, Is.EqualTo("hello world"));

        // Hovering rewrites the styles, which replaces every box the selection was made against - and
        // the selection has to survive that, or a reader would lose it the moment the pointer moved.
        Assert.That(view.PointerMoved(other.BorderBox.X + 1, other.BorderBox.Y + 1).Changed, Is.True);

        Assert.That(view.HasSelection, Is.True, "the selection is still there");
        Assert.That(view.SelectedText, Is.EqualTo("hello world"), "and still names the same text");
        Assert.That(
            view.Paint!.Commands.OfType<FillCommand>().Any(command => command.Color == Painter.DefaultHighlightColor),
            Is.True,
            "the highlight is drawn again against the boxes that replaced the ones it was measured on");
    }

    [Test]
    public async Task ALinkThePageNeverColouredIsPaintedInTheEngineColour()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><a href='next.htm'>next</a></body></html>");

        var run = TextRun(view, "next");

        Assert.That(PaintedColour(view, "next"), Is.EqualTo(0xFF0000EEu), "a link nothing coloured is blue");

        // The colour is the engine's own rather than the page's, so it still has to answer to the link
        // being followed - a state no rule in this document was written about.
        Assert.That(view.PointerPressed(run.BorderBox.X + 1, run.BorderBox.Y + 1), Is.True);
        Assert.That(PaintedColour(view, "next"), Is.EqualTo(0xFFEE0000u), "and red while it is being followed");
    }

    [Test]
    public async Task ALinkToAnAddressThatHasBeenReadIsPaintedAsRead()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><a href='next.htm'>next</a></body></html>",
            "index.htm");

        Assert.That(PaintedColour(view, "next"), Is.EqualTo(0xFF0000EEu), "nothing has been read yet");

        // Where the reader has been is the host's to say, and saying it is what draws the page again.
        view.IsVisited = _ => true;

        Assert.That(view.RefreshVisited(), Is.True, "the mark moved, so the document has to be built again");
        Assert.That(PaintedColour(view, "next"), Is.EqualTo(0xFF551A8Bu), "and the link is painted as read");
        Assert.That(view.RefreshVisited(), Is.False, "marking the same address again changes nothing");
    }

    [Test]
    public async Task AnAnchorThatNamesThePageItStandsInIsToldAsRead()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><style>a:visited{color:#008000}</style>" +
            "<a href=''>here</a><a href='#top'>there</a><a>plain</a></body></html>",
            "index.htm");

        // An href with nothing in it names the document it is written in, and a reader who is reading
        // that document has been to it - which is the whole of what an href="" link can be told. The
        // press has nowhere to go, but the state is not about the press.
        Assert.That(PaintedColour(view, "here"), Is.EqualTo(0xFF0000EEu), "nothing has been read yet");

        view.IsVisited = url => url.EndsWith("index.htm", StringComparison.Ordinal);

        Assert.That(
            view.RefreshVisited(),
            Is.True,
            "the page names itself, so the address the host has been to is the one the link carries");
        Assert.That(PaintedColour(view, "here"), Is.EqualTo(0xFF008000u), "and the visited rule is what is drawn");
        Assert.That(
            PaintedColour(view, "there"),
            Is.EqualTo(0xFF008000u),
            "a name inside this page is this page, read like the rest of it");
        Assert.That(
            PaintedColour(view, "plain"),
            Is.EqualTo(0xFF000000u),
            "an anchor with no href names no address, so nothing has been read to it");
    }

    [Test]
    public async Task ALinkThatHasBeenReadIsNoLongerOneThatHasNot()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><style>a:link{color:#ff00ff}</style>" +
            "<a href=''>here</a></body></html>",
            "index.htm");

        Assert.That(PaintedColour(view, "here"), Is.EqualTo(0xFFFF00FFu), "unread, so the :link rule is drawn");

        view.IsVisited = _ => true;
        view.RefreshVisited();

        // The two states are mutually exclusive: a rule written for a link that has not been read is
        // not a rule about this element any more, and what is left is the engine's own colour for a
        // link the reader has been to.
        Assert.That(
            PaintedColour(view, "here"),
            Is.EqualTo(0xFF551A8Bu),
            "read, so the :link rule is off it and the read colour is on");
    }

    [Test]
    public async Task TheWordsInsideALinkArePaintedWithTheLink()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<a href='next.htm'><span>inside</span><b style='color:#008000'>own</b></a>" +
            "</body></html>");

        Assert.That(
            PaintedColour(view, "inside"),
            Is.EqualTo(0xFF0000EEu),
            "a link colours what is set inside it, declaration or not");

        Assert.That(
            PaintedColour(view, "own"),
            Is.EqualTo(0xFF008000u),
            "while a colour declared in there is still the one that is drawn");
    }

    [Test]
    public async Task ALinkAsksForTheHandUntilThePageSaysOtherwise()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<a href='next.htm'>next</a><a href='more.htm' style='cursor:text'>more</a>" +
            "</body></html>");

        var next = TextRun(view, "next");
        var more = TextRun(view, "more");

        Assert.That(
            view.PointerMoved(next.BorderBox.X + 1, next.BorderBox.Y + 1).Cursor,
            Is.EqualTo(CursorKind.Pointer),
            "a link the page said nothing about asks for the hand by itself");

        Assert.That(
            view.PointerMoved(more.BorderBox.X + 1, more.BorderBox.Y + 1).Cursor,
            Is.EqualTo(CursorKind.Text),
            "and a cursor the page wrote on the link is what is asked for instead");
    }

    /// <summary>
    /// <c>:default</c> names the one element a group offers when nothing was chosen, and a form offers
    /// exactly one submit button: the first in its tree, wherever it stands and even when it is disabled
    /// (Selectors 4 §8.1 with HTML §4.10.13). A checkbox or a radio box is default by its own
    /// <c>checked</c> attribute and a reset or plain button never submits a form, so none of them ever is
    /// the form's default. Measured in Chrome over the gecko 348809 family, which draws each of these
    /// answers as a colour.
    /// </summary>
    [Test]
    public async Task OnlyTheFirstSubmitButtonOfAFormIsItsDefault()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body><form>" +
            "<input id='a' type='submit'><input id='b' type='submit'>" +
            "<input id='c' type='checkbox' checked><input id='d' type='checkbox'>" +
            "<input id='e' checked>" +
            "<button id='f' type='reset'></button><button id='g'></button>" +
            "<select><option id='h' selected></option><option id='i'></option></select>" +
            "<fieldset><input id='j' type='image'></fieldset>" +
            "</form></body></html>");

        var matched = view.Document!.QuerySelectorAll(":default");
        var ids = new List<String>();

        foreach (var element in matched)
        {
            ids.Add(element.Id);
        }

        Assert.That(ids, Is.EqualTo(new[] { "a", "c", "h" }),
            "the first button that submits the form, the box marked checked, and the option marked selected");
    }

    /// <summary>
    /// The rule reaches the painter the same way: a second submit button is left out of an arm written for
    /// <c>:default</c>, so the text that follows it keeps the colour the plain rule gives it.
    /// </summary>
    [Test]
    public async Task ASecondSubmitButtonDoesNotWearTheDefaultColour()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            "span { color: #ff0000 } :default + span { color: #00ff00 }" +
            "input { display: none }" +
            "</style><form>" +
            "<input type='submit'><span>first</span>" +
            "<input type='submit'><span>second</span>" +
            "</form></body></html>");

        Assert.Multiple(() =>
        {
            Assert.That(PaintedColour(view, "first"), Is.EqualTo(0xFF00FF00u), "the button the form submits by default");
            Assert.That(PaintedColour(view, "second"), Is.EqualTo(0xFFFF0000u), "and the one behind it is nothing special");
        });
    }

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        return view;
    }

    /// <summary>Gets the run of text a document set down, which is the box a point on it lands in.</summary>
    private static LayoutBox TextRun(HutuViewInternal view, String text) =>
        Walk(view.Layout!.Root).First(box => box.Kind == LayoutKind.Text && box.Text == text);

    /// <summary>Gets the colour the painter draws a run of text in.</summary>
    private static UInt32 PaintedColour(HutuViewInternal view, String text) =>
        view.Paint!.Commands.OfType<TextCommand>().First(command => command.Text == text).Color;

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

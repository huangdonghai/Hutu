using Hutu.Rendering;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// A hover or target mark that no selector mentions moves marks but changes no style, so the
/// document - and the paint list drawn from it - is left alone. A mark a selector does mention
/// still rebuilds, however it is written.
/// </summary>
[TestFixture]
public sealed class PseudoShortCircuitTests
{
    [Test]
    public async Task HoverAcrossElementsWithoutHoverRulesChangesNothing()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            "div { color: #000000 }" +
            "</style><div>aaa</div><div>bbb</div></body></html>");

        var first = TextRun(view, "aaa");
        var second = TextRun(view, "bbb");
        var paint = view.Paint;

        Assert.That(
            view.PointerMoved(first.BorderBox.X + 1, first.BorderBox.Y + 1).Changed,
            Is.False,
            "no :hover rule mentions the pointer, so marking it rebuilds nothing");
        Assert.That(
            view.PointerMoved(second.BorderBox.X + 1, second.BorderBox.Y + 1).Changed,
            Is.False,
            "moving on to the next element neither");
        Assert.That(view.Paint, Is.SameAs(paint), "so the paint list is the very same object");
        Assert.That(
            view.PointerMoved(second.BorderBox.X + 1, second.BorderBox.Y + 1).Cursor,
            Is.EqualTo(CursorKind.Text),
            "while the cursor is still answered from the hit in hand");
    }

    [Test]
    public async Task HoverWithHoverRuleStillRebuilds()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            "div:hover { color: #ff0000 }" +
            "</style><div>aaa</div></body></html>");

        var run = TextRun(view, "aaa");

        Assert.That(
            view.PointerMoved(run.BorderBox.X + 1, run.BorderBox.Y + 1).Changed,
            Is.True,
            "a :hover rule is what the mark is for");
        Assert.That(PaintedColour(view, "aaa"), Is.EqualTo(0xFFFF0000u));
    }

    [Test]
    public async Task NotHoverCountsAsHover()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            "div:not(:hover) { color: #ff0000 }" +
            "</style><div>aaa</div></body></html>");

        var run = TextRun(view, "aaa");

        Assert.That(PaintedColour(view, "aaa"), Is.EqualTo(0xFFFF0000u), "matched while unhovered");

        Assert.That(
            view.PointerMoved(run.BorderBox.X + 1, run.BorderBox.Y + 1).Changed,
            Is.True,
            "a hovered element stops matching :not(:hover), so the functional contents count");
        Assert.That(PaintedColour(view, "aaa"), Is.EqualTo(0xFF000000u));
    }

    [Test]
    public async Task HoverRuleInsideMediaQueryStillRebuilds()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            "@media (min-width: 1px) { div:hover { color: #ff0000 } }" +
            "</style><div>aaa</div></body></html>");

        var run = TextRun(view, "aaa");

        Assert.That(
            view.PointerMoved(run.BorderBox.X + 1, run.BorderBox.Y + 1).Changed,
            Is.True,
            "media queries are scanned, never evaluated: a missed one would skip a needed build");
        Assert.That(PaintedColour(view, "aaa"), Is.EqualTo(0xFFFF0000u));
    }

    [Test]
    public async Task UppercaseHoverStillRebuilds()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            "DIV:HOVER { color: #ff0000 }" +
            "</style><div>aaa</div></body></html>");

        var run = TextRun(view, "aaa");

        Assert.That(
            view.PointerMoved(run.BorderBox.X + 1, run.BorderBox.Y + 1).Changed,
            Is.True,
            "pseudo-classes match ASCII case-insensitively");
    }

    [Test]
    public async Task ClassNamedHoverDoesNotRebuild()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            ".hover { color: #ff0000 }" +
            "</style><div class='hover'>aaa</div><div>bbb</div></body></html>");

        var first = TextRun(view, "aaa");
        var second = TextRun(view, "bbb");

        Assert.That(PaintedColour(view, "aaa"), Is.EqualTo(0xFFFF0000u), "the class rule applies");

        Assert.That(
            view.PointerMoved(first.BorderBox.X + 1, first.BorderBox.Y + 1).Changed,
            Is.False,
            "a class called hover is not the pseudo-class: no colon, no match");
        Assert.That(
            view.PointerMoved(second.BorderBox.X + 1, second.BorderBox.Y + 1).Changed,
            Is.False);
    }

    [Test]
    public async Task AttributeValueMentioningHoverStillRebuilds()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            "[data-x=':hover'] { color: #ff0000 }" +
            "</style><div data-x=':hover'>aaa</div></body></html>");

        var run = TextRun(view, "aaa");

        Assert.That(PaintedColour(view, "aaa"), Is.EqualTo(0xFFFF0000u), "the attribute rule applies");

        Assert.That(
            view.PointerMoved(run.BorderBox.X + 1, run.BorderBox.Y + 1).Changed,
            Is.True,
            "a mention inside an attribute string cannot be told apart cheaply, so it rebuilds");
    }

    [Test]
    public async Task EscapedClassNamedHoverDoesNotRebuild()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            ".\\:hover { color: #ff0000 }" +
            "</style><div class=':hover'>aaa</div><div>bbb</div></body></html>");

        var first = TextRun(view, "aaa");
        var second = TextRun(view, "bbb");

        Assert.That(PaintedColour(view, "aaa"), Is.EqualTo(0xFFFF0000u), "the escaped class rule applies");

        Assert.That(
            view.PointerMoved(first.BorderBox.X + 1, first.BorderBox.Y + 1).Changed,
            Is.False,
            "a backslash before the colon means this is a class, not the pseudo-class");
        Assert.That(
            view.PointerMoved(second.BorderBox.X + 1, second.BorderBox.Y + 1).Changed,
            Is.False);
    }

    [Test]
    public async Task TargetWithoutTargetRuleDoesNotRebuild()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<a href='#there'>go</a><div id='there'>aaa</div>" +
            "</body></html>");

        var link = TextRun(view, "go");
        var paint = view.Paint;
        var target = view.LinkAt(link.BorderBox.X + 1, link.BorderBox.Y + 1);

        Assert.That(target, Is.Not.Null, "there is a link to follow");
        Assert.That(view.Activate(target.Value), Is.EqualTo("there"), "and it names the anchor");

        Assert.That(
            view.Paint, Is.SameAs(paint),
            "no :target rule mentions the mark, so marking it rebuilds nothing");
    }

    [Test]
    public async Task TargetWithTargetRuleStillRebuilds()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            ":target { color: #ff0000 }" +
            "</style><a href='#there'>go</a><div id='there'>aaa</div></body></html>");

        var link = TextRun(view, "go");
        var paint = view.Paint;
        var target = view.LinkAt(link.BorderBox.X + 1, link.BorderBox.Y + 1);

        Assert.That(target, Is.Not.Null, "there is a link to follow");

        view.Activate(target.Value);

        Assert.That(view.Paint, Is.Not.SameAs(paint), "a :target rule is what the mark is for");
        Assert.That(PaintedColour(view, "aaa"), Is.EqualTo(0xFFFF0000u));
    }

    [Test]
    public async Task PressOnPlainTextWithoutActiveRuleChangesNothing()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            "div { color: #000000 }" +
            "</style><div>aaa</div></body></html>");

        var run = TextRun(view, "aaa");
        var paint = view.Paint;

        Assert.That(
            view.PointerPressed(run.BorderBox.X + 1, run.BorderBox.Y + 1),
            Is.False,
            "no :active rule and no link under the button: matching is untouched and the " +
            "engine's link colour has no link to colour");
        Assert.That(view.PointerReleased(), Is.False);
        Assert.That(view.Paint, Is.SameAs(paint), "so the paint list is the very same object");
    }

    [Test]
    public async Task PressOnLinkWithoutActiveRuleStillRebuilds()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><a href='next.htm'>next</a></body></html>");

        var run = TextRun(view, "next");

        Assert.That(
            view.PointerPressed(run.BorderBox.X + 1, run.BorderBox.Y + 1),
            Is.True,
            "the engine colours a followed link red itself, whatever the sheets say");
        Assert.That(PaintedColour(view, "next"), Is.EqualTo(0xFFEE0000u));

        Assert.That(view.PointerReleased(), Is.True, "letting go takes the red back");
        Assert.That(PaintedColour(view, "next"), Is.EqualTo(0xFF0000EEu));
    }

    [Test]
    public async Task PressWithActiveRuleStillRebuilds()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            "div:active { color: #ff0000 }" +
            "</style><div>aaa</div></body></html>");

        var run = TextRun(view, "aaa");

        Assert.That(
            view.PointerPressed(run.BorderBox.X + 1, run.BorderBox.Y + 1),
            Is.True,
            "an :active rule is what the mark is for");
        Assert.That(PaintedColour(view, "aaa"), Is.EqualTo(0xFFFF0000u));

        Assert.That(view.PointerReleased(), Is.True);
        Assert.That(PaintedColour(view, "aaa"), Is.EqualTo(0xFF000000u));
    }

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        return view;
    }

    /// <summary>Gets the run of text a document set down, which is the box a point on it lands in.</summary>
    private static LayoutBox TextRun(HutuViewInternal view, string text) =>
        Walk(view.Layout!.Root).First(box => box.Kind == LayoutKind.Text && box.Text == text);

    /// <summary>Gets the colour the painter draws a run of text in.</summary>
    private static uint PaintedColour(HutuViewInternal view, string text) =>
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

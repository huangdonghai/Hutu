using AngleSharp.Dom;
using Hutu.Rendering;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// A build takes a style from the one it already has when nothing the style is computed from has
/// moved.
/// </summary>
/// <remarks>
/// What a style is computed from is four things: the element's own match, the values it inherits,
/// the scope its <c>var()</c> references are read through, and the two marks the engine answers to
/// off the element itself. Every case below moves one of them and asserts the picture that follows,
/// because a style stood down when it should not have been is a box drawn from a style it no longer
/// has - and the two marks are the ones no rule in the document needs to mention for that to show.
/// </remarks>
[TestFixture]
public sealed class StyleReuseTests
{
    [Test]
    public async Task ARebuildThatMovesNothingKeepsThePicture()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(Page());

        var stood = Snapshot(view);

        // Nothing moved at all, so every style stands down and the picture is the one already drawn.
        view.SetViewportSize(400, 600);
        view.RefreshVisited();

        Assert.That(Snapshot(view).SameAs(stood), Is.True, "the picture is the one it was");
    }

    [Test]
    public async Task TheRootsFontSizeStillReachesRem()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>html { font-size: 20px } .box { width: 10rem }</style></head>" +
            "<body style='margin:0'><div class='box'>text</div></body></html>");

        Assert.That(WidthOf(view), Is.EqualTo(200).Within(0.001), "a rem is a share of the root's size");

        // The root's own style stands down on the second build, and it is the build that hands the
        // root's size to the device - so it has to be handed over from the style that stood.
        view.SetViewportSize(400, 600);
        view.RefreshVisited();

        Assert.That(WidthOf(view), Is.EqualTo(200).Within(0.001), "and the share is still the root's");
    }

    [Test]
    public async Task ALinkTheEngineColoursAnswersToTheVisitedMark()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><body style='margin:0'><a href='next.htm'>text</a></body></html>");

        Assert.That(PaintedColour(view, "text"), Is.EqualTo(0xFF0000EEu), "blue to begin with");

        // No rule in this document mentions :visited, so the match does not move - and the colour is
        // the engine's own, read off the element rather than through the cascade.
        view.IsVisited = _ => true;

        Assert.That(view.RefreshVisited(), Is.True);
        Assert.That(PaintedColour(view, "text"), Is.EqualTo(0xFF551A8Bu), "and the link is painted as read");
    }

    [Test]
    public async Task ALinkTheEngineColoursAnswersToThePressMark()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><body style='margin:0'><a href='next.htm'>text</a></body></html>");

        var run = TextRun(view, "text");

        Assert.That(view.PointerPressed(run.BorderBox.X + 1, run.BorderBox.Y + 1), Is.True);
        Assert.That(PaintedColour(view, "text"), Is.EqualTo(0xFFEE0000u), "red while it is being followed");

        Assert.That(view.PointerReleased(), Is.True);
        Assert.That(PaintedColour(view, "text"), Is.EqualTo(0xFF0000EEu), "and blue again once it is let go");
    }

    [Test]
    public async Task AVariableThatMovesIsReadAgain()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            ".box { --tint: #111111 } .box:hover { --tint: #ff0000 } .note { color: var(--tint) }" +
            "</style><div class='box'><p class='note'>text</p></div></body></html>");

        Assert.That(PaintedColour(view, "text"), Is.EqualTo(0xFF111111u));

        var box = Block(view, "div");

        // The variable is declared on the box the pointer moved onto, so the scope the note reads
        // through is another one - and the note's own match never moved.
        view.PointerMoved(box.BorderBox.X + 1, box.BorderBox.Y + 1);

        Assert.That(PaintedColour(view, "text"), Is.EqualTo(0xFFFF0000u), "the note takes the new tint");

        view.PointerMoved(1, box.BorderBox.Bottom + 30);

        Assert.That(PaintedColour(view, "text"), Is.EqualTo(0xFF111111u), "and the old one back");
    }

    [Test]
    public async Task AResizeForgetsEveryStyle()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>html { font-size: 20px } .box { width: 50%; height: 4rem }</style></head>" +
            "<body style='margin:0'><div class='box'>text</div></body></html>");

        Assert.That(view.SetViewportSize(300, 600), Is.True);

        using var fresh = new HutuViewInternal(new FakeTextMeasurer());
        fresh.SetViewportSize(300, 600);
        await fresh.SetHtml(
            "<html><head><style>html { font-size: 20px } .box { width: 50%; height: 4rem }</style></head>" +
            "<body style='margin:0'><div class='box'>text</div></body></html>");

        Assert.That(WidthOf(view), Is.EqualTo(WidthOf(fresh)).Within(0.001), "the percentage is resolved again");
        Assert.That(
            TextRun(view, "text").BorderBox.Height,
            Is.EqualTo(TextRun(fresh, "text").BorderBox.Height).Within(0.001),
            "and so is the rem");
    }

    [Test]
    public async Task ADifferentDocumentNeverSeesTheLastOnesStyles()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>.box { color: #111111 }</style></head>" +
            "<body style='margin:0'><div class='box'>text</div></body></html>");

        await view.SetHtml(
            "<html><head><style>.box { color: #222222 }</style></head>" +
            "<body style='margin:0'><div class='box'>text</div></body></html>");

        Assert.That(PaintedColour(view, "text"), Is.EqualTo(0xFF222222u), "the second document's own colour");
    }

    [Test]
    public async Task HitTestingSurvivesARestyleInPlace()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            ".box { color: #111111 } .box:hover { color: #ff0000 }" +
            "</style><p class='box' style='margin:0'>first</p>" +
            "<p class='box' style='margin:0'>second</p></body></html>");

        var first = TextRun(view, "first");
        var second = TextRun(view, "second");

        view.PointerMoved(first.BorderBox.X + 1, first.BorderBox.Y + 1);
        Assert.That(PaintedColour(view, "first"), Is.EqualTo(0xFFFF0000u), "the first is hovered");

        // The rebuild that just ran kept the layout it had, so the hit tester answers against the
        // boxes still on screen. Were it stale, the move below would find nothing to hover.
        view.PointerMoved(second.BorderBox.X + 1, second.BorderBox.Y + 1);

        Assert.That(PaintedColour(view, "first"), Is.EqualTo(0xFF111111u), "and the first is not any more");
        Assert.That(PaintedColour(view, "second"), Is.EqualTo(0xFFFF0000u), "while the second is");
    }

    /// <summary>What the view is currently showing, in the terms that would catch a wrong style.</summary>
    private sealed class Picture
    {
        public Double Width { get; init; }
        public Double Height { get; init; }
        public List<String> Runs { get; init; } = new();

        public Boolean SameAs(Picture? other) =>
            other is not null &&
            Math.Abs(Width - other.Width) < 0.001 &&
            Math.Abs(Height - other.Height) < 0.001 &&
            Runs.Count == other.Runs.Count &&
            Runs.Zip(other.Runs).All(pair => pair.First == pair.Second);
    }

    private static Picture Snapshot(HutuViewInternal view)
    {
        var runs = new List<String>();

        foreach (var command in view.Paint!.Commands.OfType<TextCommand>())
        {
            runs.Add($"{command.Text}|{command.Color:X8}|{command.Baseline:0.###}");
        }

        return new Picture
        {
            Width = view.Layout!.Width,
            Height = view.Layout!.Height,
            Runs = runs,
        };
    }

    private static String Page() =>
        "<html><head><style>" +
        ".box { width: 60%; padding: 4px; color: #123456 } .note { font-size: 14px; color: #654321 }" +
        "</style></head><body style='margin:0'>" +
        "<div class='box'><p class='note'>one</p><p class='note'>two</p><span>three</span></div>" +
        "<div class='box'><p class='note'>four</p></div></body></html>";

    private static Double WidthOf(HutuViewInternal view) =>
        Walk(view.Layout!.Root).First(box => box.Source.Element?.GetAttribute("class") == "box").BorderBox.Width;

    private static LayoutBox TextRun(HutuViewInternal view, String text) =>
        Walk(view.Layout!.Root).First(box => box.Kind == LayoutKind.Text && box.Text == text);

    private static LayoutBox Block(HutuViewInternal view, String tag) =>
        Walk(view.Layout!.Root).First(box => box.Source.Element?.LocalName == tag);

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

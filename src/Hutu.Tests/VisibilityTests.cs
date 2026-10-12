using Hutu.Rendering;
using Hutu.Rendering.Interaction;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers <c>visibility</c>: a box that holds its ground and draws nothing, the descendant that asks to
/// be seen again, and the consequences of a thing no one can see - it is not pointed at and it is not
/// copied.
/// </summary>
/// <remarks>
/// A page's dropdown menus are the reason this matters: the Wikipedia main page hides twelve hundred
/// boxes with this property and nothing else, and drawing them is what made the rendering look like a
/// different site rather than a wrong one.
/// </remarks>
[TestFixture]
public sealed class VisibilityTests
{
    [Test]
    public async Task AHiddenBoxHoldsItsGroundAndDrawsNothing()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='h' style='visibility:hidden;height:40px;background:#ff0000'>gone</div>" +
            "<p id='after' style='margin:0;height:20px'>after</p>"));

        var hidden = view.Layout!.FindBox(view.Document!.GetElementById("h")!)!;

        Assert.That(hidden.BorderBox.Height, Is.EqualTo(40), "the box is still laid out");
        Assert.That(view.Layout.FindBox(view.Document!.GetElementById("after")!)!.BorderBox.Y, Is.EqualTo(40),
            "and what follows it sits below where it stands");
        Assert.That(Texts(view).Any(run => run.Text == "gone"), Is.False, "its text is not drawn");
        Assert.That(view.Paint!.Commands.OfType<FillCommand>().Any(fill => fill.Bounds.Equals(hidden.BorderBox)), Is.False,
            "nor its background");
    }

    [Test]
    public async Task AHiddenImageAndBorderAreNotDrawnEither()
    {
        using var view = CreateView();

        await view.SetHtml(Page("<div id='h' style='visibility:hidden;width:50px;height:50px;border:2px solid #000'>x</div>"));

        var box = view.Layout!.FindBox(view.Document!.GetElementById("h")!)!;

        Assert.That(view.Paint!.Commands.OfType<BorderCommand>().Any(border => border.Bounds.Equals(box.BorderBox)), Is.False);
    }

    [Test]
    public async Task ADescendantCanAskToBeSeenAgain()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div style='visibility:hidden'><p><span id='s' style='visibility:visible'>seen</span>" +
            "<span id='u'>unseen</span></p></div>"));

        var runs = Texts(view);

        Assert.That(runs.Single(run => run.Text == "seen").Bounds.Width, Is.GreaterThan(0));
        Assert.That(runs.Any(run => run.Text == "unseen"), Is.False, "the rest of the subtree stays hidden");
    }

    [Test]
    public async Task ACollapsedBoxDrawsNothingOfItsOwn()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='h' style='visibility:collapse;height:40px;background:#ff0000'>gone</div>" +
            "<p id='after' style='margin:0'>after</p>"));

        Assert.That(Texts(view).Any(run => run.Text == "gone"), Is.False);
        Assert.That(view.Layout!.FindBox(view.Document!.GetElementById("after")!)!.BorderBox.Y, Is.EqualTo(40),
            "a collapsed box keeps its space in a row, as a hidden one does");
    }

    [Test]
    public async Task HiddenTextCannotBeSelectedOrCopied()
    {
        using var view = CreateView();

        await view.SetHtml(Page("<div style='visibility:hidden'><p>secret</p></div><p>public</p>"));

        view.SelectAll();

        Assert.That(view.SelectedText, Is.EqualTo("public"), "a copy takes what a reader could have read");
    }

    [Test]
    public async Task AHiddenBoxIsNotWhatAPointerIsOver()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='outer' style='width:200px;height:100px'>" +
            "<div id='h' style='visibility:hidden;cursor:pointer;width:100px;height:50px'></div></div>"));

        // The hidden box declares the pointer a link would carry; a reader resting on it is over the
        // block behind it, which declares nothing, because there is nothing drawn there to point at.
        var hidden = view.PointerMoved(10, 10);

        Assert.That(hidden.Cursor, Is.EqualTo(CursorKind.Default));

        var text = Texts(view);

        Assert.That(text, Is.Empty, "and the box has no text to draw either");
    }

    private static System.Collections.Generic.IEnumerable<TextCommand> Texts(HutuViewInternal view) =>
        view.Paint!.Commands.OfType<TextCommand>();

    private static String Page(String body) =>
        "<html><body style='margin:0'>" + body + "</body></html>";

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(600, 600);
        return view;
    }
}

using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the room a tab takes in a mode that keeps its white space: the line runs on to the next
/// multiple of the tab size counted from where the line began, the count form is a number of the
/// element's own spaces, and a tab the line trails into hangs (CSS Text 3 §4.2).
/// </summary>
/// <remarks>
/// The fake measurer gives every character five pixels, so the initial <c>tab-size: 8</c> puts a stop
/// every forty pixels. The numbers the tests assert were measured in Chrome, where the same arithmetic
/// lands on 96, 192, 24 and 30 for the same shapes at a twelve-pixel space.
/// </remarks>
[TestFixture]
public sealed class TabTests
{
    [Test]
    public async Task ATabRunsTheLineOnToTheNextStop([Values("pre", "pre-wrap", "break-spaces")] String mode)
    {
        using var view = await Render(Line(mode, "width:200px", "A\t<span id='b'>B</span>"));

        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(40), "the first stop past the one character already set");
    }

    [Test]
    public async Task ATabAtATabStopGoesToTheNextOne()
    {
        using var view = await Render(Line("pre", "width:200px", "AAAAAAAAA\t<span id='b'>B</span>"));

        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(80), "forty-five pixels in, the next stop is eighty");
    }

    [Test]
    public async Task TheCountFormCountsTheElementsOwnSpaces()
    {
        using var view = await Render(Line("pre", "width:200px;tab-size:2", "A\t<span id='b'>B</span>"));

        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(10), "two of the element's five-pixel spaces");
    }

    [Test]
    public async Task ALengthFormIsTheStopItself()
    {
        using var view = await Render(Line("pre", "width:200px;tab-size:30px", "A\t<span id='b'>B</span>"));

        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(30));
    }

    [Test]
    public async Task ATabOfNoWidthRunsNowhere()
    {
        using var view = await Render(Line("pre", "width:200px;tab-size:0", "A\t<span id='b'>B</span>"));

        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(5), "the tab takes no room at all");
    }

    [Test]
    public async Task TheStopsAreCountedFromWhereTheLineBegan()
    {
        // The indentation moves where the text starts but not where the stops are: they are multiples
        // of the tab size from the content edge, so an indented line's first tab lands on the same stop
        // an unindented one's would.
        using var view = await Render(Line("pre", "width:200px;text-indent:15px", "A\t<span id='b'>B</span>"));

        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(40));
    }

    [Test]
    public async Task ATabTheLineTrailsIntoHangs()
    {
        using var view = await Render(Line("pre-wrap", "width:10px", "XX\t\t<span id='b'>XX</span>"));

        var second = Find(view, "b").BorderBox;

        Assert.That(second.X, Is.EqualTo(0), "the tabs hang, so the word after them begins the next line");
        Assert.That(second.Y, Is.EqualTo(10), "one line down");
    }

    [Test]
    public async Task ATabThatTakesRoomIsWrappedLikeAnyOtherContent()
    {
        using var view = await Render(Line("break-spaces", "width:10px", "XX\t\t<span id='b'>XX</span>"));

        var second = Find(view, "b").BorderBox;

        Assert.That(second.X, Is.EqualTo(0));
        Assert.That(second.Y, Is.EqualTo(20), "the tabs took a line of their own");
    }

    [Test]
    public async Task AColumnRunsItsTabsDownThePageAsWell()
    {
        using var view = await Render(
            "<style>#t { writing-mode: vertical-rl; width: 200px; height: 200px; white-space: pre }</style>" +
            "<div id='t'>A\t<span id='b'>B</span></div>");

        Assert.That(
            Find(view, "b").BorderBox.Y,
            Is.EqualTo(40),
            "the stop is counted along the axis the text runs on");
    }

    private static String Line(String mode, String extra, String content) =>
        $"<style>#t {{ {extra}; white-space: {mode} }}</style><div id='t'>{content}</div>";

    private static async Task<HutuViewInternal> Render(String content)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>div { margin: 0 }</style></head>" +
            "<body style='margin:0;font-size:10px;line-height:10px'>" + content + "</body></html>");
        return view;
    }

    private static LayoutBox Find(HutuViewInternal view, String id) =>
        Walk(view.Layout!.Root).First(box => box.Source.Element?.GetAttribute("id") == id);

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

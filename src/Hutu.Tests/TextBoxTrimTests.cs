using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers <c>text-box-trim</c> and <c>text-box-edge</c> (CSS Inline 3 §5.1): a block may cut the leading
/// of its first and last lines away, so the text edge stands on the block's own edge.
/// </summary>
/// <remarks>
/// The fake measurer stands its text at an ascent of 8 and a descent of 2, so a ten-pixel text box is
/// what the trim reaches, and a line written at 30 pixels carries ten of leading over and ten under.
/// </remarks>
[TestFixture]
public sealed class TextBoxTrimTests
{
    [Test]
    public async Task TheLeadingIsTrimmedFromBothEnds()
    {
        using var view = await Render("<style>div { text-box-trim: trim-both }</style><div id='p'>Test</div>");

        Assert.That(Find(view, "p").BorderBox.Height, Is.EqualTo(10), "the block ends where the text edge does at each end");
        Assert.That(Lines(view).Single().BorderBox.Y, Is.EqualTo(-10), "the line box itself is unchanged, and starts above the block");
    }

    [Test]
    public async Task OnlyTheEndAskedForIsTrimmed()
    {
        using var view = await Render("<style>div { text-box-trim: trim-start }</style><div id='p'>Test</div>");

        Assert.That(Find(view, "p").BorderBox.Height, Is.EqualTo(20), "the leading under the last line stays");
        Assert.That(Lines(view).Single().BorderBox.Y, Is.EqualTo(-10));
    }

    [Test]
    public async Task TheEndAskedForIsTheOneTrimmed()
    {
        using var view = await Render("<style>div { text-box-trim: trim-end }</style><div id='p'>Test</div>");

        Assert.That(Find(view, "p").BorderBox.Height, Is.EqualTo(20), "the leading over the first line stays");
        Assert.That(Lines(view).Single().BorderBox.Y, Is.EqualTo(0));
    }

    [Test]
    public async Task TheEdgeNamesTheLineTheBlockIsTrimmedTo()
    {
        using var view = await Render(
            "<style>div { text-box-trim: trim-start; text-box-edge: alphabetic }</style><div id='p'>Test</div>");

        Assert.That(Find(view, "p").BorderBox.Height, Is.EqualTo(12), "the baseline stands on the block's edge: 30 - 18 of ascent");
        Assert.That(Lines(view).Single().BorderBox.Y, Is.EqualTo(-18));
    }

    [Test]
    public async Task TwoEdgesNameTheStartAndTheEnd()
    {
        using var view = await Render(
            "<style>div { text-box-trim: trim-both; text-box-edge: text alphabetic }</style><div id='p'>Test</div>");

        Assert.That(Find(view, "p").BorderBox.Height, Is.EqualTo(8), "the text box at the start and the baseline at the end: 30 - 10 - 12");
    }

    [Test]
    public async Task ABlockWithoutThePropertyKeepsItsLeading()
    {
        using var view = await Render("<div id='p'>Test</div>");

        Assert.That(Find(view, "p").BorderBox.Height, Is.EqualTo(30), "the line box as it was written");
        Assert.That(Lines(view).Single().BorderBox.Y, Is.EqualTo(0));
    }

    [Test]
    public async Task OnlyTheBlocksOwnEndsAreTrimmedWhenBlocksStandBetween()
    {
        using var view = await Render(
            "<style>div { text-box-trim: trim-both }</style>" +
            "<div id='p'>Test<div id='inner' style='height:50px'></div>Test</div>");

        var block = Find(view, "p");

        Assert.That(block.BorderBox.Height, Is.EqualTo(90), "20 at the start, the 50 box, 20 at the end");
        Assert.That(Find(view, "inner").BorderBox.Y, Is.EqualTo(block.BorderBox.Y + 20), "the runs between blocks trim nothing");

        var lines = Lines(view);

        Assert.That(lines[0].BorderBox.Y, Is.EqualTo(-10), "the first line starts above the block, as the property asks");
        Assert.That(lines[1].BorderBox.Y, Is.EqualTo(70), "the last line keeps the leading over it; only the block's end is cut");
    }

    [Test]
    public async Task AFollowingBlockOfTextHoldsTheElementsLastLine()
    {
        using var view = await Render(
            "<style>div { text-box-trim: trim-both }</style>" +
            "<div id='p'>Test<div>line</div></div>");

        var block = Find(view, "p");

        Assert.That(block.BorderBox.Height, Is.EqualTo(30), "the run keeps 20 for its first line alone, and the block of text 10");
        Assert.That(Lines(view)[0].BorderBox.Y, Is.EqualTo(-10), "the first line starts above the block");
        Assert.That(Lines(view)[1].BorderBox.Y, Is.EqualTo(10), "the block of text starts its own line above itself, its own ends trimmed");
    }

    [Test]
    public async Task AFollowingBlockWithNoTextLeavesTheRunAsTheLastLine()
    {
        using var view = await Render(
            "<style>div { text-box-trim: trim-both }</style>" +
            "<div id='p'>Test<div style='height:20px'></div></div>");

        var block = Find(view, "p");

        Assert.That(block.BorderBox.Height, Is.EqualTo(30), "the run is the first and last line: 30 - 10 - 10, and 20 for the empty block");
    }

    private static async Task<HutuViewInternal> Render(String content)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>p, div { margin: 0 }</style></head>" +
            "<body style='margin:0;font-size:10px;line-height:30px'>" + content + "</body></html>");
        return view;
    }

    private static LayoutBox Find(HutuViewInternal view, String id) =>
        Walk(view.Layout!.Root).First(box => box.Source.Element?.GetAttribute("id") == id);

    private static List<LayoutBox> Lines(HutuViewInternal view) =>
        Walk(view.Layout!.Root).Where(box => box.Kind == LayoutKind.Line).ToList();

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

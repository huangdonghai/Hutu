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

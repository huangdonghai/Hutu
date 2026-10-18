using Hutu.Rendering.Layout;
using Hutu.Skia;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers <c>vertical-align</c>: where a box's baseline ends up against the one the line shares, and the
/// fact that the line box is grown from the boxes as they end up standing rather than from what they
/// reached before any of them was moved (CSS 2.1 §10.8.1).
/// </summary>
/// <remarks>
/// The fake measurer gives every font eight above the baseline and two below whatever the size, so a
/// line set at <c>line-height:1</c> is ten tall. A text box is drawn as tall as the line it stands on,
/// which is why these ask for the baseline a run was placed at rather than for its top edge.
/// </remarks>
[TestFixture]
public sealed class VerticalAlignTests
{
    [Test]
    public async Task ABoxLeftOnTheBaselineLeavesTheLineAsItWas()
    {
        using var view = CreateView();

        await view.SetHtml(Document("ab"));

        Assert.That(Lines(view).Single().BorderBox.Height, Is.EqualTo(10));
        Assert.That(Text(view, "ab").Baseline, Is.EqualTo(8), "the font's eight above the baseline");
    }

    [Test]
    public async Task ARaisedRunLiftsTheHeightOfTheLine()
    {
        using var view = CreateView();

        // A superscript is raised four of the eight pixels its font reaches above the baseline, and the
        // line has to have that room: the box it stands on grows to where the raised run reaches rather
        // than to where the same run would have been left on the baseline.
        await view.SetHtml(Document("<span style='vertical-align:super'>ab</span>"));

        Assert.That(Lines(view).Single().BorderBox.Height, Is.EqualTo(14), "ten, plus the four it was raised");
        Assert.That(Text(view, "ab").Baseline, Is.EqualTo(8), "the line's own baseline went up with the run");
    }

    [Test]
    public async Task AMiddleAlignmentRestsTheBoxOnTheParentsMidline()
    {
        using var view = CreateView();

        // The middle of the box goes half the parent's x-height above the baseline. This measurer states
        // no x-height for any font, so the engine stands one in from the half-em `ex` it already uses for
        // that unit: two and a half of a ten pixel font, against the box's own centre three above its
        // baseline. The box is lowered by the difference and the line grows below it.
        await view.SetHtml(Document("<span style='vertical-align:middle'>ab</span>"));

        Assert.That(Lines(view).Single().BorderBox.Height, Is.EqualTo(10.5).Within(0.001));
        Assert.That(Text(view, "ab").Baseline, Is.EqualTo(8.5).Within(0.001), "lowered by half a pixel");
    }

    [Test]
    public async Task AMiddleAlignmentUsesTheHeightTheFaceItselfStates()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(600, 600);

        // Ahem stands the top of its lowercase at the top of its box, so half its x-height is sixteen of
        // the forty pixels it is drawn at, against the twelve its own midline stands above the baseline.
        // The run climbs that four and the line grows by it - which is the height Chromium gives the same
        // page, where the em approximation above would have left the line at forty.
        await view.SetHtml(AhemDocument("<span style='vertical-align:middle'>ab</span>"));

        Assert.That(measurer.GetMetrics(new FontStyle("Ahem", 40, 400, false)).XHeight, Is.EqualTo(32));
        Assert.That(Lines(view).Single().BorderBox.Height, Is.EqualTo(44), "forty, plus the four it climbed");
        Assert.That(Lines(view).Single().Baseline, Is.EqualTo(36), "and the room was wanted above");
        Assert.That(Text(view, "ab").Baseline, Is.EqualTo(32), "four above the line's own baseline");
    }

    [Test]
    public async Task ABoxAlignedToTheTopClimbsToTheTopOfTheLine(
        [Values("top", "bottom")] String alignment)
    {
        using var view = CreateView();

        // The empty inline-block stands on its bottom edge, so the line reaches thirty above the shared
        // baseline and two below. A run reaching eight above its own baseline has twenty-two to climb to
        // stand at the top of that, and nothing at all to climb at the other end.
        await view.SetHtml(Document(
            "<span style='display:inline-block;width:5px;height:30px'></span>" +
            $"<span style='vertical-align:{alignment}'>ab</span>"));

        Assert.That(Lines(view).Single().BorderBox.Height, Is.EqualTo(32));

        Assert.That(
            Text(view, "ab").Baseline,
            Is.EqualTo(alignment == "top" ? 8 : 30),
            alignment == "top"
                ? "the run was raised until its font touched the top of the line box"
                : "the run's own two below the baseline are already the two the line reaches");
    }

    [Test]
    public async Task AnAlignedBoxCarriesTheTextInsideItAlong()
    {
        using var view = CreateView();

        // The run's own alignment is the baseline, but the element it sits in is aligned to the top of
        // the line, and an inline box takes its content along with it - the band it draws included.
        await view.SetHtml(Document(
            "<span style='display:inline-block;width:5px;height:30px'></span>" +
            "<span id='aligned' style='vertical-align:top'><b>ab</b></span>"));

        Assert.That(Text(view, "ab").Baseline, Is.EqualTo(8), "the text went up with the box around it");

        var aligned = view.Document!.GetElementById("aligned")!;

        Assert.That(
            view.GetElementBounds(aligned)!.Value.Y,
            Is.EqualTo(0),
            "and the band the aligned element draws stands with it");
    }

    private static String Document(String content) =>
        "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:1'>" +
        content + "</p></body></html>";

    /// <summary>A page drawn with the font that states its own heights, at the size the test measures.</summary>
    private static String AhemDocument(String content) =>
        "<html><body style='margin:0'><p style='margin:0;font-family:Ahem;font-size:40px;line-height:1'>" +
        content + "</p></body></html>";

    private static HutuViewInternal CreateView(Int32 viewportWidth = 600)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static LayoutBox Text(HutuViewInternal view, String text) =>
        Walk(view).Single(node => node.Kind == LayoutKind.Text && node.Text == text);

    private static IEnumerable<LayoutBox> Lines(HutuViewInternal view) =>
        Walk(view).Where(node => node.Kind == LayoutKind.Line);

    private static IEnumerable<LayoutBox> Walk(HutuViewInternal view)
    {
        var root = view.Layout?.Root;

        return root is null ? Enumerable.Empty<LayoutBox>() : Walk(root);
    }

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

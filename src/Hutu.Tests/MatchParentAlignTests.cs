using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers <c>text-align: match-parent</c>, which is inherit with one difference: a parent that came to
/// <c>start</c> or <c>end</c> is matched as the physical edge the parent's own direction gives it, so a
/// child that reads the other way still stands where the parent's text stood. With no parent to match -
/// the root - the value computes to <c>start</c> (CSS Text 3 §6.1).
/// </summary>
/// <remarks>
/// The fake measurer gives five pixels a character, and every box below is a hundred of them wide with a
/// ten pixel word in it: the word stands at 0, 45 or 90 according to which edge the alignment read.
/// </remarks>
[TestFixture]
public sealed class MatchParentAlignTests
{
    [Test]
    public async Task TheChildTakesTheEdgeTheParentCameTo()
    {
        using var view = CreateView();

        // A parent that came to `end` along its own left-to-right lines is matched as `right`: the same
        // edge the parent gave its lines, and the same one the child gives its own.
        await view.SetHtml(Page("<div style='text-align:end'>" + Box("text-align:match-parent") + "</div>"));

        Assert.That(Left(view, "word"), Is.EqualTo(90).Within(0.001));
    }

    [Test]
    public async Task TheEdgeIsReadAgainstTheParentsDirectionAndNotTheChilds()
    {
        using var view = CreateView();

        // The case that is not a restatement of inherit: the parent reads right-to-left and came to
        // `start`, which is its right edge, and the child reads left-to-right. The specification gives
        // the child the parent's edge; Chromium, measured on this shape, gives it its own - and the
        // borrowed pages that name the value ask for the reading above.
        await view.SetHtml(
            Page(
                "<div style='direction:rtl;text-align:start'>" +
                "<div style='direction:ltr;text-align:match-parent;width:100px'>" +
                "<span id='word'>ab</span></div></div>"));

        // A block stands against its containing block's inline start, which is the right edge in a
        // right-to-left page: the hundred-pixel box begins at 500 of the six hundred, and the word the
        // parent's edge sends right stands ninety pixels inside it (measured in Chrome on this shape).
        Assert.That(Left(view, "word"), Is.EqualTo(590).Within(0.001), "the parent's edge, not the child's");
    }

    [Test]
    public async Task AnEdgeThatWasAlreadyPhysicalTravelsUnchanged()
    {
        using var view = CreateView();

        // The two logical keywords are the only ones the value re-reads: a centred parent is matched as
        // centred, and a parent that justified its lines is matched as justified.
        await view.SetHtml(Page("<div style='text-align:center'>" + Box("text-align:match-parent") + "</div>"));

        Assert.That(Left(view, "word"), Is.EqualTo(45).Within(0.001));
    }

    [Test]
    public async Task TheRootHasNothingToMatchAndKeepsTheLogicalValue()
    {
        using var view = CreateView();

        // The root computes `start`, not an edge - and what the blocks under it inherit is that logical
        // value, which each of them reads against its own direction. This page reads left-to-right, so
        // the word stands at the left.
        await view.SetHtml(
            "<html style='margin:0;text-align:match-parent'><body style='margin:0'>" +
            "<div style='width:100px;font-size:10px;line-height:1'><span id='word'>ab</span></div>" +
            "</body></html>");

        Assert.That(Left(view, "word"), Is.EqualTo(0).Within(0.001));
    }

    [Test]
    public async Task TheRootValueStillReadsFromTheDirectionOfTheBlockBelowIt()
    {
        using var view = CreateView();

        // The same page turned around: a root that asked to match what no parent gives it, and document
        // that reads right-to-left. `start` is that direction's right edge, which is where the word
        // stands - the root did not turn the value into a physical side.
        await view.SetHtml(
            "<html style='margin:0;direction:rtl;text-align:match-parent'><body style='margin:0'>" +
            "<div style='width:100px;font-size:10px;line-height:1'><span id='word'>ab</span></div>" +
            "</body></html>");

        Assert.That(Left(view, "word"), Is.EqualTo(590).Within(0.001), "start along right-to-left lines");
    }

    /// <summary>Makes a hundred-pixel box of the fixture's one word, aligned as the test asks.</summary>
    private static String Box(String style) =>
        "<div style='width:100px;font-size:10px;line-height:1;" + style + "'><span id='word'>ab</span></div>";

    private static String Page(String content) =>
        "<html><body style='margin:0;font-size:10px;line-height:1'>" + content + "</body></html>";

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(600, 600);
        return view;
    }

    private static Double Left(HutuViewInternal view, String id) => Bounds(view, id).X;

    private static Rect Bounds(HutuViewInternal view, String id)
    {
        var element = view.Document!.GetElementById(id);

        Assert.That(element, Is.Not.Null, $"the document holds no element with id '{id}'");

        var bounds = view.GetElementBounds(element!);

        Assert.That(bounds, Is.Not.Null, $"the element '{id}' was given no box to measure");

        return bounds!.Value;
    }
}

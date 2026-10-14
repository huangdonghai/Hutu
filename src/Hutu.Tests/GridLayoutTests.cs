using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The grid formatting context: track sizing, the automatic placement, spans, implicit tracks, gaps
/// and both levels of alignment.
/// </summary>
/// <remarks>
/// The documents go through the real cascade, so <c>grid-template-columns</c> and friends are read the
/// way a page would present them - including the structured <c>repeat()</c> and <c>minmax()</c> forms.
/// The font is a fixed-width stand-in, so a character is five pixels wide and a line is ten pixels
/// tall, which is what makes the expected numbers exact.
/// </remarks>
[TestFixture]
public sealed class GridLayoutTests
{
    [Test]
    public async Task EqualFractionsSplitTheWidthEvenly()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(3,minmax(0,1fr));width:300px'>" +
            "<div id='a'>A</div><div id='b'>B</div><div id='c'>C</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");
        var c = Find(view, "c");

        Assert.That(a.BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(a.BorderBox.Width, Is.EqualTo(100).Within(0.5));
        Assert.That(b.BorderBox.X, Is.EqualTo(100).Within(0.5));
        Assert.That(c.BorderBox.X, Is.EqualTo(200).Within(0.5));
        Assert.That(c.BorderBox.Right, Is.EqualTo(300).Within(0.5), "the tracks fill the container");
    }

    [Test]
    public async Task GapIsLeftBetweenTheTracks()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:10px;width:320px'>" +
            "<div id='a'>A</div><div id='b'>B</div><div id='c'>C</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");

        // Two gaps come off the width before the three tracks share it.
        Assert.That(a.BorderBox.Width, Is.EqualTo(100).Within(0.5));
        Assert.That(b.BorderBox.X - a.BorderBox.Right, Is.EqualTo(10).Within(0.5));
    }

    [Test]
    public async Task AFixedTrackKeepsItsWidthAndTheFractionTakesTheRest()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:240px minmax(0,1fr);width:800px'>" +
            "<div id='a'>A</div><div id='b'>B</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(240).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(240).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(560).Within(0.5));
    }

    [Test]
    public async Task PercentTracksResolveAgainstTheContainer()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:25% 75%;width:400px'>" +
            "<div id='a'>A</div><div id='b'>B</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(100).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(300).Within(0.5));
    }

    [Test]
    public async Task ContentTracksSizeToTheirItems()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:auto auto;justify-content:flex-start'>" +
            "<div id='a'>A</div><div id='b'>A longer cell</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(5).Within(0.5), "one character");
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(65).Within(0.5), "thirteen characters");
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(5).Within(0.5));
    }

    [Test]
    public async Task MinMaxRaisesATrackToItsMinimum()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:minmax(120px,1fr) minmax(120px,1fr);width:400px'>" +
            "<div id='a'>A</div><div id='b'>B</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(200).Within(0.5), "the minimum is a floor, not a cap");
    }

    [Test]
    public async Task AutoFitCollapsesTheTracksNoItemReaches()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(auto-fit,minmax(125px,1fr));gap:12px;width:400px'>" +
            "<div id='a'>A</div><div id='b'>B</div>" +
            "</div>" +
            "</body></html>");

        // Three tracks of 125 would fit, but only two have an item, so the two that remain share the
        // whole width - which is the difference between auto-fit and auto-fill.
        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(194).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(206).Within(0.5));
    }

    [Test]
    public async Task AutoFillKeepsTheTracksNoItemReaches()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(auto-fill,minmax(125px,1fr));gap:12px;width:400px'>" +
            "<div id='a'>A</div><div id='b'>B</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(125.33).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Right, Is.LessThan(400), "the third track stays empty");
    }

    [Test]
    public async Task ASpanningItemPushesTheFollowingItemsToTheNextRow()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(3,minmax(0,1fr));width:300px'>" +
            "<div id='a' style='grid-column:span 2'>A</div>" +
            "<div id='b'>B</div><div id='c'>C</div><div id='d'>D</div><div id='e'>E</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(200).Within(0.5), "two tracks");
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(200).Within(0.5), "the next item takes the third track");
        Assert.That(Find(view, "b").BorderBox.Y, Is.EqualTo(Find(view, "a").BorderBox.Y).Within(0.5));
        Assert.That(Find(view, "c").BorderBox.Y, Is.GreaterThan(Find(view, "a").BorderBox.Y), "the row wrapped");
        Assert.That(Find(view, "c").BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(Find(view, "e").BorderBox.X, Is.EqualTo(200).Within(0.5));
    }

    [Test]
    public async Task AnExplicitPlacementPutsAnItemWhereItSays()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(3,minmax(0,1fr));width:300px'>" +
            "<div id='a' style='grid-column:2'>A</div>" +
            "<div id='b'>B</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");

        Assert.That(a.BorderBox.X, Is.EqualTo(100).Within(0.5), "the second column");
        Assert.That(a.BorderBox.Y, Is.EqualTo(0).Within(0.5), "the cursor was still on the first row");
        // The cursor advances past the explicitly placed item, so the automatic one continues the row
        // instead of back-filling the hole the explicit placement left behind.
        Assert.That(b.BorderBox.X, Is.EqualTo(200).Within(0.5));
        Assert.That(b.BorderBox.Y, Is.EqualTo(a.BorderBox.Y).Within(0.5));
    }

    [Test]
    public async Task AnItemPastTheExplicitGridCreatesAnImplicitTrack()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:100px;grid-auto-columns:50px;width:400px;justify-content:flex-start'>" +
            "<div id='a'>A</div><div id='b'>B</div><div id='c' style='grid-column:3'>C</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(100).Within(0.5));
        Assert.That(Find(view, "c").BorderBox.X, Is.EqualTo(150).Within(0.5), "an implicit track follows the explicit one");
        Assert.That(Find(view, "c").BorderBox.Width, Is.EqualTo(50).Within(0.5));
    }

    [Test]
    public async Task ARowIsAsTallAsItsTallestItem()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(2,minmax(0,1fr));width:200px'>" +
            "<div id='a' style='height:40px'>A</div><div id='b' style='height:20px'>B</div>" +
            "<div id='c'>C</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Height, Is.EqualTo(40).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Height, Is.EqualTo(20).Within(0.5), "a shorter item keeps its own height");
        Assert.That(Find(view, "c").BorderBox.Y, Is.EqualTo(40).Within(0.5), "the second row starts below the first");
    }

    [Test]
    public async Task AStretchedItemFillsItsArea()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(2,minmax(0,1fr));width:200px'>" +
            "<div id='a' style='height:40px'>A</div><div id='b'>B</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "b").BorderBox.Height, Is.EqualTo(40).Within(0.5), "stretch is the default");
    }

    [Test]
    public async Task PlaceItemsCentresAnItemInItsArea()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;place-items:center;width:100px'>" +
            "<div id='a' style='width:20px;height:10px'>A</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(40).Within(0.5), "(100 - 20) / 2");
        Assert.That(Find(view, "a").BorderBox.Y, Is.EqualTo(0).Within(0.5), "the row is only as tall as the item");
    }

    [Test]
    public async Task JustifyContentMovesTheWholeSetOfTracks()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:50px 50px;justify-content:center;width:200px'>" +
            "<div id='a'>A</div><div id='b'>B</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(50).Within(0.5), "(200 - 100) / 2");
    }

    [Test]
    public async Task JustifySelfOverridesTheContainerAlignment()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:100px;justify-items:center;width:100px'>" +
            "<div id='a' style='width:20px'>A</div>" +
            "<div id='b' style='width:20px;justify-self:flex-end'>B</div>" +
            "<div id='c' style='width:20px;justify-self:flex-start'>C</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(40).Within(0.5), "centred by the container");
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(80).Within(0.5), "pushed to the end");
        Assert.That(Find(view, "c").BorderBox.X, Is.EqualTo(0).Within(0.5), "back to the start");
    }

    [Test]
    public async Task AHeightOfAutoRowsIsPaidForByTheFractionRow()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:100px;grid-template-rows:auto minmax(0,1fr);height:200px'>" +
            "<div id='a'>A</div><div id='b'>B</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Height, Is.EqualTo(10).Within(0.5), "one line of text");
        Assert.That(Find(view, "b").BorderBox.Y, Is.EqualTo(10).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Height, Is.EqualTo(190).Within(0.5), "the rest of the height");
    }

    [Test]
    public async Task AColumnOfGridsNestsWithoutLosingItsWidth()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:10px;width:210px'>" +
            "<div id='outer'>" +
            "<div style='display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:10px'>" +
            "<div id='a'>A</div><div id='b'>B</div>" +
            "</div>" +
            "</div>" +
            "</div>" +
            "</body></html>");

        // The outer grid gives the nested one 100 pixels, and the nested grid shares them out again.
        Assert.That(Find(view, "outer").BorderBox.Width, Is.EqualTo(100).Within(0.5));
        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(45).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(55).Within(0.5));
    }

    [Test]
    public async Task AnInlineOnlyContainerHoldsOneItem()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;place-items:center;width:100px;height:40px'>H</div>" +
            "</body></html>");

        // The bare word is wrapped into a single anonymous item, and the container's own declared size
        // must not leak into it - otherwise the text would be stretched across the whole grid.
        var line = FindLine(view, "H");

        Assert.That(line.BorderBox.X, Is.EqualTo(47.5).Within(0.5), "(100 - 5) / 2");
        Assert.That(line.BorderBox.Y, Is.EqualTo(15).Within(0.5), "(40 - 10) / 2");
    }

    private static HutuViewInternal CreateView(Int32 viewportWidth)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    /// <summary>
    /// A template area placed by name: the item says which cell it belongs to and the grid says where
    /// that cell is, on both axes and across the rows the area spans. This is the shape a real page's
    /// frame is written in, and the numbers are what a browser makes of the same document.
    /// </summary>
    [Test]
    public async Task ANamedAreaPlacesTheItemWhereTheTemplateSays()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;width:300px;grid-template-columns:100px 100px;" +
            "grid-template-rows:50px 50px;grid-template-areas:&#39;head side&#39; &#39;body side&#39;'>" +
            "<div id='a' style='grid-area:side'>a</div>" +
            "<div id='b' style='grid-area:head'>b</div>" +
            "<div id='c' style='grid-area:body'>c</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");
        var c = Find(view, "c");

        Assert.That(a.BorderBox.X, Is.EqualTo(100).Within(0.5), "the side cell is the second column");
        Assert.That(a.BorderBox.Y, Is.EqualTo(0).Within(0.5));
        Assert.That(a.BorderBox.Height, Is.EqualTo(100).Within(0.5), "and it reaches down both rows");
        Assert.That(b.BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(b.BorderBox.Y, Is.EqualTo(0).Within(0.5));
        Assert.That(c.BorderBox.Y, Is.EqualTo(50).Within(0.5), "the body cell is below the head one");
    }

    /// <summary>
    /// A line named in the track list, which is the other thing an item's placement can point at: the
    /// names in front of the sizes are not tracks, so they are read from the text of the list.
    /// </summary>
    [Test]
    public async Task ANamedLinePlacesTheItemWhereTheTracksSay()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;width:300px;" +
            "grid-template-columns:[first] 100px [second] 100px;grid-template-rows:50px 50px'>" +
            "<div id='a' style='grid-column:second;grid-row:2'>a</div>" +
            "<div id='b' style='grid-column:first/span 2'>b</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");

        Assert.That(a.BorderBox.X, Is.EqualTo(100).Within(0.5), "the second named line");
        Assert.That(a.BorderBox.Y, Is.EqualTo(50).Within(0.5));
        Assert.That(b.BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(b.BorderBox.Width, Is.EqualTo(200).Within(0.5), "and a span across both");
    }

    /// <summary>
    /// A template that names its cells without sizing them still says how many tracks there are: the
    /// grid gets one column per cell of a row and one row per string, each sized as <c>auto</c>.
    /// </summary>
    [Test]
    public async Task ATemplateOfAreasAloneSetsTheTrackCount()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;width:300px;grid-template-areas:&#39;left right&#39; &#39;left right&#39;'>" +
            "<div id='a' style='grid-area:left'>a</div>" +
            "<div id='b' style='grid-area:right'>b</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");

        Assert.That(a.BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(b.BorderBox.X, Is.GreaterThan(a.BorderBox.Right - 0.5), "the second column starts after the first");
        Assert.That(b.BorderBox.Right, Is.EqualTo(300).Within(1), "and the two together fill the grid");

        // Both areas cover both rows, so both items reach the same depth however those rows were sized;
        // a grid that had not read the template would have laid them one under the other instead.
        Assert.That(a.BorderBox.Bottom, Is.EqualTo(b.BorderBox.Bottom).Within(0.5));
        Assert.That(a.BorderBox.Height, Is.GreaterThan(0));
    }

    /// <summary>
    /// A cell of the template can be left unnamed with a <c>.</c>, which claims the track without
    /// giving it a name anything could be placed by. The real pages that need this are the ones whose
    /// frame leaves a corner of the grid to whatever falls there.
    /// </summary>
    [Test]
    public async Task ACellOfATemplateCanBeLeftUnnamed()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;width:300px;grid-template-areas:&#39;a .&#39; &#39;a .&#39;'>" +
            "<div id='a' style='grid-area:a'>a</div>" +
            "<div id='b'>b</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");

        Assert.That(a.BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(a.BorderBox.Right, Is.LessThan(300).Within(1), "the unnamed cell still holds a column");
        Assert.That(a.BorderBox.Width, Is.GreaterThan(100), "and the named one keeps the rest it was sized to");
    }

    /// <summary>
    /// An area's <c>-end</c> line is named by cutting its four-character suffix, not the six that
    /// <c>-start</c> takes. Cut the wrong length and a short area name asks for a substring of negative
    /// length, while a longer one reads a stem that matches no area at all.
    /// </summary>
    [Test]
    public async Task AnAreasEndLineCutsItsOwnSuffixLength()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;width:300px;grid-template-columns:100px 100px 100px;grid-template-rows:50px;" +
            "grid-template-areas:&#39;. a a&#39;'>" +
            "<div id='a' style='grid-column:a-start / a-end;grid-row:1'>a</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");

        // The area covers the second and third columns, so its start line is the second and its end the fourth.
        Assert.That(a.BorderBox.X, Is.EqualTo(100).Within(0.5), "the -start line begins the area");
        Assert.That(a.BorderBox.Right, Is.EqualTo(300).Within(0.5), "and the -end line closes it");
    }

    private static LayoutBox Find(HutuViewInternal view, String id)
    {
        foreach (var node in Walk(view))
        {
            if (node.Source.Element?.GetAttribute("id") == id)
            {
                return node;
            }
        }

        Assert.Fail($"no box with id '{id}' was laid out");
        return null!;
    }

    /// <summary>Finds the line box that holds a given piece of text.</summary>
    private static LayoutBox FindLine(HutuViewInternal view, String text)
    {
        foreach (var node in Walk(view))
        {
            if (node.Kind != LayoutKind.Line)
            {
                continue;
            }

            // Through the whole subtree: text wrapped in an inline element hangs off that element's own
            // node rather than off the line, one node per line the element reaches.
            if (Descend(node).Any(child => child.Kind == LayoutKind.Text && child.Text == text))
            {
                return node;
            }
        }

        Assert.Fail($"no line box holding '{text}' was laid out");
        return null!;
    }

    private static IEnumerable<LayoutBox> Walk(HutuViewInternal view)
    {
        var root = view.Layout?.Root;

        Assert.That(root, Is.Not.Null, "the document produced no layout");

        return Descend(root!);
    }

    private static IEnumerable<LayoutBox> Descend(LayoutBox node)
    {
        yield return node;

        foreach (var child in node.Children)
        {
            foreach (var descendant in Descend(child))
            {
                yield return descendant;
            }
        }
    }
}

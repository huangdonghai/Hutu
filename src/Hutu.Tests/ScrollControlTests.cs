using Hutu.Rendering.Layout;
using NUnit.Framework;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers resolving where to scroll: aligning a rectangle in the viewport, clamping it to the
/// document, and looking an element or an anchor up in what was laid out.
/// </summary>
[TestFixture]
public sealed class ScrollControlTests
{
    private const string TallDocument =
        "<html><body style='margin:0'><div style='height:1000px'></div></body></html>";

    [Test]
    public async Task TheDocumentIsAsTallAsItsContent()
    {
        using var view = await CreateViewAsync();

        Assert.That(view.Layout!.Height, Is.EqualTo(1000));
        Assert.That(view.ViewportHeight, Is.EqualTo(300));
    }

    [Test]
    public async Task StartAlignmentPutsTheTopEdgeAtTheTop()
    {
        using var view = await CreateViewAsync();

        Assert.That(OffsetFor(view, y: 400, height: 100, ScrollAlignment.Start), Is.EqualTo(400));
    }

    [Test]
    public async Task CenterAlignmentCentresTheRectangle()
    {
        using var view = await CreateViewAsync();

        // The 200px of viewport left over once the rectangle is in it is split evenly, so the
        // rectangle ends up 100px below the top of the viewport.
        Assert.That(OffsetFor(view, y: 400, height: 100, ScrollAlignment.Center), Is.EqualTo(300));
    }

    [Test]
    public async Task EndAlignmentPutsTheBottomEdgeAtTheBottom()
    {
        using var view = await CreateViewAsync();

        Assert.That(OffsetFor(view, y: 400, height: 100, ScrollAlignment.End), Is.EqualTo(200));
    }

    [Test]
    public async Task NearestLeavesARectangleThatIsAlreadyVisibleWhereItIs()
    {
        using var view = await CreateViewAsync();

        Assert.That(
            OffsetFor(view, y: 400, height: 100, ScrollAlignment.Nearest, current: 350),
            Is.EqualTo(350));
    }

    [Test]
    public async Task NearestMovesTheLeastAmountWhenTheRectangleIsOutOfView()
    {
        using var view = await CreateViewAsync();

        Assert.That(
            OffsetFor(view, y: 400, height: 100, ScrollAlignment.Nearest, current: 0),
            Is.EqualTo(200));

        Assert.That(
            OffsetFor(view, y: 400, height: 100, ScrollAlignment.Nearest, current: 600),
            Is.EqualTo(400));
    }

    [Test]
    public async Task TheOffsetIsClampedToWhatTheDocumentCanScroll()
    {
        using var view = await CreateViewAsync();

        Assert.That(OffsetFor(view, y: -500, height: 100, ScrollAlignment.Start), Is.EqualTo(0));

        // The document is 1000 tall in a 300 viewport, so 700 is as far as it goes.
        Assert.That(OffsetFor(view, y: 990, height: 100, ScrollAlignment.Start), Is.EqualTo(700));
    }

    [Test]
    public async Task AnAnchorResolvesByIdOrByName()
    {
        using var view = await CreateViewAsync(
            "<html><body style='margin:0'>" +
            "<h2 id='intro'>intro</h2>" +
            "<a name='legacy'>legacy</a>" +
            "</body></html>");

        Assert.That(view.FindAnchor("#intro")!.TextContent, Is.EqualTo("intro"));
        Assert.That(view.FindAnchor("intro")!.TextContent, Is.EqualTo("intro"));
        Assert.That(view.FindAnchor("chapter.htm#legacy")!.TextContent, Is.EqualTo("legacy"));
        Assert.That(view.FindAnchor("missing"), Is.Null);
        Assert.That(view.FindAnchor("#"), Is.Null);
    }

    [Test]
    public async Task ElementBoundsAreReportedInDocumentCoordinates()
    {
        using var view = await CreateViewAsync(
            "<html><body style='margin:0'>" +
            "<div style='height:100px'></div>" +
            "<div id='target' style='height:50px'></div>" +
            "</body></html>");

        var target = view.Document!.GetElementById("target")!;
        var bounds = view.GetElementBounds(target);

        Assert.That(bounds, Is.Not.Null);
        Assert.That(bounds!.Value.Y, Is.EqualTo(100));
        Assert.That(bounds.Value.Height, Is.EqualTo(50));
    }

    [Test]
    public async Task AnElementIsBroughtIntoViewFromWhereTheViewportIsNow()
    {
        using var view = await CreateViewAsync(
            "<html><body style='margin:0'>" +
            "<div style='height:400px'></div>" +
            "<div id='target' style='height:100px'></div>" +
            "<div style='height:500px'></div>" +
            "</body></html>");

        var target = view.Document!.GetElementById("target")!;

        // Just past the fold, so the smallest move puts its bottom edge at the bottom of the view.
        Assert.That(view.GetVerticalFrameOffset(target, ScrollAlignment.Nearest, 0), Is.EqualTo(200));

        // Already on screen, so nothing moves.
        Assert.That(view.GetVerticalFrameOffset(target, ScrollAlignment.Nearest, 400), Is.EqualTo(400));
    }

    private static double OffsetFor(
        HutuViewInternal view,
        double y,
        double height,
        ScrollAlignment alignment,
        double current = 0)
    {
        return view.Layout!.VerticalFrameOffsetFor(
            new Rect(0, y, 100, height),
            view.ViewportHeight,
            alignment,
            current);
    }

    private static async Task<HutuViewInternal> CreateViewAsync(string? html = null)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);
        await view.SetHtml(html ?? TallDocument);
        return view;
    }
}

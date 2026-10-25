using Hutu.Skia;
using NUnit.Framework;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// A column must make progress: a block that cannot keep its orphans in a column's room overflows the
/// column instead of leaving the fill to ask the next column the same question about the same room -
/// which a `column-fill: auto` run that names as many columns as it needs would never stop asking
/// (CSS Break 3 §4.3). The page is the shape
/// wpt/css-break/table/repeated-section/text-in-nested-multicol draws.
/// </summary>
[TestFixture]
public sealed class NestedMulticolTests
{
    [Test]
    public async Task AColumnThatCannotKeepItsOrphansOverflowsIt()
    {
        var watch = Stopwatch.StartNew();

        using var view = new HutuViewInternal(new SkiaTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><body style='margin:0'><div style=\"columns:3;width:fit-content;column-fill:auto;height:100px;gap:0;line-height:20px\">" +
            "<div style=\"display:table\"><div style=\"display:table-header-group;break-inside:avoid\">" +
            "<div style=\"columns:2;gap:0;column-fill:auto;height:20px\">h<br>a<br></div>" +
            "</div><div style=\"height:200px\"></div></div></div></body></html>");

        watch.Stop();

        Assert.That(view.Layout?.Root, Is.Not.Null, "the page lays out");
        Assert.That(
            watch.ElapsedMilliseconds,
            Is.LessThan(4000),
            "the draw comes back: a column takes a line it cannot keep rather than asking the next one forever");
    }
}

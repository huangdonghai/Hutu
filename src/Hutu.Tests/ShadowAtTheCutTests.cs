using Hutu.Rendering.Paint;
using NUnit.Framework;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the shadow of a box that a column cut through: each fragment casts the shadow of the whole
/// box, kept inside its own band, and nothing falls across the edge it was cut at
/// (CSS Break 3 §4.7).
/// </summary>
[TestFixture]
public sealed class ShadowAtTheCutTests
{
    [Test]
    public async Task EveryFragmentCastsTheShadowOfTheWholeBox()
    {
        var casts = await Casts();

        Assert.That(casts, Has.Count.EqualTo(3), "the box stands in three columns and casts from each");
        Assert.That(casts.Select(cast => cast.Shape.Height).Distinct(), Is.EqualTo(new[] { 270.0 }),
            "each fragment draws the shadow of the whole box, ten above it and ten below");
        Assert.That(casts.Select(cast => cast.Shape.Y).ToList(), Is.EqualTo(new[] { -10.0, -110.0, -210.0 }),
            "and the whole box moves with the fragment it stands in");
    }

    [Test]
    public async Task NoShadowIsDrawnAcrossTheEdgeTheBoxWasCutAt()
    {
        var casts = await Casts();

        Assert.That(casts.Select(cast => cast.Band!.Value.Height).ToList(), Is.EqualTo(new[] { 110.0, 100.0, 60.0 }),
            "the first fragment keeps its own top edge's shadow, the last its bottom's, and the middle one neither");
        Assert.That(casts[0].Band!.Value.Y, Is.EqualTo(-10), "the shadow of the edge the box really has falls ten above it");
        Assert.That(casts[1].Band!.Value.Y, Is.EqualTo(0), "the middle fragment's band is the column it stands in");
        Assert.That(casts[1].Band!.Value.Bottom, Is.EqualTo(100), "which is all of it and no more");
        Assert.That(casts[2].Band!.Value.Bottom, Is.EqualTo(60),
            "and the last fragment's runs from its column's top to ten past the box's own end");
    }

    [Test]
    public async Task ABoxThatStandsWholeKeepsItsShadowInNoBand()
    {
        var casts = await Casts(whole: true);

        Assert.That(casts, Has.Count.EqualTo(1));
        Assert.That(casts[0].Band, Is.Null, "nothing was cut, so the shadow is drawn where it falls");
    }

    private static async Task<System.Collections.Generic.List<BoxShadowCommand>> Casts(Boolean whole = false)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>div { margin: 0 }</style></head>" +
            "<body style='margin:0;font-size:10px;line-height:10px'>" +
            "<div style='columns:3;column-gap:0;column-fill:auto;width:300px;height:100px'>" +
            "<div id='b' style='box-shadow:0 0 0 10px black;height:" + (whole ? "90px" : "250px") + "'></div>" +
            "</div></body></html>");

        return view.Paint!.Commands.OfType<BoxShadowCommand>().ToList();
    }
}

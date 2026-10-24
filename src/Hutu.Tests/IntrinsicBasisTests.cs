using AngleSharp.Css;
using AngleSharp.Dom;
using Hutu.Rendering;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using Hutu.Skia;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The flag that says a box wants the same intrinsic size at every containing block, which the layout
/// engine keys its intrinsic cache on.
/// </summary>
/// <remarks>
/// Two ways to be wrong, and they cost opposite things. Calling a box dependent when it is not only
/// means it is measured again, so it is invisible. Calling a box free when it is not hands back a size
/// measured against a containing block the box never had - a wrong layout with nothing to point at.
/// The tests below therefore lean on the second: every kind of length that reads the containing block
/// has one.
/// </remarks>
[TestFixture]
public sealed class IntrinsicBasisTests
{
    [TearDown]
    public void TurnTheAssertionOff() => LayoutEngine.AssertBasisFree = false;

    [Test]
    public async Task ABoxThatResolvesNothingAgainstItsContainingBlockIgnoresIt()
    {
        using var view = CreateView(800);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:200px;padding:10px;margin:5px'>word</div>" +
            "</body></html>");

        Assert.That(Boxes(view, "div")[0].IntrinsicIgnoresBasis, Is.True);
    }

    [Test]
    public async Task ADeclaredPercentageWidthReadsTheBasis()
    {
        using var view = CreateView(800);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:50%'></div>" +
            "</body></html>");

        Assert.That(Boxes(view, "div")[0].IntrinsicIgnoresBasis, Is.False);
    }

    [Test]
    public async Task APercentageMarginReadsTheBasis()
    {
        using var view = CreateView(800);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='margin-left:5%'></div>" +
            "</body></html>");

        Assert.That(Boxes(view, "div")[0].IntrinsicIgnoresBasis, Is.False);
    }

    [Test]
    public async Task APercentagePaddingReadsTheBasis()
    {
        using var view = CreateView(800);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='padding-top:5%'></div>" +
            "</body></html>");

        Assert.That(Boxes(view, "div")[0].IntrinsicIgnoresBasis, Is.False);
    }

    [Test]
    public async Task APercentageHeightReadsTheBasis()
    {
        using var view = CreateView(800);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:50%'></div>" +
            "</body></html>");

        Assert.That(Boxes(view, "div")[0].IntrinsicIgnoresBasis, Is.False);
    }

    /// <summary>
    /// <c>min()</c>, <c>max()</c> and <c>clamp()</c> do not survive the style layer today: the
    /// declaration reaches the DOM but AngleSharp drops it, the length reader falls back to
    /// <c>auto</c>, and the box ends up sized as though nothing had been declared. The flag follows
    /// what the engine does rather than what CSS asks for, so a box carrying one is basis-free - it
    /// never consults the width at all.
    /// </summary>
    /// <remarks>
    /// A characterisation, not a wish: the day this fails, the style layer has learned to read the
    /// function. The scan in the box builder already looks inside a math length's arguments and
    /// should still answer correctly, but the sample pages have to be measured again - until now
    /// nothing has ever exercised that path.
    /// </remarks>
    [Test]
    public async Task AMathFunctionDeclinesToALengthToday()
    {
        using var view = CreateView(800);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:min(100%, 640px)'></div>" +
            "</body></html>");

        var box = Boxes(view, "div")[0];

        Assert.That(box.Style.Width.Kind, Is.EqualTo(StyleLengthKind.Auto));
        Assert.That(box.IntrinsicIgnoresBasis, Is.True);
    }

    /// <summary>
    /// A <c>calc()</c> that adds a share of the box to a distance keeps the two apart: CSS Values 4 §10.9
    /// makes the computed value of a math function the function itself, so <c>calc(10px + 5%)</c> reaches
    /// the box model as a sum and takes its 5% off the box being laid out rather than off the window the
    /// style happened to be read in. A length that reads the containing block cannot be measured once for
    /// every basis, so the box is not a basis-free one.
    /// </summary>
    [Test]
    public async Task ACalcKeepsItsShareApartFromItsDistance()
    {
        using var view = CreateView(800);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:calc(10px + 5%)'></div>" +
            "</body></html>");

        var box = Boxes(view, "div")[0];

        Assert.Multiple(() =>
        {
            Assert.That(box.Style.Width.Kind, Is.EqualTo(StyleLengthKind.Math));
            Assert.That(box.Style.Width.Resolve(800), Is.EqualTo(50).Within(0.5), "10px and 5% of this box");
            Assert.That(box.Style.Width.Resolve(400), Is.EqualTo(30).Within(0.5), "and the share follows");
            Assert.That(box.IntrinsicIgnoresBasis, Is.False, "the length reads the basis");
        });
    }

    /// <summary>
    /// The flag is a fact about the subtree, not about the box, so a percentage anywhere below has to
    /// reach every ancestor - it is the ancestor's intrinsic size that gets cached.
    /// </summary>
    [Test]
    public async Task APercentageOnADescendantReachesItsAncestors()
    {
        using var view = CreateView(800);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='outer'><section><p style='margin-left:10%'>word</p></section></div>" +
            "</body></html>");

        Assert.That(Boxes(view, "p")[0].IntrinsicIgnoresBasis, Is.False);
        Assert.That(Boxes(view, "section")[0].IntrinsicIgnoresBasis, Is.False);
        Assert.That(Boxes(view, "div")[0].IntrinsicIgnoresBasis, Is.False);
        Assert.That(Boxes(view, "body")[0].IntrinsicIgnoresBasis, Is.False);
    }

    /// <summary>
    /// The real pages, with the assertion on: every reuse of a basis-free size is measured again with
    /// the basis in the key, and the two have to agree. This is what says the flag is right about the
    /// pages the engine actually lays out, rather than about the handful of shapes above.
    /// </summary>
    [Test]
    public async Task TheSamplePagesMeasureTheSameWithAndWithoutTheBasis()
    {
        LayoutEngine.AssertBasisFree = true;

        foreach (var page in new[] { "index.htm", "Pages/lists-tables.htm" })
        {
            using var skia = new SkiaTextMeasurer();
            using var view = new HutuViewInternal(skia);
            view.AddEmbeddedResourceSource(typeof(Sample.Common.SampleUtils).Assembly, "Samples");
            view.SetViewportSize(900, 600);
            await view.LoadUrlAsync(page);

            var device = new HutuRenderDevice(900, 600);
            var tree = BoxBuilder.TryBuildFused(view.Document!, device, (IImageProvider?)null);

            var engine = new LayoutEngine(skia);
            engine.Layout(tree, device.RenderWidth, device.RenderHeight);
        }
    }

    private static HutuViewInternal CreateView(Int32 viewportWidth)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static List<Box> Boxes(HutuViewInternal view, String tag)
    {
        var device = new HutuRenderDevice(800, 600);
        var root = BoxBuilder.TryBuildFused(view.Document!, device, (IImageProvider?)null);

        var found = new List<Box>();

        if (root is not null)
        {
            Collect(root, tag, found);
        }

        Assert.That(found, Is.Not.Empty, $"no <{tag}> box in the tree");
        return found;
    }

    private static void Collect(Box box, String tag, List<Box> into)
    {
        if (box.Element?.LocalName == tag)
        {
            into.Add(box);
        }

        foreach (var child in box.Children)
        {
            Collect(child, tag, into);
        }
    }
}

using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the font-relative lengths: <c>em</c> and its family are relative to the element's own font
/// size, the font size itself to its parent's, and <c>rem</c> to the root's.
/// </summary>
/// <remarks>
/// The document is wrapper-free apart from a body whose font size is the ten pixels the fake measurer
/// gives every text a width of, so a geometry assertion is the layout's arithmetic and not the
/// machine's fonts.
/// </remarks>
[TestFixture]
public sealed class LengthUnitTests
{
    [Test]
    public async Task EmPaddingIsRelativeToTheElementFontSize()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<div style='width:100px;height:10px;font-size:20px;padding:0.5em'></div>"));

        // Half an em of the element's own twenty pixels, not of the root's sixteen.
        Assert.That(Box(view, "div").Padding.Left, Is.EqualTo(10).Within(0.01));
    }

    [Test]
    public async Task EmWidthIsRelativeToTheElementFontSize()
    {
        using var view = CreateView();

        // The box model reads `width` through the reader that hands over the style rather than a font
        // size, so the size it wants has to be fetched while the length is being resolved. Two em of
        // the element's own twenty, not of the root's sixteen.
        await view.SetHtml(Document("<div style='width:2em;font-size:20px'></div>"));

        Assert.That(Box(view, "div").BorderBox.Width, Is.EqualTo(40).Within(0.01));
    }

    [Test]
    public async Task EmMarginIsRelativeToTheElementFontSize()
    {
        using var view = CreateView();

        // Ten points are 13.33px, so 0.9em is twelve of them - not the 14.4 the root size would give.
        await view.SetHtml(Document("<h2 style='margin:0.9em 0;font-size:10pt'>x</h2>"));

        Assert.That(Box(view, "h2").Margin.Top, Is.EqualTo(12).Within(0.01));
    }

    [Test]
    public async Task EmFontSizeIsRelativeToTheParent()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<div style='font-size:20px'><span style='font-size:1.5em'>x</span></div>"));

        Assert.That(TextFontSize(view, "x"), Is.EqualTo(30).Within(0.01));
    }

    [Test]
    public async Task InheritedRelativeFontSizeIsNotAppliedTwice()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            "<div style='font-size:20px'><span style='font-size:1.5em'><b>x</b></span></div>"));

        Assert.That(
            TextFontSize(view, "x"),
            Is.EqualTo(30).Within(0.01),
            "the child inherits the resolved size; it is not measured against the parent's again");
    }

    [Test]
    public async Task PercentFontSizeIsRelativeToTheParent()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<div style='font-size:20px'><span style='font-size:150%'>x</span></div>"));

        Assert.That(TextFontSize(view, "x"), Is.EqualTo(30).Within(0.01));
    }

    [Test]
    public async Task RemStaysOnTheRootFontSize()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<div style='font-size:20px;width:2rem;height:10px'></div>"));

        Assert.That(
            Box(view, "div").BorderBox.Width,
            Is.EqualTo(32),
            "two rem are twice the sixteen-pixel root, not twice the element's own size");
    }

    [Test]
    public async Task EmBorderRadiusIsRelativeToTheElementFontSize()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            "<div style='width:100px;height:100px;font-size:20px;border-radius:0.5em'></div>"));

        Assert.That(Box(view, "div").Radii.TopLeft.Horizontal, Is.EqualTo(10).Within(0.01));
    }

    /// <summary>Gets the document under test: a body with no margin and the root font size the em tests assume.</summary>
    private static String Document(String content, Int32 width = 0) =>
        "<html><body style='margin:0;font-size:16px;line-height:1'" +
        (width > 0 ? ";width:" + width + "px" : String.Empty) +
        ">" + content + "</body></html>";

    private static HutuViewInternal CreateView(Int32 viewportWidth = 600)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static LayoutBox Box(HutuViewInternal view, String tag) =>
        Walk(view).First(node => node.Source.Element?.LocalName == tag);

    private static Double TextFontSize(HutuViewInternal view, String text) =>
        Walk(view).First(node => node.Kind == LayoutKind.Text && node.Text == text).Font.Size;

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

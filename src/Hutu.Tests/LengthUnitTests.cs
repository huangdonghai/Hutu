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
/// <para>
/// The root is the sixteen a reader starts from unless the document says otherwise, which is what makes
/// the <c>rem</c> cases here worth their while: the size is the document's to choose, and the device the
/// lengths are converted against has to be told what it chose.
/// </para>
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
    public async Task RemIsAShareOfTheSizeTheDocumentChoseForItsRoot()
    {
        using var view = CreateView();

        // The idiom a whole design system is built on: the root is set to ten pixels and every `rem` in
        // the sheets is a multiple of that. Held to the sixteen the device starts at, a ten-rem box is
        // sixteen pixels too wide - which is not a page but a page magnified a half again, and every
        // line of text in it wrapping somewhere else than the author left it.
        await view.SetHtml(
            "<html style='font-size:62.5%'><body style='margin:0'>" +
            "<div style='width:10rem;height:10px'></div></body></html>");

        Assert.That(Box(view, "div").BorderBox.Width, Is.EqualTo(100).Within(0.01));
    }

    [Test]
    public async Task RemOnTheRootIsAShareOfTheInitialSize()
    {
        using var view = CreateView();

        // A root whose size is stated in rem cannot be a share of the size it is deciding, so the spec
        // breaks the circle at the initial value: two rem are thirty-two, and the ten rem below are
        // shares of the thirty-two the root came to.
        await view.SetHtml(
            "<html style='font-size:2rem'><body style='margin:0'>" +
            "<div style='width:10rem;height:10px'></div></body></html>");

        Assert.That(Box(view, "div").BorderBox.Width, Is.EqualTo(320).Within(0.01));
    }

    [Test]
    public async Task ANextDocumentIsNotMeasuredAgainstTheLastOnesRoot()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html style='font-size:62.5%'><body style='margin:0'>" +
            "<div style='width:10rem;height:10px'></div></body></html>");

        Assert.That(Box(view, "div").BorderBox.Width, Is.EqualTo(100).Within(0.01));

        // The device is one object over the life of the view and the root size is a fact about the
        // document in it, so a page that says nothing about its own root has to go back to the size a
        // reader does rather than keep the ten the last page asked for.
        await view.SetHtml(Document("<div style='width:10rem;height:10px'></div>"));

        Assert.That(Box(view, "div").BorderBox.Width, Is.EqualTo(160).Within(0.01));
    }

    [Test]
    public async Task EmBorderRadiusIsRelativeToTheElementFontSize()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            "<div style='width:100px;height:100px;font-size:20px;border-radius:0.5em'></div>"));

        Assert.That(Box(view, "div").Radii.TopLeft.Horizontal, Is.EqualTo(10).Within(0.01));
    }

    [Test]
    public async Task PercentageLineHeightIsRelativeToTheElementFontSize()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<p style='margin:0;font-size:20px;line-height:150%'>a</p>"));

        Assert.That(
            Walk(view).First(node => node.Kind == LayoutKind.Line).BorderBox.Height,
            Is.EqualTo(30).Within(0.01),
            "150% of the paragraph's own twenty pixels, not a share of the viewport it is read through");
    }

    [Test]
    public async Task CalcLineHeightKeepsLineHeightsOwnUnits()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<p style='margin:0;font-size:20px;line-height:calc(150%)'>a</p>"));
        Assert.That(OneLineHeight(view), Is.EqualTo(30).Within(0.01),
            "a percentage in a line-height calc is of the element's font size, not the viewport");

        await view.SetHtml(Document("<p style='margin:0;font-size:20px;line-height:calc(2 * 10px + 50%)'>b</p>"));
        Assert.That(OneLineHeight(view), Is.EqualTo(30).Within(0.01),
            "a length and a percentage fold together, each against its own basis");

        await view.SetHtml(Document("<p style='margin:0;font-size:20px;line-height:calc(3)'>c</p>"));
        Assert.That(OneLineHeight(view), Is.EqualTo(60).Within(0.01),
            "a plain number is a multiple of the size - read as a length it has no unit to convert");
    }

    [Test]
    public async Task IcIsTheAdvanceOfAnIdeograph()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<div style='width:100px;height:10px;font-size:20px;padding-left:0.125ic'></div>"));

        // The fake face advances every character by five pixels, so an ic is five and an eighth is
        // five eighths - not the half em ex and ch are read as.
        Assert.That(Box(view, "div").Padding.Left, Is.EqualTo(0.625).Within(0.001));
    }

    [Test]
    public async Task AnIcFontSizeIsRelativeToTheParentAdvance()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<div style='font-size:2ic'>x</div>"));

        Assert.That(TextFontSize(view, "x"), Is.EqualTo(10).Within(0.01), "two ic of the parent's face");
    }

    [Test]
    public async Task ChIsTheFaceAdvanceOnTheClampsAsWellAsTheSizes()
    {
        using var view = CreateView();

        // The fake face advances every character by five pixels, so eight ch is forty: the clamps are
        // written in the same face as the sizes, and a fold handed only the font size would answer with
        // the half em and give thirty-two of a twenty-pixel line (CSS Values 4 §4.4).
        await view.SetHtml(Document("<div style='width:100px;font-size:20px;min-height:8ch'></div>"));

        Assert.That(Box(view, "div").BorderBox.Height, Is.EqualTo(40).Within(0.01));
    }

    private static Double OneLineHeight(HutuViewInternal view) =>
        Walk(view).First(node => node.Kind == LayoutKind.Line).BorderBox.Height;

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

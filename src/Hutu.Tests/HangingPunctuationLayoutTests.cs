using Hutu.Rendering.Layout;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers where <c>hanging-punctuation</c> lets a glyph stand: outside the line's start and end edges,
/// within the indent or the margin, against the edge the line's direction begins at, in a column, and
/// out of the room the content asks for (CSS Text 3 §8.2, §8.2.1).
/// </summary>
/// <remarks>
/// The fake face advances every character by half its size, so a glyph at ten pixels takes five and a
/// run of four takes twenty.
/// </remarks>
[TestFixture]
public sealed class HangingPunctuationLayoutTests
{
    private const String Face = "margin:0;font-size:10px;line-height:1;font-family:Ahem;";

    [Test]
    public async Task AnOpeningGlyphStandsBeforeTheLine()
    {
        using var view = CreateView();

        await view.SetHtml(Document($"<p style='{Face}hanging-punctuation:first'>(abc</p>"));

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "(").BorderBox.X, Is.EqualTo(-5).Within(0.001));
            Assert.That(Text(view, "abc").BorderBox.X, Is.EqualTo(0).Within(0.001));
        });
    }

    [Test]
    public async Task AClosingGlyphStandsPastTheLine()
    {
        using var view = CreateView();

        await view.SetHtml(Document($"<p style='{Face}width:15px;hanging-punctuation:last'>abc)</p>"));

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "abc").BorderBox.X, Is.EqualTo(0).Within(0.001));
            Assert.That(Text(view, ")").BorderBox.X, Is.EqualTo(15).Within(0.001));
        });
    }

    [Test]
    public async Task AClosingGlyphGivesUpItsRoomToTheAlignment()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            $"<p style='{Face}width:30px;text-align:right;hanging-punctuation:last'>abc)</p>"));

        // The line is measured without the glyph, so the end-aligned content ends at the line's edge
        // and the glyph stands past it.
        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "abc").BorderBox.X, Is.EqualTo(15).Within(0.001));
            Assert.That(Text(view, ")").BorderBox.X, Is.EqualTo(30).Within(0.001));
        });
    }

    [Test]
    public async Task AllowEndKeepsAStopTheLineCannotHold()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            $"<p style='{Face}width:20px;hanging-punctuation:allow-end'>ab c\u3001</p>"));

        // Four characters fill the line and the stop hangs past it, rather than the line ending at
        // the space before them.
        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "ab c").BorderBox.Width, Is.EqualTo(20).Within(0.001));
            Assert.That(Text(view, "\u3001").BorderBox.X, Is.EqualTo(20).Within(0.001));
        });
    }

    [Test]
    public async Task AllowEndLeavesAStopTheLineHolds()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            $"<p style='{Face}width:50px;hanging-punctuation:allow-end'>ab c\u3001</p>"));

        Assert.That(Text(view, "ab c\u3001").BorderBox.Width, Is.EqualTo(25).Within(0.001));
    }

    [Test]
    public async Task ForceEndHangsAStopEvenWhereItFits()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            $"<p style='{Face}width:50px;text-align:right;hanging-punctuation:force-end'>ab,</p>"));

        // The comma gives up its advance although it fits, so the end-aligned text ends at the line's
        // edge and the comma stands past it.
        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "ab").BorderBox.X, Is.EqualTo(40).Within(0.001));
            Assert.That(Text(view, ",").BorderBox.X, Is.EqualTo(50).Within(0.001));
        });
    }

    [Test]
    public async Task AColumnHangsItsOpeningGlyphFromTheTop()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            $"<p style='{Face}writing-mode:vertical-rl;height:50px;width:20px;hanging-punctuation:first'>(abc</p>"));

        // The inline axis of a column runs down the page, so the start edge is the top one.
        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "(").BorderBox.Y, Is.EqualTo(-5).Within(0.001));
            Assert.That(Text(view, "abc").BorderBox.Y, Is.EqualTo(0).Within(0.001));
        });
    }

    [Test]
    public async Task ARightToLeftLineHangsItsOpeningGlyphAtTheRight()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            $"<p dir='rtl' style='{Face}width:30px;hanging-punctuation:first'>(abc</p>"));

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "abc").BorderBox.X, Is.EqualTo(15).Within(0.001));
            Assert.That(Text(view, "(").BorderBox.X, Is.EqualTo(30).Within(0.001));
        });
    }

    [Test]
    public async Task ABorderBetweenTheGlyphAndTheEdgeKeepsItOnTheLine()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            $"<p style='{Face}hanging-punctuation:first'><span style='border-left:2px solid black'>(</span>abc</p>"));

        Assert.That(Text(view, "(").BorderBox.X, Is.EqualTo(2).Within(0.001));
    }

    [Test]
    public async Task TheWidestLineOfAFloatGivesUpTheHungGlyph()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            $"<div style='{Face}float:left;hanging-punctuation:first'>(abc</div>"));

        // The float shrinks to fit the line as the line is measured, and the hanging glyph is not part
        // of it: the box is three characters wide although eleven are drawn.
        Assert.That(Block(view).BorderBox.Width, Is.EqualTo(15).Within(0.001));
    }

    [Test]
    public async Task TheLastLineOfAShrinkToFitBoxGivesUpItsClosingGlyph()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            $"<div style='{Face}float:left;hanging-punctuation:last'>abc)</div>"));

        Assert.That(Block(view).BorderBox.Width, Is.EqualTo(15).Within(0.001));
    }

    private static String Document(String content) =>
        "<html><body style='margin:0;font-size:10px;line-height:1'>" + content + "</body></html>";

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(200, 200);
        return view;
    }

    private static LayoutBox Text(HutuViewInternal view, String text) =>
        Walk(view).First(node => node.Kind == LayoutKind.Text && node.Text == text);

    private static LayoutBox Block(HutuViewInternal view) =>
        Walk(view).First(node => node.Kind == LayoutKind.Block && node.BorderBox.Width > 0 && node.Children.Any(child => child.Kind == LayoutKind.Line));

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
            foreach (var at in Walk(child))
            {
                yield return at;
            }
        }
    }
}

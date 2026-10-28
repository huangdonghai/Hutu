using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers where the marks of <c>text-emphasis</c> stand and what they cost the line: nothing, because
/// the marks are paint beyond the content edge of the text they mark (CSS Text Decoration 3 搂3.3).
/// </summary>
/// <remarks>
/// The fake measurer sets every character five pixels wide and every font at an ascent of eight and a
/// descent of two, whatever size it is asked for.
/// </remarks>
[TestFixture]
public sealed class EmphasisTests
{
    [Test]
    public async Task AMarkTakesNoRoomInTheLine()
    {
        using var view = CreateView();

        // Twenty pixels of line height around a ten-pixel font: the marks stand in the leading and the
        // line is no taller for them.
        await view.SetHtml(Document("ab", "text-emphasis: circle"));

        Assert.That(Lines(view).Single().BorderBox.Height, Is.EqualTo(20), "the line is its line-height");
        Assert.That(Marks(view, "ab").First().Band.Y, Is.EqualTo(0), "and the mark stands in the leading");
    }

    [Test]
    public async Task AMarkStandsJustOutsideTheContentEdgeItNames()
    {
        using var view = CreateView();

        // The line is twenty tall with a font ten tall, so its baseline is thirteen from the line's top
        // and the content edge eight above that. A mark is half the font, five pixels, so it stands from
        // zero to five above the text; one asked for under it stands from fifteen down.
        await view.SetHtml(Document("ab", "text-emphasis: circle"));
        Assert.That(Marks(view, "ab").First().Band.Y, Is.EqualTo(0), "over: the mark ends at the content edge");
        Assert.That(Marks(view, "ab").First().Band.Height, Is.EqualTo(5), "the strip is half the font");

        using var under = CreateView();
        await under.SetHtml(Document("ab", "text-emphasis: circle; text-emphasis-position: under"));

        Assert.That(
            Marks(under, "ab").First().Band.Y,
            Is.EqualTo(15),
            "under: the mark begins at the content edge below the baseline");
    }

    [Test]
    public async Task AMarkForAColumnStandsOnTheSideThePageNames()
    {
        using var view = CreateView();

        // A column reads its side from the value rather than from over and under: `right` is the side
        // the glyphs are turned towards, whatever way the columns march (CSS Text Decoration 3 搂3.3).
        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:2;" +
            "writing-mode:vertical-lr;height:200px;text-emphasis: circle'>ab</p></body></html>");

        var band = Marks(view, "ab").First().Band;

        Assert.That(band.X, Is.GreaterThan(Text(view, "ab").BorderBox.X), "vertical-lr: `right` is the far side");
    }

    private static String Document(String content, String emphasis) =>
        "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:2;" +
        emphasis + "'>" + content + "</p></body></html>";

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(600, 600);
        return view;
    }

    private static List<EmphasisMark> Marks(HutuViewInternal view, String text) =>
        Text(view, text).Marks ?? throw new AssertionException($"'{text}' carries no marks.");

    private static LayoutBox Text(HutuViewInternal view, String text) =>
        Walk(view).SingleOrDefault(node => node.Kind == LayoutKind.Text && node.Text == text)
            ?? throw new AssertionException($"The layout holds no text box for '{text}'.");

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



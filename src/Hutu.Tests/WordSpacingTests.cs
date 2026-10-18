using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers <c>word-spacing</c>: the room it adds where two words meet, how many times it is added when a
/// run of spaces was written there, and the separators it leaves to the font - the ideographic space among
/// them.
/// </summary>
/// <remarks>
/// The fake measurer gives every character five pixels, so the space of a written document is five and a
/// style that asks for four more makes nine. A width is the layout's own arithmetic under it, which is
/// what makes these assertions independent of the machine's fonts.
/// </remarks>
[TestFixture]
public sealed class WordSpacingTests
{
    [Test]
    public async Task RoomIsAddedWhereTwoWordsMeet()
    {
        using var view = CreateView();

        await view.SetHtml(Page("<p style='word-spacing:4px'>ab cd</p>"));

        Assert.That(
            Text(view, "ab cd").BorderBox.Width,
            Is.EqualTo(29),
            "four characters of five, the space of five, and the room of four");
    }

    [Test]
    public async Task NormalLeavesTheWordsAsTheyWere()
    {
        using var view = CreateView();

        await view.SetHtml(Page("<p>ab cd</p>"));

        Assert.That(Text(view, "ab cd").BorderBox.Width, Is.EqualTo(25), "the initial value adds nothing");
    }

    [Test]
    public async Task ACollapsedRunOfSpacesIsChargedOnce()
    {
        using var view = CreateView();

        // Three spaces written between two words are drawn as one, and the room is added to the separator
        // that is drawn rather than to each of the three that were written.
        await view.SetHtml(Page("<p style='word-spacing:4px'>a   b</p>"));

        Assert.That(Text(view, "a b").BorderBox.Width, Is.EqualTo(19));
    }

    [Test]
    public async Task APreformattedRunIsChargedForEachSpaceItDraws()
    {
        using var view = CreateView();

        // Preformatted text keeps both spaces, and both of them separate the words the line is made of.
        await view.SetHtml(Page("<p style='white-space:pre;word-spacing:4px'>a  b</p>"));

        Assert.That(Text(view, "a  b").BorderBox.Width, Is.EqualTo(28), "two spaces of nine each");
    }

    [Test]
    public async Task ASpaceThatOpensTheTextIsNotBetweenTwoWords()
    {
        using var view = CreateView();

        // The room is what is added between words, and a preserved leading space has a word only after it.
        await view.SetHtml(Page("<p style='white-space:pre;word-spacing:4px'> ab</p>"));

        Assert.That(Text(view, " ab").BorderBox.Width, Is.EqualTo(15), "the space of five, uncharged");
    }

    [Test]
    public async Task ANegativeRoomNarrowsTheGap()
    {
        using var view = CreateView();

        await view.SetHtml(Page("<p style='word-spacing:-2px'>ab cd</p>"));

        Assert.That(Text(view, "ab cd").BorderBox.Width, Is.EqualTo(23));
    }

    [Test]
    public async Task TheIdeographicSpaceIsLeftToTheFont()
    {
        using var view = CreateView();

        // U+3000 reads as a gap and is not a word separator: word-spacing says the effect on the other
        // space characters is the font's, and Chromium, measured, adds nothing to this one.
        await view.SetHtml(Page("<p style='word-spacing:4px'>a" + (Char)0x3000 + "b</p>"));

        Assert.That(
            Text(view, "a" + (Char)0x3000 + "b").BorderBox.Width,
            Is.EqualTo(15),
            "three characters of five, and no room added");
    }

    [Test]
    public async Task ThePropertyTravelsDownToTheTextItInherits()
    {
        using var view = CreateView();

        await view.SetHtml(Page("<div style='word-spacing:4px'><span>ab cd</span></div>"));

        Assert.That(Text(view, "ab cd").BorderBox.Width, Is.EqualTo(29));
    }

    [Test]
    public async Task TheRoomCountsTowardWhereTheLineComesApart()
    {
        // Two words of ten and a separator of five come to twenty-five, which is exactly the width the
        // paragraph is given. The four added to the separator is what sends the second word below.
        using var spaced = CreateView();
        await spaced.SetHtml(Page("<p style='width:25px;word-spacing:4px'>ab cd</p>"));

        using var plain = CreateView();
        await plain.SetHtml(Page("<p style='width:25px'>ab cd</p>"));

        Assert.That(Lines(spaced), Is.EqualTo(2), "the room made the line too long");
        Assert.That(Lines(plain), Is.EqualTo(1), "and without it the words stand together");
    }

    private static String Page(String content) =>
        "<html><body style='margin:0;font-size:10px;line-height:2'>" + content + "</body></html>";

    private static HutuViewInternal CreateView(Int32 viewportWidth = 600)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static Int32 Lines(HutuViewInternal view) =>
        Walk(view).Count(node => node.Kind == LayoutKind.Line);

    private static LayoutBox Text(HutuViewInternal view, String text) =>
        Walk(view).Single(node => node.Kind == LayoutKind.Text && node.Text == text);

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

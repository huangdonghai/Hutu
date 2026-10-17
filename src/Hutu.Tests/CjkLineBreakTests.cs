using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the places a line may come apart without a space to break at: the ideographic and fullwidth
/// forms, the Hangul syllables, and the ideographs the extension planes carry.
/// </summary>
/// <remarks>
/// The measurer is the fixed one, five pixels a character of the string, so an ideograph from above
/// U+FFFF is worth two of those - which is exactly why the break test has to be made on the code point
/// and not on the character under it.
/// </remarks>
[TestFixture]
public sealed class CjkLineBreakTests
{
    [Test]
    public async Task ALineBreaksBetweenHangulSyllables()
    {
        using var view = CreateView();

        await view.SetHtml(Document("가가가가", width: 10));

        Assert.That(
            Lines(view).Count(),
            Is.EqualTo(2),
            "a Korean line comes apart between syllables without any space to help it");
    }

    [Test]
    public async Task ALineBreaksBetweenIdeographsOfTheExtensionPlanes()
    {
        using var view = CreateView();

        // U+20000 is one character and two of the measurer's, so two of them fill the ten pixels the
        // paragraph is given and each stands on a line of its own.
        await view.SetHtml(Document("\U00020000\U00020001", width: 10));

        Assert.That(
            Lines(view).Count(),
            Is.EqualTo(2),
            "the ideographs a name is written with are as breakable as the ones inside the BMP");
        Assert.That(
            Text(view, "\U00020000").BorderBox.Width,
            Is.EqualTo(10),
            "and the pair is measured and broken as one character, not as two halves");
    }

    [Test]
    public void TheBreakTestIsMadeOnTheCodePoint()
    {
        Assert.That(TextChunker.IsCjk("가가가", 0), Is.True, "a syllable is a place to break");
        Assert.That(TextChunker.IsCjk("\U00020000", 0), Is.True, "so is one of the rare ideographs");
        Assert.That(
            TextChunker.CodePointAt("\U00020000", 0),
            Is.EqualTo(0x20000),
            "which is the code point the pair stands for");
        Assert.That(
            TextChunker.IsCjk("hello", 0),
            Is.False,
            "a Latin letter is not, and a word of them stays whole");
    }

    [Test]
    public void HangulBreaksLikeAnIdeographAndIsSetLikeAnAlphabetic()
    {
        Assert.That(
            TextChunker.IsUpright('가'),
            Is.False,
            "a column turns Hangul, so the break set and the upright set are not the same question");
        Assert.That(TextChunker.IsUpright('漢'), Is.True, "an ideograph keeps its face in a column");
    }

    private static String Document(String content, Int32 width = 0) =>
        "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:1" +
        (width > 0 ? ";width:" + width + "px" : String.Empty) +
        "'>" + content + "</p></body></html>";

    private static HutuViewInternal CreateView(Int32 viewportWidth = 600)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static IEnumerable<LayoutBox> Lines(HutuViewInternal view) =>
        Walk(view).Where(node => node.Kind == LayoutKind.Line);

    private static LayoutBox Text(HutuViewInternal view, String text) =>
        Walk(view).SingleOrDefault(node => node.Kind == LayoutKind.Text && node.Text == text)
            ?? throw new AssertionException($"The layout holds no text box for '{text}'.");

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

using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using Hutu.Skia;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers <c>letter-spacing</c>: the space it adds after every character, the room it takes along the
/// line, and the fact that it reaches the painter as well as the layout.
/// </summary>
/// <remarks>
/// The fake measurer gives every character five pixels whatever the font, so a width is the layout's
/// own arithmetic and the assertions do not depend on the machine's fonts.
/// </remarks>
[TestFixture]
public sealed class LetterSpacingTests
{
    [Test]
    public async Task SpacingIsAddedAfterEveryCharacter()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<p style='letter-spacing:2px'>abc</p>"));

        Assert.That(
            Text(view, "abc").BorderBox.Width,
            Is.EqualTo(21),
            "three characters of five pixels plus two each, the last one included");
    }

    [Test]
    public async Task NormalSpacingLeavesTheRunAlone()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<p>abc</p>"));

        Assert.That(Text(view, "abc").BorderBox.Width, Is.EqualTo(15));
    }

    [Test]
    public async Task SpacingAppliesToSpacesToo()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<p style='letter-spacing:2px'>a b</p>"));

        Assert.That(Text(view, "a b").BorderBox.Width, Is.EqualTo(21), "the space is a character like any other");
    }

    [Test]
    public async Task SpacingFollowsTheFontSizeItIsDeclaredIn()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<p style='font-size:20px;letter-spacing:0.5em'>ab</p>"));

        // Half the element's twenty pixels is ten a character, not half of the sixteen-pixel root.
        Assert.That(Text(view, "ab").BorderBox.Width, Is.EqualTo(30));
    }

    [Test]
    public async Task SpacingMakesALineWrapEarlier()
    {
        using var view = CreateView(40);

        await view.SetHtml(Document(
            "<p style='letter-spacing:4px'>ab cd</p>",
            width: 40));

        Assert.That(Lines(view).Count(), Is.EqualTo(2), "the spaced words no longer fit on one line");
    }

    [Test]
    public async Task SpacingReachesThePainter()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<p style='letter-spacing:3px'>ab</p>"));

        var run = view.Paint!.Commands.OfType<TextCommand>().Single(command => command.Text == "ab");

        Assert.That(run.LetterSpacing, Is.EqualTo(3));
    }

    [Test]
    public async Task AnIdeographOfTheExtensionPlanesIsSpacedAsOneCharacter()
    {
        using var view = CreateView();

        // The fake measurer counts the units the string is stored in, so the ideograph is worth ten of
        // its pixels and the letter after it five: fifteen of the encoding. The spacing is charged by
        // the two characters they stand for, and not by the three units that spell them out.
        var text = Char.ConvertFromUtf32(0x20000) + "x";

        await view.SetHtml(Document("<p style='letter-spacing:2px'>" + text + "</p>"));

        Assert.That(
            Text(view, text).BorderBox.Width,
            Is.EqualTo(19),
            "fifteen of the encoding, and four for the two characters of it");
    }

    [Test]
    public async Task ACharacterUnderAMarkIsSpacedOnce()
    {
        using var view = CreateView();

        // A letter with a combining mark over it is two of the encoding and one to read: one space,
        // not two, and the letter that follows it stands one space from the character it was written
        // after rather than two.
        var text = "e" + (Char)0x0301 + "z";

        await view.SetHtml(Document("<p style='letter-spacing:3px'>" + text + "</p>"));

        Assert.That(
            Text(view, text).BorderBox.Width,
            Is.EqualTo(21),
            "fifteen of the encoding, and six for the two characters of it");
    }

    [Test]
    public async Task ASpacedRunGrowsByTheCharactersOfIt()
    {
        // Against the real shaper, where no advance is known beforehand: the answer is the difference
        // between the same text spaced and unspaced, which is one space per character of it whatever
        // the font measured the characters at.
        using var measurer = new SkiaTextMeasurer();

        var text = Char.ConvertFromUtf32(0x20000) + Char.ConvertFromUtf32(0x20001);

        using var plain = new HutuViewInternal(measurer);
        using var spaced = new HutuViewInternal(measurer);
        plain.SetViewportSize(200, 200);
        spaced.SetViewportSize(200, 200);

        await plain.SetHtml(Document("<p>" + text + "</p>"));
        await spaced.SetHtml(Document("<p style='letter-spacing:20px'>" + text + "</p>"));

        Assert.That(
            Text(spaced, text).BorderBox.Width - Text(plain, text).BorderBox.Width,
            Is.EqualTo(40).Within(0.001),
            "two characters, one space after each, and no space for the units that stand for them");
    }

    /// <summary>Gets the document under test: a paragraph with no margin of its own.</summary>
    private static String Document(String content, Int32 width = 0) =>
        "<html><body style='margin:0;font-size:10px;line-height:2'" +
        (width > 0 ? ";width:" + width + "px" : String.Empty) +
        ">" + content + "</body></html>";

    private static HutuViewInternal CreateView(Int32 viewportWidth = 600)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static LayoutBox Text(HutuViewInternal view, String text) =>
        Walk(view).First(node => node.Kind == LayoutKind.Text && node.Text == text);

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

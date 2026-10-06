using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
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

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
/// Covers the important annotation of a declaration: that it outranks a later declaration of the same
/// name, that the keyword is the keyword whatever its case, and that white space between the mark and the
/// word is allowed - CSS Syntax 3 §5.4.3 writes the annotation as a mark, any white space, and the
/// keyword. A page that spaces it, which is how the specifications themselves tend to write it, is not
/// writing something else; and an annotation that is not the keyword leaves the declaration invalid, which
/// is the other half of the same rule.
/// </summary>
/// <remarks>
/// The answers are read off the colour the engine drew each paragraph's text in, because what the
/// annotation decides is which declaration the cascade answers with - the value the box came to is the
/// only place that shows.
/// </remarks>
[TestFixture]
public sealed class ImportantDeclarationTests
{
    private const UInt32 Red = 0xFFFF_0000;
    private const UInt32 Blue = 0xFF00_00FF;

    [Test]
    public async Task AnImportantDeclarationOutranksTheOneWrittenAfterIt()
    {
        var ink = await Ink("p.one { color: red !important } p.two { color: blue }");

        Assert.That(ink, Is.EqualTo(Red), "the important one wins though it came first");
    }

    [Test]
    public async Task AMarkAndAWordSeparatedBySpaceAreOneAnnotation()
    {
        var ink = await Ink("p.one { color: red ! important } p.two { color: blue }");

        Assert.That(
            ink,
            Is.EqualTo(Red),
            "white space between the mark and the keyword is white space and nothing more");
    }

    [Test]
    public async Task TheKeywordIsTheKeywordWhateverItsCase()
    {
        var ink = await Ink("p.one { color: red !IMPORTANT } p.two { color: blue }");

        Assert.That(ink, Is.EqualTo(Red), "the keyword does not care how it is cased");
    }

    [Test]
    public async Task AWordThatIsNotTheKeywordLeavesTheDeclarationInvalid()
    {
        // An annotation the grammar does not know invalidates the declaration that carries it, which is
        // what lets the value written below stand - and the letters standing where the keyword should be
        // must not be read as the keyword.
        var ink = await Ink("p.one { color: red !imp } p.two { color: blue }");

        Assert.That(ink, Is.EqualTo(Blue), "no annotation was written, so nothing is important");
    }

    [Test]
    public async Task AnImportantShorthandTakesTheLineItsEarlierOneAskedFor()
    {
        // The shape text-decoration-zorder-1-standards states: one rule asks for the line and a later
        // one, written with the space between the mark and the word, says there is none.
        var underlined = await Underlined(
            "p.a { text-decoration: underline } p.b { text-decoration: none ! important }",
            "<p class='a b'>text</p>");

        Assert.That(underlined, Is.False, "the important shorthand wins and leaves the box without a line");
    }

    /// <summary>
    /// Gets the colour one paragraph was drawn in, where the page gives it both classes at once and so
    /// puts the two declarations in front of the same cascade.
    /// </summary>
    private static async Task<UInt32> Ink(String css)
    {
        using var view = new HutuViewInternal(new SkiaTextMeasurer());
        view.SetViewportSize(400, 200);

        await view.SetHtml(
            "<html><head><style>" + css + "</style></head><body style='margin:0'>" +
            "<p class='one two'>text</p></body></html>");

        var paint = view.Paint!;
        var run = Walk(view.Layout!.Root).Single(box => box.Kind == LayoutKind.Text);
        var command = paint.Commands.OfType<TextCommand>().SingleOrDefault(c => c.Bounds.Equals(run.BorderBox));

        Assert.That(command, Is.Not.Null, "the paragraph was drawn");

        return command!.Color;
    }

    private static async Task<Boolean> Underlined(String css, String body)
    {
        using var view = new HutuViewInternal(new SkiaTextMeasurer());
        view.SetViewportSize(400, 200);

        await view.SetHtml($"<html><head><style>{css}</style></head><body style='margin:0'>{body}</body></html>");

        return Walk(view.Layout!.Root)
            .Where(box => box.Kind == LayoutKind.Text)
            .Any(box => box.Source.Style.HasUnderline);
    }

    private static IEnumerable<LayoutBox> Walk(LayoutBox node)
    {
        yield return node;

        foreach (var child in node.Children)
        {
            foreach (var under in Walk(child))
            {
                yield return under;
            }
        }
    }
}

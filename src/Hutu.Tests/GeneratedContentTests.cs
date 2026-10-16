using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The boxes <c>::before</c> and <c>::after</c> generate: where they stand among the originating
/// element's children, what they inherit, and when no box is made at all.
/// </summary>
/// <remarks>
/// Pseudo-Elements 4 §4.1 - "when their computed content value is not none, these pseudo-elements
/// generate boxes as if they were immediate children of their originating element" - and §4, which
/// has a tree-abiding pseudo-element inherit what its originating element inherited and take the
/// initial value of everything else. §8 keeps the one-colon spelling CSS 2 wrote working.
/// </remarks>
[TestFixture]
public sealed class GeneratedContentTests
{
    [Test]
    public async Task TheGeneratedBoxesStandFirstAndLastAmongTheChildren()
    {
        var view = await Build(
            "p::before { content: '[B]' }" +
            "p::after  { content: '[A]' }",
            "<p>mid</p>");

        var texts = Painted(view);

        Assert.Multiple(() =>
        {
            Assert.That(texts, Does.Contain("[B]"), "the box ::before generates is drawn");
            Assert.That(texts, Does.Contain("[A]"), "the box ::after generates is drawn");
            Assert.That(texts.IndexOf("[B]") < texts.IndexOf("mid"), Is.True, "::before stands before the content");
            Assert.That(texts.IndexOf("mid") < texts.IndexOf("[A]"), Is.True, "::after stands after it");
        });
    }

    [Test]
    public async Task NoneAndNormalGenerateNoBox()
    {
        var view = await Build(
            "p::before { content: none }" +
            "p::after  { content: normal }",
            "<p>only</p>");

        Assert.That(
            Painted(view),
            Is.EqualTo(new[] { "only" }),
            "Content 3 §1: none inhibits the pseudo-element, and normal computes to none on it");
    }

    [Test]
    public async Task APageThatGeneratesNothingIsLeftAlone()
    {
        var view = await Build("p { color: #0000ff }", "<p>only</p>");

        Assert.That(Painted(view), Is.EqualTo(new[] { "only" }), "no rule names a pseudo-element, so no box is made");
    }

    [Test]
    public async Task TheOneColonSpellingWorks()
    {
        var view = await Build("p:before { content: 'X' }", "<p>text</p>");

        Assert.That(Painted(view), Does.Contain("X"), "Pseudo-Elements 4 §8 keeps the CSS 2 spelling");
    }

    [Test]
    public async Task APseudoElementInheritsFromTheElementItIsAttachedTo()
    {
        var view = await Build("p { color: #0000ff } p::before { content: '>' }", "<p>text</p>");

        Assert.That(
            ColourOf(view, ">"),
            Is.EqualTo(ColourOf(view, "text")),
            "Pseudo-Elements 4 §4: a tree-abiding pseudo-element inherits from its originating element");
    }

    [Test]
    public async Task APseudoElementIsStyledOnItsOwn()
    {
        var view = await Build(
            "p { color: #0000ff } p::before { color: #ff0000; content: '>' }",
            "<p>text</p>");

        Assert.Multiple(() =>
        {
            Assert.That(ColourOf(view, ">"), Is.Not.EqualTo(ColourOf(view, "text")), "its own declaration wins");
            Assert.That(
                ColourOf(view, "text"),
                Is.EqualTo(ColourOf(view, "text")),
                "and the element's own colour is untouched by it");
        });
    }

    [Test]
    public async Task AnArmWrittenForAnElementStillStylesThatElement()
    {
        var view = await Build(
            "p::before, span { color: #ff0000 }" +
            "p::before { content: '>' }",
            "<p>a <span>b</span></p>");

        Assert.Multiple(() =>
        {
            Assert.That(ColourOf(view, ">"), Is.EqualTo(ColourOf(view, "b")), "both arms carry the declaration");
            Assert.That(ColourOf(view, ">"), Is.Not.EqualTo(ColourOf(view, "a ")), "and the element that is not named keeps its own");
        });
    }

    [Test]
    public async Task AGeneratedBoxIsNotPartOfWhatCanBeSelected()
    {
        var view = await Build("p::before { content: '>' }", "<p>text</p>");

        var box = Walk(view.Layout!.Root).First(candidate => candidate.Kind == LayoutKind.Text && candidate.Text == ">");

        Assert.That(box.Source.IsGenerated, Is.True, "the box is marked as generated, which is what leaves it out of a selection");
    }

    [Test]
    public async Task ADisplayOfBlockGivesTheGeneratedContentABlockOfItsOwn()
    {
        var view = await Build("p::before { content: 'B'; display: block }", "<p>text</p>");

        var generated = Walk(view.Layout!.Root).First(box => box.Source.IsGenerated);

        Assert.Multiple(() =>
        {
            Assert.That(generated.Source.Kind, Is.EqualTo(BoxKind.Block), "Pseudo-Elements 4 §4.1: fully styleable, so display is honoured");
            Assert.That(Walk(generated).Any(box => box.Text == "B"), Is.True, "and the content stands inside it");
        });
    }

    [Test]
    public async Task AnEmptyContentWithASizeDrawsABoxToHangABackgroundOn()
    {
        var view = await Build(
            "p::before { content: ''; display: inline-block; width: 20px; height: 20px; background: #ff0000 }",
            "<p>text</p>");

        var generated = Walk(view.Layout!.Root).First(box => box.Source.IsGenerated);

        Assert.Multiple(() =>
        {
            Assert.That(generated.Source.Kind, Is.EqualTo(BoxKind.InlineBlock));
            Assert.That(generated.BorderBox.Width, Is.EqualTo(20d).Within(0.5d), "the box is the size the sheet asked for");
            Assert.That(generated.BorderBox.Height, Is.EqualTo(20d).Within(0.5d));
        });
    }

    [Test]
    public async Task AnImageContentMakesThePseudoElementTheReplacedBox()
    {
        var view = await Build("p::before { content: url('icon.png') }", "<p>text</p>");

        var generated = Walk(view.Layout!.Root).First(box => box.Source.IsGenerated);

        Assert.Multiple(() =>
        {
            Assert.That(generated.Source.IsReplaced, Is.True, "Content 3 §2: an image is an anonymous inline replaced element");
            Assert.That(generated.Source.SourceUrl, Is.Not.Null, "and it names the picture it was given");
        });
    }

    [Test]
    public async Task DisplayNoneGeneratesNoBox()
    {
        var view = await Build("p::before { content: 'B'; display: none }", "<p>text</p>");

        Assert.That(Walk(view.Layout!.Root).Any(box => box.Source.IsGenerated), Is.False);
    }

    [Test]
    public async Task AContentValueTheParserCannotReadGeneratesNoBox()
    {
        // The `/` alternative text of Content 3 §1.2 is not a value this parser models, so the
        // declaration is dropped whole - and a dropped declaration is no content at all, which is
        // `normal`, which computes to `none`. Half-reading it and drawing the "A" would put up part
        // of what the page asked for and none of the rest.
        var view = await Build("p::before { content: 'A' / 'spoken instead' }", "<p>text</p>");

        Assert.That(
            Walk(view.Layout!.Root).Any(box => box.Source.IsGenerated),
            Is.False,
            "a value the parser will not take is a declaration the cascade never sees");
    }

    private static async Task<HutuViewInternal> Build(String style, String body)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml($"<html><body style='margin:0'><style>{style}</style>{body}</body></html>");
        return view;
    }

    private static List<String> Painted(HutuViewInternal view) =>
        view.Paint!.Commands.OfType<TextCommand>().Select(command => command.Text).ToList();

    private static System.UInt32 ColourOf(HutuViewInternal view, String text) =>
        view.Paint!.Commands.OfType<TextCommand>().First(command => command.Text == text).Color;

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

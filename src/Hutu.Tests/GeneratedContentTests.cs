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
    public async Task AnAttributeIsDrawnAsTheTextTheElementCarries()
    {
        var view = await Build(
            "p::before { content: attr(data-mark) ':' attr(missing, 'fallback') }",
            "<p data-mark='here'>mid</p>");

        var texts = Painted(view);

        Assert.That(
            String.Concat(texts),
            Does.Contain("here:fallback"),
            "Content 3 §1 and §10.5: an attribute is read as the text it holds, and a missing one falls back");
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
    public async Task AnEmptyElementKeepsTheBoxItsPseudoElementWasGiven()
    {
        // CSS 2.1 §9.2.1: an inline box holds inline-level content and nothing else, so the run
        // ::before generates inside an empty <i> is content the <i> can hold as it stands. Wrapped in
        // the anonymous block §9.2.1.1 wants only for a block-level sibling, it would put a block-level
        // box inside an inline one, and the inline box loses its own box - which is how a font icon,
        // an empty element whose ::before carries the glyph, went missing altogether.
        var view = await Build("i { font-family: 'Icon' } i::before { content: '\\50' }", "<p>a<i></i>b</p>");

        var element = Walk(view.Layout!.Root).First(box => box.Source.Element?.LocalName == "i");

        Assert.Multiple(() =>
        {
            Assert.That(element.Source.Kind, Is.EqualTo(BoxKind.Inline), "the empty element keeps its own box");
            Assert.That(Painted(view), Does.Contain("P"), "and the glyph its ::before generates is drawn");
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
    public async Task TheAlternativeTextAfterASlashIsNotDrawn()
    {
        // Content 3 §1.2: what stands after the slash is spoken in place of the content rather than drawn
        // with it. The text before the slash is drawn, and neither the slash nor the words after it reach
        // the page - which is also what Chrome does with the same declaration.
        var view = await Build("p::before { content: 'A' / 'spoken instead' }", "<p>text</p>");

        var texts = Painted(view);

        Assert.Multiple(() =>
        {
            Assert.That(texts, Does.Contain("A"), "the content the page asked to be drawn is drawn");
            Assert.That(String.Concat(texts), Does.Not.Contain("spoken"), "and the alternative text is not");
        });
    }

    [Test]
    public async Task AGeneratedStringMadeOfVariablesIsPainted()
    {
        // The arm's own content is read through the scope the element's variables are resolved in, so
        // a generated string the page wrote as references is painted as the words they stand for.
        using var view = await Build(
            ":root { --a: 'hello'; --b: 'there' } #x::before { content: var(--a) ' ' var(--b) }",
            "<p id=x></p>");

        Assert.That(String.Concat(Painted(view)), Is.EqualTo("hello there"));
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

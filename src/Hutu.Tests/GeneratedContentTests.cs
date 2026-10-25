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

    [Test]
    public async Task ACounterIsDrawnWhereTheContentAsksForIt()
    {
        // CSS 2.1 §12.4.1: the counter an element resets is seen by that element's own generated
        // content, so the number written on the div is the number its ::after says.
        using var view = await Build(
            "div::after { content: counter(n) }",
            "<div style='counter-reset: n 7'></div>");

        Assert.That(String.Concat(Painted(view)), Is.EqualTo("7"));
    }

    [Test]
    public async Task ACounterIsSpelledInTheStyleTheContentNames()
    {
        using var view = await Build(
            "div::after { content: counter(n, lower-roman) ')' }",
            "<div style='counter-reset: n 4'></div>");

        Assert.That(String.Concat(Painted(view)), Is.EqualTo("iv)"));
    }

    [Test]
    public async Task AnIncrementFollowsTheResetWrittenOnTheSameElement()
    {
        // CSS Lists 3 §5.2: the increment is applied after the reset, so the two written together come
        // to the reset value plus the step rather than to the step alone.
        using var view = await Build(
            "div::after { content: counter(n) }",
            "<div style='counter-reset: n 5; counter-increment: n 2'></div>");

        Assert.That(String.Concat(Painted(view)), Is.EqualTo("7"));
    }

    [Test]
    public async Task ACounterSetOnASiblingIsSeenByTheSiblingsAfterIt()
    {
        // §5.2: the scope of a counter is the element that set it, its descendants, and its following
        // siblings and theirs - so the second div reads the first one's number.
        using var view = await Build(
            "div { counter-increment: n } div::after { content: counter(n) }",
            "<div></div><div></div><div></div>");

        Assert.That(String.Concat(Painted(view)), Is.EqualTo("123"));
    }

    [Test]
    public async Task TheCountersOfEveryLevelStandInOrderInCounters()
    {
        // §5.3: counters() gives the values of all the counters of that name in scope, outermost first.
        using var view = await Build(
            "div { counter-reset: section } span::after { content: counters(section, '.') }",
            "<div><div><span></span></div></div>");

        Assert.That(String.Concat(Painted(view)), Is.EqualTo("0.0"));
    }

    [Test]
    public async Task AnElementShownNothingCountsForNothing()
    {
        // CSS 2.1 §12.4.1: a counter is not incremented on an element whose display is none, so the
        // numbers the visible pair says are 1 and 2 rather than 1 and 3.
        using var view = await Build(
            "div { counter-increment: n } div::after { content: counter(n) } .gone { display: none }",
            "<div></div><div class=gone></div><div></div>");

        Assert.That(String.Concat(Painted(view)), Is.EqualTo("12"));
    }

    [Test]
    public async Task ASiblingStartingTheCountAgainLeavesTheOneBeforeItOut()
    {
        // §4.4.2: "newly instantiated counters replace identically-named counters originating from
        // previous siblings, but are added in addition to identically-named counters originating from
        // ancestor elements" - so a run of siblings is one counter read again, not a list of them.
        using var view = await Build(
            "div { counter-increment: n } div::after { content: counters(n, '-') '!' }",
            "<div></div><div></div>");

        Assert.That(String.Concat(Painted(view)), Is.EqualTo("1!2!"));
    }

    [Test]
    public async Task AGeneratedBoxCountsItself()
    {
        // §4.4.1, §4.5: the arm is a box of its own standing inside the element, so a counter it
        // increments is instantiated there and read back by its own content.
        using var view = await Build(
            "p::before { counter-increment: t; content: counter(t) }",
            "<p></p><p></p>");

        Assert.That(String.Concat(Painted(view)), Is.EqualTo("11"));
    }

    [Test]
    public async Task ACountersetKeptByTheParentIsSteppedByEachChild()
    {
        // CSS 2.1 §12.4.1: the children each step the one counter their parent opened, so the numbers
        // run on from it rather than restarting at each of them.
        using var view = await Build(
            "#t { counter-reset: c } #t span { counter-increment: c } #t span::before { content: counter(c) }",
            "<div id=t><span></span><span></span><span></span></div>");

        Assert.That(String.Concat(Painted(view)), Is.EqualTo("123"));
    }

    [Test]
    public async Task ABoxThatLeavesNoBoxLeavesTheCountingToWhatItHolds()
    {
        // CSS Display 3 §2.5 with Lists 3 §4.5: a `contents` element generates no box, so the elements
        // it holds stand in the run of siblings it stands in and count on from them.
        using var view = await Build(
            ".c { counter-increment: n } .c::after { content: counter(n) }",
            "<div style='display:contents'><div class=c></div></div><div class=c></div>");

        Assert.That(String.Concat(Painted(view)), Is.EqualTo("12"));
    }

    [Test]
    public async Task AStyleContainedBoxScopesTheIncrementToItsSubtree()
    {
        // CSS Contain 2 §3.4: the box itself is outside its own subtree, so its increment is the one
        // around it; the arm inside starts a fresh counter rather than stepping that one.
        using var view = await Build(
            "div { contain: style; counter-increment: n }" +
            "div::before, div::after { content: counters(n, '.') ' ' }" +
            "div::after { counter-increment: n 2 }",
            "<div></div>");

        Assert.That(String.Concat(Painted(view)), Is.EqualTo("1 1.2"));
    }

    [Test]
    public async Task AnIncrementInsideAStyleContainedSubtreeStartsFresh()
    {
        // The contained div's own 123 belongs to the outer scope; the spans inside may not step it, so
        // they instantiate a counter of their own and run 1, 2.
        using var view = await Build(
            "div { contain: style; counter-increment: c 123 }" +
            "span { counter-increment: c 1 }" +
            "span::before { content: counter(c) ' ' }",
            "<div><span></span><span></span></div>");

        Assert.That(String.Concat(Painted(view)), Is.EqualTo("1 2"));
    }

    [Test]
    public async Task AStyleContainedSubtreeDoesNotStepTheCounterAroundIt()
    {
        // A1 steps the outer counter to 1 and its arm reads it; B1 and B2 start a fresh counter inside
        // A1, so A2 - outside A1 - steps the outer one again to 2 and reads that.
        using var view = await Build(
            ".i { contain: style; counter-increment: c }" +
            ".i::before { content: '[' counter(c) '] ' }",
            "<div class=i>A1<div class=i>B1</div><div class=i>B2</div></div><div class=i>A2</div>");

        Assert.That(String.Concat(Painted(view)), Is.EqualTo("[1] A1[1] B1[2] B2[2] A2"));
    }

    [Test]
    public async Task QuotationMarksNestAndCloseWhereThePagePutsThem()
    {
        // CSS 2.1 §12.3.2: open-quote draws the pair at the depth the box stands at and steps in;
        // close-quote steps out and draws that pair's closing mark.
        using var view = await Build(
            "div { quotes: 'A' 'Z' '1' '9' }" +
            "div::before, span::before { content: open-quote }" +
            "div::after { content: close-quote }",
            "<div><span></span></div>");

        Assert.That(String.Concat(Painted(view)), Is.EqualTo("A19"));
    }

    [Test]
    public async Task AStyleContainedSubtreeKeepsItsQuotationDepthToItself()
    {
        // CSS Contain 2 §3.4: the subtree reads the depth its context implies, but stepping it inside
        // does not step the depth outside - so the span's mark is the second pair and the div's
        // closing mark is still the first pair's.
        using var view = await Build(
            "div { quotes: 'A' 'Z' '1' '9' }" +
            "span { contain: style }" +
            "div::before, span::before { content: open-quote }" +
            "div::after { content: close-quote }",
            "<div><span></span></div>");

        Assert.That(String.Concat(Painted(view)), Is.EqualTo("A1Z"));
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

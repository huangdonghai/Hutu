using Hutu.Rendering;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// What the CSS Variables grammar allows a declaration to say, and what comes of it once the
/// substitution runs: which spellings of a custom property name are the same name, which values a
/// custom property may hold, what a fallback may hold, and which declarations are thrown away before
/// any of that.
/// </summary>
/// <remarks>
/// Asserted on the ink a run is drawn with, because every one of these rules ends either in the
/// property taking its value or in the declaration not existing at all - and a colour is the shortest
/// way to tell the two apart. The pages are written the way the wpt series writes them: a rule that
/// colours the text one thing and a declaration that may or may not survive to colour it another.
/// </remarks>
[TestFixture]
public sealed class CustomPropertySyntaxTests
{
    [Test]
    public async Task ANameBeginningWithADigitIsStillADashName()
    {
        // A custom property name is an identifier that begins with two hyphens, and an identifier may
        // hold a digit as soon as it has got past that start (CSS Syntax 3 §4.2.7, CSS Variables 1 §2).
        var ink = await Inked("p { color: red } .x { --0: green; color: var(--0) }", "x");

        Assert.That(ink, Is.EqualTo("ff008000"));
    }

    [Test]
    public async Task AnEscapedDigitNamesThePropertyTheDigitNames()
    {
        var ink = await Inked("p { color: red } .x { --0: green; color: var(--\\30) }", "x");

        Assert.That(ink, Is.EqualTo("ff008000"));
    }

    [Test]
    public async Task ANameWrittenWithAnEscapeIsFoundByItsOwnSpelling()
    {
        var ink = await Inked("p { color: red } .x { --\\30 : green; color: var(--\\30) }", "x");

        Assert.That(ink, Is.EqualTo("ff008000"));
    }

    [Test]
    public async Task ALoneSurrogateEscapeNamesTheReplacementCharacter()
    {
        // An escape that stands for no character - a lone surrogate, or a number past the last code
        // point - names U+FFFD, which is what makes the two spellings one name (CSS Syntax 3 §4.3.7).
        var ink = await Inked(
            "p { color: red } .x { --\\d800 : green; color: var(--\\fffd) }",
            "x");

        Assert.That(ink, Is.EqualTo("ff008000"));
    }

    [Test]
    public async Task AnOutOfRangeEscapeNamesTheReplacementCharacter()
    {
        var ink = await Inked(
            "p { color: red } .x { --\\ffffff : green; color: var(--\\fffd) }",
            "x");

        Assert.That(ink, Is.EqualTo("ff008000"));
    }

    [Test]
    public async Task AValueOfNothingIsStillAValue()
    {
        // A custom property may be given nothing at all, and a value of nothing at all is still a
        // value: the reference to it substitutes to nothing rather than making the using declaration
        // invalid.
        var ink = await Inked("p { color: red } .x { --a: ; color: var(--a) green }", "x");

        Assert.That(ink, Is.EqualTo("ff008000"));
    }

    [Test]
    public async Task AFallbackOfNothingIsStillAFallback()
    {
        var ink = await Inked("p { color: red } .x { --a: var(--b , ); color: var(--a) green }", "x");

        Assert.That(ink, Is.EqualTo("ff008000"));
    }

    [Test]
    public async Task ABangInAFallbackThrowsTheDeclarationAway()
    {
        // The argument of a reference is its own top level, and a bang at a top level ends a
        // declaration - so the value is not a declaration value at all and the rule never had it
        // (CSS Variables 1 §3.1). What stands is the declaration the same rule made before it.
        var ink = await Inked(
            "p { color: red } .x { --a: green; --b: crimson; --a: var(--b,!) ; color: var(--a) }",
            "x");

        Assert.That(ink, Is.EqualTo("ff008000"));
    }

    [Test]
    public async Task ASemicolonInAFallbackThrowsADeclarationThatUsesItAway()
    {
        var ink = await Inked(
            "p { color: red } .x { --a: crimson; color: green; color: var(--a,;) }",
            "x");

        Assert.That(ink, Is.EqualTo("ff008000"));
    }

    [Test]
    public async Task APriorityWrittenTwiceThrowsTheDeclarationAway()
    {
        var ink = await Inked(
            "p { color: red } .x { --a: crimson; color: green; color: var(--a) !important !important }",
            "x");

        Assert.That(ink, Is.EqualTo("ff008000"));
    }

    [Test]
    public async Task AClosingParenThatOpensNothingThrowsTheDeclarationAway()
    {
        var ink = await Inked("p { color: red } .x { --a: green; --a: red) ; color: var(--a) }", "x");

        Assert.That(ink, Is.EqualTo("ff008000"));
    }

    [Test]
    public async Task ACommentCloseIsPartOfTheValueItIsWrittenIn()
    {
        // A comment-close stands in a value as itself, and the value it leads a reference with is
        // therefore not a colour - which leaves the property with what its parent had.
        var ink = await Inked(
            "body { color: green } .x { color: red; --b: crimson; --a: --> var(--b); color: var(--a) }",
            "x");

        Assert.That(ink, Is.EqualTo("ff008000"));
    }

    [Test]
    public async Task TwoReferencesWrittenTogetherDoNotWeldIntoOneWord()
    {
        // Substitution puts tokens back, and a token boundary survives it: two idents written with
        // nothing between them are still two idents, which no colour grammar reads.
        var ink = await Inked(
            "body { color: green } .x { color: red; --a: var(--b)var(--c); --b: orange; --c: red;" +
            " color: var(--a) }",
            "x");

        Assert.That(ink, Is.EqualTo("ff008000"));
    }

    [Test]
    public async Task TheFirstWordOfTheWideOnesNamesNoValueAtAll()
    {
        var ink = await Inked("p { color: red } .x { --a: initial; color: var(--a,green) }", "x");

        Assert.That(ink, Is.EqualTo("ff008000"));
    }

    [Test]
    public async Task TheFirstWordOfTheWideOnesOutranksWhatWasInherited()
    {
        var ink = await Inked(
            "p { color: red } .x { --a: crimson; color: orange } .y { --a: initial;" +
            " color: var(--a,green) }",
            "x",
            "<p class=x><span class=y>x</span></p>");

        Assert.That(ink, Is.EqualTo("ff008000"));
    }

    [Test]
    public async Task TheSecondWordOfTheWideOnesTakesWhatTheParentHad()
    {
        var ink = await Inked(
            "p { color: red } .x { --a: green; color: crimson } .y { --a: inherit; color: red }" +
            " .z { --a: unset; color: var(--a) }",
            "x",
            "<p class=x><span class=y><span class=z>x</span></span></p>");

        Assert.That(ink, Is.EqualTo("ff008000"));
    }

    [Test]
    public async Task AWideWordReachedThroughAFallbackIsTheSameWord()
    {
        var ink = await Inked(
            "p { color: red } .x { --a: green } .y { --a: var(--nope, unset) }" +
            " .z { color: var(--a) }",
            "x",
            "<p class=x><span class=y><span class=z>x</span></span></p>");

        Assert.That(ink, Is.EqualTo("ff008000"));
    }

    [Test]
    public async Task AVariableThatRefersToItselfHasNoValueForSomeoneElseToFallBackOn()
    {
        var ink = await Inked(
            "p { color: red } .x { --a: var(--a, crimson); color: var(--a, green) }",
            "x");

        Assert.That(ink, Is.EqualTo("ff008000"));
    }

    [Test]
    public async Task ARingOfVariablesHasNoValueAnywhereInIt()
    {
        var ink = await Inked(
            "body { color: green } .x { color: crimson; --a: var(--b,red); --b: var(--c);" +
            " --c: var(--d); --d: var(--e); --e: var(--a); --f: var(--e); color: var(--f) }",
            "x");

        Assert.That(ink, Is.EqualTo("ff008000"));
    }

    /// <summary>Gets the colour a page's one run of text is drawn with, as the eight hex digits.</summary>
    private static Task<String> Inked(String css, String text) =>
        Inked(css, text, "<p class='x'>" + text + "</p>");

    /// <summary>Gets the colour a page's one run of text is drawn with, over any body at all.</summary>
    private static async Task<String> Inked(String css, String text, String body)
    {
        using var view = new HutuViewInternal(new Hutu.Tests.FakeTextMeasurer());
        view.SetViewportSize(300, 200);

        await view.SetHtml(
            "<html><head><style>" + css + "</style></head><body style='margin:0'>"
            + body + "</body></html>");

        var command = view.Paint!.Commands.OfType<TextCommand>().Single(c => c.Text == text);

        return $"{command.Color:x8}";
    }
}

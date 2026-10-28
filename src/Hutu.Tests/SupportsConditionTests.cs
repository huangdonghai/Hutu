using Hutu.Rendering;
using NUnit.Framework;
using System;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers what a <c>@supports</c> condition claims about a declaration, which is the one part of the
/// cascade a page can ask about before it knows what the value will be.
/// </summary>
/// <remarks>
/// Conditional 3 §3 reads a feature test as a declaration, and the declaration is only answered for a
/// property the engine knows: a name nobody recognizes is not a feature, whatever is written after its
/// colon. That is the whole of what these two tests hold, and it is the pair's own shape - the corpus page
/// writes <c>(not (yadayada: x, calc( 2/3 )))</c> and expects the rule to apply, which needs both halves to
/// answer the same way.
/// </remarks>
[TestFixture]
public sealed class SupportsConditionTests
{
    [Test]
    public async Task APropertyNobodyRecognizesIsNotAFeatureTheConditionCanClaim()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<style>body{color:red}" +
            "@supports (yadayada: x, calc( 2/3 )) { p { color: lime; } }</style>" +
            "<p id='t'>x</p>"));

        Assert.That(Colour(view), Is.EqualTo("rgba(255, 0, 0, 1)"), "an unknown name answers false");
    }

    [Test]
    public async Task ANegatedUnknownPropertyIsTheTrueAnswer()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<style>body{color:red}" +
            "@supports (not (yadayada: x, calc( 2/3 ))) { p { color: lime; } }</style>" +
            "<p id='t'>x</p>"));

        Assert.That(Colour(view), Is.EqualTo("rgba(0, 255, 0, 1)"), "and its negation is true");

        // A property that is known keeps its own grammar as the answer: the same shape with a real name and
        // a value that grammar refuses is still false.
        await view.SetHtml(Page(
            "<style>body{color:red}" +
            "@supports (color: 12px) { p { color: lime; } }</style>" +
            "<p id='t'>x</p>"));

        Assert.That(Colour(view), Is.EqualTo("rgba(255, 0, 0, 1)"), "a known property still checks its grammar");
    }

    private static String Colour(HutuViewInternal view) =>
        view.GetComputedStyle(view.Document!.GetElementById("t")!).GetPropertyValue("color");

    [Test]
    public async Task AFunctionTheConditionGrammarDoesNotKnowReadsFalseRatherThanUnreadable()
    {
        using var view = CreateView();

        // A `<general-enclosed>` form is a condition with an answer - false - and not a hole in the
        // tree, which is what makes the negation of an unknown function a true condition the page may
        // write its rules on (CSS Conditional 4 §3.1).
        await view.SetHtml(Page(
            "<style>body{color:red}" +
            "@supports unknown(nonsense) { p { color: lime; } }</style>" +
            "<p id='t'>x</p>"));

        Assert.That(Colour(view), Is.EqualTo("rgba(255, 0, 0, 1)"), "the unknown function itself answers false");

        await view.SetHtml(Page(
            "<style>body{color:red}" +
            "@supports not unknown(nonsense) { p { color: lime; } }</style>" +
            "<p id='t'>x</p>"));

        Assert.That(Colour(view), Is.EqualTo("rgba(0, 255, 0, 1)"), "and turning it around answers true");
    }

    [Test]
    public async Task TheFontFeatureConditionsAnswerForTheNamesTheEngineReads()
    {
        using var view = CreateView();

        // One identifier, asked of the formats and technologies the engine can use; a quoted string or
        // a list is not the question, so it is not supported either (CSS Conditional 4 §3.1).
        await view.SetHtml(Page(
            "<style>body{color:red}" +
            "@supports font-format(woff) { p { color: lime; } }</style>" +
            "<p id='t'>x</p>"));

        Assert.That(Colour(view), Is.EqualTo("rgba(0, 255, 0, 1)"), "a format the engine reads");

        await view.SetHtml(Page(
            "<style>body{color:red}" +
            "@supports font-format(\"woff\") { p { color: lime; } }</style>" +
            "<p id='t'>x</p>"));

        Assert.That(Colour(view), Is.EqualTo("rgba(255, 0, 0, 1)"), "a quoted name is not one name");

        await view.SetHtml(Page(
            "<style>body{color:red}" +
            "@supports font-tech(color-COLRv0) { p { color: lime; } }</style>" +
            "<p id='t'>x</p>"));

        Assert.That(Colour(view), Is.EqualTo("rgba(0, 255, 0, 1)"), "and a technology it knows");
    }

    [Test]
    public async Task ASelectorNamingABoxTheEngineKnowsIsReadable()
    {
        using var view = CreateView();

        // The pseudo-elements the parser knows the name of are readable even where nothing is drawn
        // for them, which is the question `selector()` asks (CSS Conditional 4 §3.1).
        await view.SetHtml(Page(
            "<style>body{color:red}" +
            "@supports selector(input::placeholder) { p { color: lime; } }</style>" +
            "<p id='t'>x</p>"));

        Assert.That(Colour(view), Is.EqualTo("rgba(0, 255, 0, 1)"), "the name is known");

        await view.SetHtml(Page(
            "<style>body{color:red}" +
            "@supports selector(::no-such-pseudo-thing) { p { color: lime; } }</style>" +
            "<p id='t'>x</p>"));

        Assert.That(Colour(view), Is.EqualTo("rgba(255, 0, 0, 1)"), "and one that is not cannot be read");
    }

    private static String Page(String content) => "<html><body style='margin:0'>" + content + "</body></html>";

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());

        view.SetViewportSize(400, 300);
        return view;
    }
}

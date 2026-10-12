using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using Hutu.Rendering;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// What a custom property is worth where it is finally used: the size a box comes to when its width
/// was written as <c>var()</c>, the branch that redeclares the name, the fallback that stands when
/// nothing declared it, and the one substitution - an environment variable - that still hands the
/// document to the other path.
/// </summary>
/// <remarks>
/// Asserted on laid-out geometry rather than on styles, because the geometry is what a reader sees and
/// because <see cref="FusedStyleTests"/> already holds the two paths' styles against each other, variable
/// included.
/// </remarks>
[TestFixture]
public sealed class VariableStyleTests
{
    [Test]
    public async Task AWidthWrittenAsAVariableSizesTheBox()
    {
        using var view = await Laid(":root { --w: 123px } .x { width: var(--w); height: 10px }", "<div id='x' class='x'></div>");

        AssertBounds(Bounds(view, "#x"), 0, 0, 123, 10);
    }

    [Test]
    public async Task ABranchThatRedeclaresTheNameSizesItsOwnBoxes()
    {
        // The two boxes matched exactly alike - same rules, no inline style - so every cached area they
        // share is keyed on their cascade alone, and only the variable neighbourhood in the key keeps
        // the inner one from taking the outer's width.
        using var view = await Laid(
            ":root { --w: 100px } .narrow { --w: 50px } .x { width: var(--w); height: 10px }",
            "<div id='a' class='x'></div><div class='narrow'><div id='b' class='x'></div></div>");

        AssertBounds(Bounds(view, "#a"), 0, 0, 100, 10);

        // The two are block boxes in one container, so the second stands below the first - which is
        // what makes it a test of the widths rather than of the flow.
        AssertBounds(Bounds(view, "#b"), 0, 10, 50, 10);
    }

    [Test]
    public async Task AFallbackStandsWhenNobodyDeclaredTheName()
    {
        using var view = await Laid(".x { width: var(--nope, 77px); height: 10px }", "<div id='x' class='x'></div>");

        Assert.That(Bounds(view, "#x").Width, Is.EqualTo(77));
    }

    [Test]
    public async Task AnUnresolvableVariableLeavesThePropertyUndeclared()
    {
        // With no value to take, an auto width fills what the container left - which is what the
        // specification's "invalid at computed-value time" comes to here, and what the other path does.
        using var view = await Laid(".x { width: var(--nope); height: 10px }", "<div id='x' class='x'></div>");

        Assert.That(Bounds(view, "#x").Width, Is.EqualTo(600));
    }

    [Test]
    public async Task AVariableCanSayAnotherVariable()
    {
        using var view = await Laid(
            ":root { --a: var(--b) } :root { --b: 40px } .x { width: var(--a); height: 10px }",
            "<div id='x' class='x'></div>");

        Assert.That(Bounds(view, "#x").Width, Is.EqualTo(40));
    }

    [Test]
    public async Task TheNameWinsOverTheInheritedOneEvenWhenItCannotResolve()
    {
        // A branch that redeclares a name and then points it at nothing does not fall through to what
        // the root said: its own declaration shadows the inherited one, and the property is undeclared.
        using var view = await Laid(
            ":root { --w: 100px } .narrow { --w: var(--nope) } .x { width: var(--w); height: 10px }",
            "<div class='narrow'><div id='b' class='x'></div></div>");

        Assert.That(Bounds(view, "#b").Width, Is.EqualTo(600));
    }

    [Test]
    public async Task AShorthandWrittenAsAVariableReachesItsLonghands()
    {
        // The declaration list holds no <c>padding</c> to read through, only four longhands left empty
        // by an expansion that could not be made before the variable was; the box still has to know
        // them.
        using var view = await Laid(
            ":root { --pad: 6px } .p { padding: var(--pad); width: 100px; height: 10px }",
            "<div id='p' class='p'></div>");

        Assert.That(Bounds(view, "#p").Width, Is.EqualTo(112), "100 of content and 6 of padding either side");
    }

    /// <summary>
    /// An environment variable is a value only the host has, so the fused resolvers give the document up
    /// when one reaches the cascade. What this pins is that it does not: the parser drops the
    /// declaration before it gets there, so no page is handed over for it today. Should the upstream
    /// parser start keeping <c>env()</c>, the assertion fails and the guard in the walk is what catches
    /// the document - which is the behaviour worth having, and worth re-testing then.
    /// </summary>
    [Test]
    public async Task AnEnvironmentVariableNeverReachesTheCascade()
    {
        using var view = CreateView();

        var document = await view.SetHtml(Page(
            ":root { --w: 100px } .x { width: var(--w); padding-left: env(safe-area-inset-top, 4px) }",
            "<div id='x' class='x'></div>"));

        var fused = BoxBuilder.TryBuildFused(document, new HutuRenderDevice(600, 600), (IImageProvider?)null, null);

        Assert.That(fused, Is.Not.Null, "env() is dropped upstream rather than abstained over here");
        Assert.That(fused!.Children, Is.Not.Empty, "and the box under it is still built by the fused path");
    }

    /// <summary>
    /// The form a portal's stylesheet writes its sizes in: a <c>calc()</c> multiplying a length by a
    /// variable that only the desktop client defines, with the web's answer left as the fallback.
    /// </summary>
    [Test]
    public async Task AFallbackInsideACalcSizesTheFont()
    {
        using var view = await Laid(
            ".t { font-size: calc(18px * var(--clientfont-scale, 1)); line-height: 1 }",
            "<p id='t' class='t'>word</p>");

        Assert.That(Bounds(view, "#t").Height, Is.EqualTo(18).Within(0.5), "the fallback answers for the missing name");
    }

    [Test]
    public async Task AVariableInsideACalcMultipliesTheLength()
    {
        using var view = await Laid(
            ":root { --scale: 2 } .t { font-size: calc(18px * var(--scale)); line-height: 1 }",
            "<p id='t' class='t'>word</p>");

        Assert.That(Bounds(view, "#t").Height, Is.EqualTo(36).Within(0.5));
    }

    [Test]
    public async Task AFallbackInsideACalcSizesAWidth()
    {
        using var view = await Laid(
            ".x { width: calc(100px * var(--nope, 2)); height: 10px }",
            "<div id='x' class='x'></div>");

        Assert.That(Bounds(view, "#x").Width, Is.EqualTo(200).Within(0.5));
    }

    private static async Task<HutuViewInternal> Laid(String css, String body)
    {
        var view = CreateView();

        await view.SetHtml(Page(css, body));

        Assert.That(view.Layout, Is.Not.Null, "the document has to be laid out");
        return view;
    }

    private static Rect Bounds(HutuViewInternal view, String selector)
    {
        var element = view.Document!.QuerySelector(selector);

        Assert.That(element, Is.Not.Null, $"no element for {selector}");

        var box = view.Layout!.FindBox(element!);

        Assert.That(box, Is.Not.Null, $"{selector} generated no box");
        return box!.BorderBox;
    }

    /// <summary>
    /// Compares a rectangle by writing it the way a stylesheet would, because a <see cref="Rect"/> that
    /// differs reports itself as two objects of the same class and leaves the reading of the difference
    /// to whoever has to guess which of the four numbers moved.
    /// </summary>
    private static void AssertBounds(Rect actual, Double x, Double y, Double width, Double height) =>
        Assert.That(
            $"{actual.X:F0},{actual.Y:F0} {actual.Width:F0}x{actual.Height:F0}",
            Is.EqualTo($"{x:F0},{y:F0} {width:F0}x{height:F0}"));

    private static String Page(String css, String body) =>
        "<html><head><style>" + css + "</style></head><body style='margin:0'>" + body + "</body></html>";

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(600, 600);
        return view;
    }
}

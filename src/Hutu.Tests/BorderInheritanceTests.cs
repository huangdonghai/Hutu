using Hutu.Rendering;
using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// <c>border-width: inherit</c> on a box whose width the parent set in a font-relative unit takes the
/// length the parent came to - not that unit re-read at the child's own, larger font size.
/// </summary>
/// <remarks>
/// Border is not an inherited property, so the parent's width travels to the child for this keyword
/// alone. Two things guard it: a child set to a bigger font must still land on the parent's pixels, and
/// the fused style cache - which keys a border on the cascade and the font size - must not hand one
/// child another child's inherited width when the two share a cascade and a size but sit under parents
/// of different borders. Both fall out of the parent's border riding in the cache key.
/// </remarks>
[TestFixture]
public sealed class BorderInheritanceTests
{
    [Test]
    public async Task InheritedBorderWidthTakesThePixelsTheParentCameToNotItsOwnReReadEm()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='border:1em solid'>" +
            "<p style='font-size:2em;border-style:solid;border-width:inherit'>x</p>" +
            "</div></body></html>");

        var parent = BoxOf(view, "div");
        var child = BoxesNamed(view, "p").Single();

        // 1em of the parent at its own 16px, not the 32px the child's doubled font would make of the
        // same unit: the child inherits the length, not the word.
        Assert.That(parent.Border.Top, Is.EqualTo(16).Within(0.01));
        Assert.That(child.Border.Top, Is.EqualTo(16).Within(0.01), "the child takes the parent's computed width");
        Assert.That(child.Border.Top, Is.Not.EqualTo(32).Within(0.01), "and does not re-read the parent's em at its own size");
    }

    [Test]
    public async Task TwoChildrenSharingACascadeKeepTheirOwnParentsWidths()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><head><style>.cell { border-style: solid; border-width: inherit }</style></head>" +
            "<body style='margin:0'>" +
            "<div style='border:10px solid'><p class='cell'>a</p></div>" +
            "<div style='border:20px solid'><p class='cell'>b</p></div>" +
            "</body></html>");

        var first = BoxesNamed(view, "p").ElementAt(0);
        var second = BoxesNamed(view, "p").ElementAt(1);

        // Same cascade, same font size, so only the parent tells the two apart - and the cache has to
        // tell them apart too, rather than hand the second the first's inherited ten pixels.
        Assert.That(first.Border.Top, Is.EqualTo(10).Within(0.01));
        Assert.That(second.Border.Top, Is.EqualTo(20).Within(0.01), "the shared cascade must not share the wrong parent");
    }

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(200, 200);
        return view;
    }

    /// <summary>Finds the single block an element of the given name was laid out as.</summary>
    private static LayoutBox BoxOf(HutuViewInternal view, String name) =>
        BoxesNamed(view, name).Single();

    private static IEnumerable<LayoutBox> BoxesNamed(HutuViewInternal view, String name) =>
        Walk(view.Layout!.Root).Where(box => box.Kind == LayoutKind.Block && box.Source.Element?.LocalName == name);

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

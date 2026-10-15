using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// CSS Transforms (2D) and Motion Path: a transform/offset emits a paint transform that moves where a
/// box is drawn (not where it is laid out); a transformed box becomes the containing block for a fixed
/// descendant; and a pointer is inverse-mapped so a moved box is hit where it appears.
/// </summary>
[TestFixture]
public sealed class TransformTests
{
    [Test]
    public async Task ATranslateEmitsATransformThatMovesTheBox()
    {
        using var view = View(100, 100);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='box' style='position:absolute;left:0;top:0;width:20px;height:20px;background:#ff0000;transform:translateX(30px)'></div>" +
            "</body></html>");

        var transform = FirstTransform(view);

        Assert.That(transform.Matrix.E, Is.EqualTo(30).Within(0.5), "the box is drawn 30 pixels right");
        Assert.That(Find(view, "box").BorderBox.X, Is.EqualTo(0).Within(0.5), "but its layout place is unmoved");
    }

    [Test]
    public async Task AScaleAboutTheOriginHalvesTheMatrix()
    {
        using var view = View(100, 100);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='box' style='position:absolute;left:0;top:0;width:40px;height:40px;background:#00ff00;transform-origin:0 0;transform:scale(0.5)'></div>" +
            "</body></html>");

        var transform = FirstTransform(view);

        Assert.That(transform.Matrix.A, Is.EqualTo(0.5).Within(0.01));
        Assert.That(transform.Matrix.D, Is.EqualTo(0.5).Within(0.01));
    }

    [Test]
    public async Task OffsetPathEmitsATransformAlongThePath()
    {
        using var view = View(200, 100);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='box' style=\"position:absolute;left:0;top:0;width:20px;height:20px;background:#0000ff;transform-origin:0 0;offset-path:path('m 0 0 h 100');offset-distance:50%\"></div>" +
            "</body></html>");

        var transform = FirstTransform(view);

        Assert.That(transform.Matrix.E, Is.EqualTo(50).Within(1), "half-way along a 100-pixel path");
    }

    [Test]
    public async Task ATransformWrittenWithVarResolvesTheCustomProperty()
    {
        // Tailwind writes `transform: translateX(var(--tw-translate-x))`. AngleSharp's own transform
        // grammar rejects an unresolved var() and discards the declaration to `none`; keeping transform
        // as raw text lets the VariableScope substitute the custom property, so the shift lands.
        using var view = View(100, 100);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='box' style='--x:30px;position:absolute;left:0;top:0;width:20px;height:20px;background:#ff0000;transform:translateX(var(--x))'></div>" +
            "</body></html>");

        var transform = FirstTransform(view);

        Assert.That(transform.Matrix.E, Is.EqualTo(30).Within(0.5), "the var() resolved to the declared 30px");
    }

    [Test]
    public async Task ATransformedAncestorIsTheContainingBlockOfAFixedDescendant()
    {
        using var view = View(300, 300);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='position:absolute;left:100px;top:100px;width:50px;height:50px;transform:rotate(0deg)'>" +
            "<div id='f' style='position:fixed;left:0;top:0;width:10px;height:10px'></div>" +
            "</div></body></html>");

        var f = Find(view, "f");

        Assert.That(f.BorderBox.X, Is.EqualTo(100).Within(0.5), "fixed anchors to the transformed ancestor");
        Assert.That(f.BorderBox.Y, Is.EqualTo(100).Within(0.5));
    }

    [Test]
    public async Task ATranslatedLinkIsHitWhereItIsDrawn()
    {
        using var view = View(200, 200);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<a href='#x' style='position:absolute;left:0;top:0;width:20px;height:20px;display:block;transform:translateX(50px)'>L</a>" +
            "</body></html>");

        Assert.That(view.LinkAt(55, 5), Is.Not.Null, "the link is drawn 50 pixels right, so that is where it is clicked");
        Assert.That(view.LinkAt(5, 5), Is.Null, "its layout place is empty once moved");
    }

    // --- helpers --------------------------------------------------------

    private static HutuViewInternal View(Int32 width, Int32 height)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(width, height);
        return view;
    }

    private static PushTransformCommand FirstTransform(HutuViewInternal view)
    {
        foreach (var command in view.Paint!.Commands)
        {
            if (command is PushTransformCommand transform)
            {
                return transform;
            }
        }

        throw new AssertionException("no transform was emitted");
    }

    private static LayoutBox Find(HutuViewInternal view, String id)
    {
        foreach (var node in Walk(view.Layout!.Root))
        {
            if (node.Source.Element?.GetAttribute("id") == id)
            {
                return node;
            }
        }

        throw new AssertionException($"no box for #{id}");
    }

    private static IEnumerable<LayoutBox> Walk(LayoutBox box)
    {
        yield return box;

        foreach (var child in box.Children)
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }
}

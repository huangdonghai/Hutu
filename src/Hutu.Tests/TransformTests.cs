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

    /// <summary>
    /// A transform-origin is a point inside the box, so a box a relative ancestor moves keeps its
    /// origin with it. Reading it in document coordinates when the box is laid out would leave the point
    /// behind and turn the box about the place it used to stand.
    /// </summary>
    [Test]
    public async Task ATransformOriginTravelsWithTheBoxItBelongsTo()
    {
        using var view = View(400, 400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='position:relative;left:100px;top:100px;width:200px;height:200px'>" +
            "<div id='box' style='position:relative;width:40px;height:40px;transform:rotate(45deg);transform-origin:10px 10px'></div>" +
            "</div></body></html>");

        var box = Find(view, "box");
        var transform = FirstTransform(view);

        Assert.That(box.TransformOrigin.X, Is.EqualTo(10).Within(0.5), "the origin is read within the box");
        Assert.That(box.TransformOrigin.Y, Is.EqualTo(10).Within(0.5));
        Assert.That(transform.Origin.X, Is.EqualTo(110).Within(0.5), "and the painter resolves it against the box's own corner");
        Assert.That(transform.Origin.Y, Is.EqualTo(110).Within(0.5));
    }

    /// <summary>
    /// With no <c>at</c> in the shape, a declared <c>offset-position</c> is the circle's centre: `auto`
    /// is the element's own corner, which leaves a zero-radius circle to stand on.
    /// </summary>
    [Test]
    public async Task AnOffsetPositionIsTheCentreOfAShapeThatNamesNone()
    {
        using var view = View(300, 300);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='position:relative;width:200px;height:100px'>" +
            "<div id='box' style='position:relative;width:40px;height:40px;offset-path:circle();offset-position:auto'></div>" +
            "</div></body></html>");

        var transform = FirstTransform(view);

        Assert.That(transform.Matrix.E, Is.EqualTo(-20).Within(0.5), "the middle of the box lands on its own top-left corner");
        Assert.That(transform.Matrix.F, Is.EqualTo(-20).Within(0.5));
    }

    /// <summary>
    /// An explicit <c>offset-anchor</c> names the point that rides the path, so the matrix has to take the
    /// anchor out of the transform-origin after turning: the corner lands on the path, not the middle.
    /// </summary>
    [Test]
    public async Task AnOffsetAnchorLandsTheCornerItNamesOnThePath()
    {
        using var view = View(800, 500);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='position:relative;width:600px;height:400px'>" +
            "<div id='box' style='position:relative;width:100px;height:100px;offset-path:rect(5px 95% 95% 5px);offset-distance:40%;offset-anchor:10% 10%'></div>" +
            "</div></body></html>");

        var transform = FirstTransform(view);
        var matrix = transform.Matrix;

        // 40% of the rectangle's outline is the middle of its right edge, at (570,192); the box turns a
        // quarter turn there, and its 10%/10% anchor is what stands on the point.
        Assert.That(matrix.A, Is.EqualTo(0).Within(0.001));
        Assert.That(matrix.B, Is.EqualTo(1).Within(0.001));
        Assert.That(matrix.C, Is.EqualTo(-1).Within(0.001));
        Assert.That(matrix.D, Is.EqualTo(0).Within(0.001));

        var origin = transform.Origin;
        var anchorX = 10.0;
        var anchorY = 10.0;
        var imageX = origin.X + matrix.A * (anchorX - origin.X) + matrix.C * (anchorY - origin.Y) + matrix.E;
        var imageY = origin.Y + matrix.B * (anchorX - origin.X) + matrix.D * (anchorY - origin.Y) + matrix.F;

        Assert.That(imageX, Is.EqualTo(570).Within(0.5), "the anchor's image is the path point");
        Assert.That(imageY, Is.EqualTo(192).Within(0.5));
    }

    /// <summary>
    /// A basic shape sizes into the box its <c>&lt;coord-box&gt;</c> names on the element that establishes
    /// the containing block - here the padding box of the parent, not the parent's content box.
    /// </summary>
    [Test]
    public async Task AShapeSizesIntoTheCoordBoxTheValueNames()
    {
        using var view = View(400, 400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='position:relative;width:200px;height:100px;padding:10px;border:10px solid #000'>" +
            "<div id='box' style='position:relative;width:40px;height:40px;offset-path:padding-box'></div>" +
            "</div></body></html>");

        var transform = FirstTransform(view);

        // The padding box starts at (10,10); the box stands at (20,20) with its middle at (40,40).
        Assert.That(transform.Matrix.E, Is.EqualTo(-30).Within(0.5));
        Assert.That(transform.Matrix.F, Is.EqualTo(-30).Within(0.5));
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

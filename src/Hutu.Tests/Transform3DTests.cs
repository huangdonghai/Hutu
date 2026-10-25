using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// CSS Transforms 2's third dimension: a box turned about x or y is drawn as the quadrilateral its
/// plane projects to, a parent's <c>perspective</c> sizes what stands toward the viewer, a plane seen
/// from behind can be hidden, and the individual <c>translate</c>/<c>rotate</c>/<c>scale</c>
/// properties compose before the <c>transform</c> list.
/// </summary>
[TestFixture]
public sealed class Transform3DTests
{
    [Test]
    public async Task ATurnAboutXWithoutPerspectiveSquashesTheBox()
    {
        using var view = View(200, 200);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='box' style='position:absolute;left:0;top:0;width:100px;height:100px;transform-origin:0 0;transform:rotateX(60deg)'></div>" +
            "</body></html>");

        var transform = FirstTransform(view);

        Assert.That(transform.Matrix.A, Is.EqualTo(1).Within(0.001), "x is untouched by a turn about x");
        Assert.That(transform.Matrix.D, Is.EqualTo(0.5).Within(0.001), "y is foreshortened by cos 60");
        Assert.That(transform.Matrix.E, Is.EqualTo(0).Within(0.001));
        Assert.That(transform.Matrix.F, Is.EqualTo(0).Within(0.001));
        Assert.That(Find(view, "box").FacesAway, Is.False, "the plane still faces the reader");
    }

    [Test]
    public async Task AParentsPerspectiveSizesWhatStandsTowardTheViewer()
    {
        using var view = View(200, 200);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='position:absolute;left:0;top:0;width:200px;height:200px;perspective:100px'>" +
            "<div id='box' style='width:100px;height:100px;transform-origin:0 0;transform:translateZ(50px)'></div>" +
            "</div></body></html>");

        var transform = FirstTransform(view);

        // A point 50 pixels toward the eye in the 100-pixel view is twice as large: w = 1 - 50/100,
        // which is where the perspective row of the matrix puts the divide.
        Assert.That(transform.Matrix.A / transform.Matrix.P2, Is.EqualTo(2).Within(0.001));
        Assert.That(transform.Matrix.D / transform.Matrix.P2, Is.EqualTo(2).Within(0.001));
        Assert.That(transform.Matrix.P2, Is.EqualTo(0.5).Within(0.001), "the divide is carried, not folded in");
    }

    [Test]
    public async Task APlaneTurnedAwayIsNotDrawnWhenItsBackfaceIsHidden()
    {
        using var view = View(200, 200);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='box' style='position:absolute;left:0;top:0;width:100px;height:100px;background:#ff0000;" +
            "backface-visibility:hidden;transform:rotateY(180deg)'></div>" +
            "</body></html>");

        Assert.That(Find(view, "box").FacesAway, Is.True, "a half turn about y shows the plane's back");
        Assert.That(Fills(view), Is.Empty, "and the box asks not to be drawn from behind");
    }

    [Test]
    public async Task APlaneSeenFromBehindIsDrawnWhenItsBackfaceIsVisible()
    {
        using var view = View(200, 200);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='box' style='position:absolute;left:0;top:0;width:100px;height:100px;background:#ff0000;" +
            "backface-visibility:visible;transform:rotateY(180deg)'></div>" +
            "</body></html>");

        Assert.That(Find(view, "box").FacesAway, Is.True);
        Assert.That(Fills(view), Is.Not.Empty, "the default is to show the back");
    }

    [Test]
    public async Task TheIndividualPropertiesMoveThenTurnThenResize()
    {
        using var view = View(200, 200);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='box' style='position:absolute;left:0;top:0;width:20px;height:20px;transform-origin:0 0;" +
            "translate:10px;rotate:90deg;scale:2'></div>" +
            "</body></html>");

        var transform = FirstTransform(view);

        // translate(10, 0) · rotate(90°) · scale(2): the x axis lands on y, at twice its length.
        Assert.That(transform.Matrix.A, Is.EqualTo(0).Within(0.001));
        Assert.That(transform.Matrix.B, Is.EqualTo(2).Within(0.001));
        Assert.That(transform.Matrix.C, Is.EqualTo(-2).Within(0.001));
        Assert.That(transform.Matrix.D, Is.EqualTo(0).Within(0.001));
        Assert.That(transform.Matrix.E, Is.EqualTo(10).Within(0.001), "the move is the outer factor");
    }

    [Test]
    public async Task ATranslatedShareOfTheBoxTakesItsWidth()
    {
        using var view = View(400, 200);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='box' style='position:absolute;left:0;top:0;width:200px;height:50px;transform-origin:0 0;translate:50%'></div>" +
            "</body></html>");

        Assert.That(FirstTransform(view).Matrix.E, Is.EqualTo(100).Within(0.5), "half of a 200-pixel box");
    }

    [Test]
    public async Task Matrix3dReadsItsSixteenValuesByColumn()
    {
        using var view = View(200, 200);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='box' style='position:absolute;left:0;top:0;width:20px;height:20px;transform-origin:0 0;" +
            "transform:matrix3d(2,0,0,0, 0,3,0,0, 0,0,1,0, 10,20,0,1)'></div>" +
            "</body></html>");

        var transform = FirstTransform(view);

        Assert.That(transform.Matrix.A, Is.EqualTo(2).Within(0.001));
        Assert.That(transform.Matrix.D, Is.EqualTo(3).Within(0.001));
        Assert.That(transform.Matrix.E, Is.EqualTo(10).Within(0.001), "the last four values are the move");
        Assert.That(transform.Matrix.F, Is.EqualTo(20).Within(0.001));
    }

    [Test]
    public async Task AnInheritedTranslateIsTheParentsMove()
    {
        using var view = View(200, 200);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='position:absolute;left:0;top:0;width:100px;height:100px;translate:10px 20px'>" +
            "<div id='box' style='position:absolute;left:0;top:0;width:20px;height:20px;translate:inherit'></div>" +
            "</div></body></html>");

        var matrices = Transforms(view);

        Assert.That(matrices.Count, Is.EqualTo(2), "the parent and the child each carry a move");
        Assert.That(matrices[1].Matrix.E, Is.EqualTo(10).Within(0.001), "the child takes the parent's move");
        Assert.That(matrices[1].Matrix.F, Is.EqualTo(20).Within(0.001));
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
        var all = Transforms(view);

        return all.Count > 0 ? all[0] : throw new AssertionException("no transform was emitted");
    }

    private static List<PushTransformCommand> Transforms(HutuViewInternal view)
    {
        var found = new List<PushTransformCommand>();

        foreach (var command in view.Paint!.Commands)
        {
            if (command is PushTransformCommand transform)
            {
                found.Add(transform);
            }
        }

        return found;
    }

    private static List<FillCommand> Fills(HutuViewInternal view)
    {
        var found = new List<FillCommand>();

        foreach (var command in view.Paint!.Commands)
        {
            if (command is FillCommand fill)
            {
                found.Add(fill);
            }
        }

        return found;
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

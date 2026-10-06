using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers how <c>border-radius</c> is read, resolved against the box it belongs to and lowered into
/// the drawing instructions: per-corner, elliptical, percentage and overlapping radii, the inner
/// (padding edge) curve, and the clips that <c>background-clip</c> and <c>overflow</c> ask for.
/// </summary>
/// <remarks>
/// The documents are the input rather than a hand-built style because the radii come from the cascade,
/// and how AngleSharp expands the <c>border-radius</c> shorthand - and whether it keeps a percentage
/// un-resolved - is part of what is being asserted. Reading the resolved <see cref="LayoutBox.Radii"/>
/// rather than the style value is deliberate: it is the single result of reading, percentage
/// resolution and the overlap scale, all of which have to agree before a corner is painted.
/// </remarks>
[TestFixture]
public sealed class BorderRadiusTests
{
    [Test]
    public async Task OneValueRoundsEveryCornerEqually()
    {
        var box = await Box(
            "<div id='b' style='width:100px;height:100px;border-radius:20px'></div>");

        Assert.That(box.Radii.TopLeft.Horizontal, Is.EqualTo(20).Within(0.01));
        Assert.That(box.Radii.TopLeft.Vertical, Is.EqualTo(20).Within(0.01));
        Assert.That(box.Radii.TopRight.Horizontal, Is.EqualTo(20).Within(0.01));
        Assert.That(box.Radii.BottomRight.Horizontal, Is.EqualTo(20).Within(0.01));
        Assert.That(box.Radii.BottomLeft.Horizontal, Is.EqualTo(20).Within(0.01));
        Assert.That(box.Radii.IsUniform, Is.True);
    }

    [Test]
    public async Task TwoValuesSetOppositePairs()
    {
        var box = await Box(
            "<div id='b' style='width:100px;height:100px;border-radius:10px 30px'></div>");

        // The four values go top-left, top-right, bottom-right, bottom-left: opposite corners share an
        // entry, so two values give a diagonal pattern.
        Assert.That(box.Radii.TopLeft.Horizontal, Is.EqualTo(10).Within(0.01));
        Assert.That(box.Radii.BottomRight.Horizontal, Is.EqualTo(10).Within(0.01));
        Assert.That(box.Radii.TopRight.Horizontal, Is.EqualTo(30).Within(0.01));
        Assert.That(box.Radii.BottomLeft.Horizontal, Is.EqualTo(30).Within(0.01));
    }

    [Test]
    public async Task ThreeValuesSplitTheMiddlePairAcrossTheSecondAndFourthCorner()
    {
        var box = await Box(
            "<div id='b' style='width:100px;height:100px;border-radius:8px 20px 35px'></div>");

        Assert.That(box.Radii.TopLeft.Horizontal, Is.EqualTo(8).Within(0.01));
        Assert.That(box.Radii.TopRight.Horizontal, Is.EqualTo(20).Within(0.01));
        Assert.That(box.Radii.BottomRight.Horizontal, Is.EqualTo(35).Within(0.01));
        Assert.That(box.Radii.BottomLeft.Horizontal, Is.EqualTo(20).Within(0.01));
    }

    [Test]
    public async Task FourValuesSetEachCornerOnItsOwn()
    {
        var box = await Box(
            "<div id='b' style='width:100px;height:100px;border-radius:5px 15px 30px 45px'></div>");

        Assert.That(box.Radii.TopLeft.Horizontal, Is.EqualTo(5).Within(0.01));
        Assert.That(box.Radii.TopRight.Horizontal, Is.EqualTo(15).Within(0.01));
        Assert.That(box.Radii.BottomRight.Horizontal, Is.EqualTo(30).Within(0.01));
        Assert.That(box.Radii.BottomLeft.Horizontal, Is.EqualTo(45).Within(0.01));
        Assert.That(box.Radii.IsUniform, Is.False);
    }

    [Test]
    public async Task LonghandsSetIndividualCorners()
    {
        var box = await Box(
            "<div id='b' style='width:100px;height:100px;" +
            "border-top-left-radius:10px;border-top-right-radius:20px;" +
            "border-bottom-right-radius:30px;border-bottom-left-radius:40px'></div>");

        Assert.That(box.Radii.TopLeft.Horizontal, Is.EqualTo(10).Within(0.01));
        Assert.That(box.Radii.TopRight.Horizontal, Is.EqualTo(20).Within(0.01));
        Assert.That(box.Radii.BottomRight.Horizontal, Is.EqualTo(30).Within(0.01));
        Assert.That(box.Radii.BottomLeft.Horizontal, Is.EqualTo(40).Within(0.01));
    }

    [Test]
    public async Task LatterShorthandResetsEarlierLonghands()
    {
        // Section 2 of the showcase page: a longhand is written first and then the shorthand zeroes
        // it. The cascade has to leave no radius behind, so this must paint a square box.
        var box = await Box(
            "<div id='b' style='width:100px;height:100px;" +
            "border-top-left-radius:30px;border-radius:0'></div>");

        Assert.That(box.Radii.IsZero, Is.True);
    }

    [Test]
    public async Task SlashSyntaxGivesEllipticalCorners()
    {
        var box = await Box(
            "<div id='b' style='width:100px;height:100px;border-radius:40px / 10px'></div>");

        // Before the slash is the horizontal axis, after it the vertical one.
        Assert.That(box.Radii.TopLeft.Horizontal, Is.EqualTo(40).Within(0.01));
        Assert.That(box.Radii.TopLeft.Vertical, Is.EqualTo(10).Within(0.01));
        Assert.That(box.Radii.IsUniform, Is.False, "a flattened corner is not a circle");
    }

    [Test]
    public async Task PercentageRadiusResolvesAgainstEachAxisOfTheBox()
    {
        // 50% of the width horizontally and 50% of the height vertically, which is an ellipse rather
        // than a circle whenever the box is not square.
        var box = await Box(
            "<div id='b' style='width:200px;height:100px;border-radius:50%'></div>");

        Assert.That(box.Radii.TopLeft.Horizontal, Is.EqualTo(100).Within(0.5));
        Assert.That(box.Radii.TopLeft.Vertical, Is.EqualTo(50).Within(0.5));
    }

    [Test]
    public async Task PercentageRadiusDoesNotResolveAgainstTheViewport()
    {
        // The containing block is 200px wide inside a 400px viewport: a percentage read off the render
        // device instead of the box would come out at 200, which is what this pins down.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:200px'>" +
            "<div id='b' style='height:80px;border-radius:50%'></div>" +
            "</div>" +
            "</body></html>");

        var box = BoxWithId(view, "b");

        Assert.That(box.Radii.TopLeft.Horizontal, Is.EqualTo(100).Within(0.5), "half of the 200px box");
    }

    [Test]
    public async Task ACornerRadiusLongerThanTheSideIsScaledDown()
    {
        // Section 5 of the showcase page: an 80px box with a 60px radius. Two 60px radii do not fit
        // in 80px, so the whole set shrinks by 80/120 and each corner comes out at 40px.
        var box = await Box(
            "<div id='b' style='width:80px;height:300px;border-radius:60px'></div>");

        Assert.That(box.Radii.TopLeft.Horizontal, Is.EqualTo(40).Within(0.1));
        Assert.That(box.Radii.TopLeft.Vertical, Is.EqualTo(40).Within(0.1));
    }

    [Test]
    public async Task AnOversizedRadiusIsCappedToTheSameResultAsOneThatExactlyFits()
    {
        // 100px on an 80px box is just 60px taken further: both reduce to the 40px the box can hold.
        var box = await Box(
            "<div id='b' style='width:80px;height:300px;border-radius:100px'></div>");

        Assert.That(box.Radii.TopLeft.Horizontal, Is.EqualTo(40).Within(0.1));
    }

    [Test]
    public async Task ARadiusThatFitsIsLeftAlone()
    {
        var box = await Box(
            "<div id='b' style='width:80px;height:300px;border-radius:20px'></div>");

        Assert.That(box.Radii.TopLeft.Horizontal, Is.EqualTo(20).Within(0.1));
    }

    [Test]
    public async Task PaddingRadiiSubtractTheBorderThickness()
    {
        // Section 8: a 6px border around a 14px corner leaves an 8px corner on the inner edge.
        var box = await Box(
            "<div id='b' style='width:160px;height:90px;border:6px solid #1a6b8a;border-radius:14px'></div>");

        Assert.That(box.Radii.TopLeft.Horizontal, Is.EqualTo(14).Within(0.01), "the outer curve is unchanged");
        Assert.That(box.PaddingRadii.TopLeft.Horizontal, Is.EqualTo(8).Within(0.01));
        Assert.That(box.PaddingRadii.TopLeft.Vertical, Is.EqualTo(8).Within(0.01));
    }

    [Test]
    public async Task PaddingRadiiClampAtZeroWhenTheBorderIsThickerThanTheCorner()
    {
        var box = await Box(
            "<div id='b' style='width:100px;height:100px;border:10px solid #000;border-radius:4px'></div>");

        Assert.That(box.PaddingRadii.IsZero, Is.True, "a border wider than the radius squares the inner edge");
    }

    [Test]
    public async Task BackgroundClipPaddingBoxPaintsInsideTheBorder()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='b' style='width:100px;height:100px;border:6px solid #000;" +
            "background:#ff0000;background-clip:padding-box'></div>" +
            "</body></html>");

        var box = BoxWithId(view, "b");
        var fill = view.Paint!.Commands.OfType<FillCommand>().Single(command => command.Color == 0xFFFF0000);

        Assert.That(fill.Bounds.X, Is.EqualTo(box.PaddingBox.X).Within(0.01), "the fill starts at the border's inner edge");
        Assert.That(fill.Bounds.Width, Is.EqualTo(box.PaddingBox.Width).Within(0.01));
    }

    [Test]
    public async Task BackgroundClipDefaultsToTheBorderBox()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='b' style='width:100px;height:100px;border:6px solid #000;background:#ff0000'></div>" +
            "</body></html>");

        var box = BoxWithId(view, "b");
        var fill = view.Paint!.Commands.OfType<FillCommand>().Single(command => command.Color == 0xFFFF0000);

        Assert.That(fill.Bounds.X, Is.EqualTo(box.BorderBox.X).Within(0.01), "the default clip reaches under the border");
        Assert.That(fill.Bounds.Width, Is.EqualTo(box.BorderBox.Width).Within(0.01));
    }

    [Test]
    public async Task OverflowHiddenWrapsTheChildrenInAClipPair()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='b' style='width:100px;height:100px;border-radius:20px;overflow:hidden'>" +
            "<div style='width:200px;height:200px;background:#00ff00'></div>" +
            "</div>" +
            "</body></html>");

        var commands = view.Paint!.Commands;
        var push = commands.OfType<PushClipCommand>().ToList();
        var pop = commands.OfType<PopClipCommand>().ToList();

        Assert.That(push, Has.Count.EqualTo(1), "the clipping box opens one clip");
        Assert.That(pop, Has.Count.EqualTo(1), "and closes it again, so the stack stays balanced");

        // The clip is the padding box with the inner curve, which is what confines a child to the
        // rounded edge rather than letting it paint into the square corners.
        var box = BoxWithId(view, "b");
        Assert.That(push[0].Bounds.X, Is.EqualTo(box.PaddingBox.X).Within(0.01));
        Assert.That(push[0].Radii.TopLeft.Horizontal, Is.EqualTo(box.PaddingRadii.TopLeft.Horizontal).Within(0.01));

        // The pair brackets the child's own fill.
        var childFill = commands.OfType<FillCommand>().Single(command => command.Color == 0xFF00FF00);
        Assert.That(commands.IndexOf(push[0]), Is.LessThan(commands.IndexOf(childFill)));
        Assert.That(commands.IndexOf(pop[0]), Is.GreaterThan(commands.IndexOf(childFill)));
    }

    [Test]
    public async Task OverflowVisibleEmitsNoClip()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:100px;height:100px;border-radius:20px'>" +
            "<div style='width:200px;height:200px;background:#00ff00'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(view.Paint!.Commands.OfType<PushClipCommand>(), Is.Empty);
    }

    [Test]
    public async Task OverflowWithoutChildrenEmitsNoClip()
    {
        // An empty clipping box has nothing to confine, so the clip is skipped rather than left to
        // open and close around no commands.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:100px;height:100px;overflow:hidden'></div>" +
            "</body></html>");

        Assert.That(view.Paint!.Commands.OfType<PushClipCommand>(), Is.Empty);
    }

    private static async Task<LayoutBox> Box(String markup)
    {
        using var view = CreateView(400);
        await view.SetHtml("<html><body style='margin:0'>" + markup + "</body></html>");
        return BoxWithId(view, "b");
    }

    private static LayoutBox BoxWithId(HutuViewInternal view, String id)
    {
        var found = Walk(view.Layout!.Root)
            .Where(box => box.Kind is LayoutKind.Block or LayoutKind.HorizontalRule &&
                box.Source.Element?.GetAttribute("id") == id)
            .ToList();

        Assert.That(found, Has.Count.EqualTo(1), $"expected one box for #{id}");
        return found[0];
    }

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

    private static HutuViewInternal CreateView(int viewportWidth)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }
}

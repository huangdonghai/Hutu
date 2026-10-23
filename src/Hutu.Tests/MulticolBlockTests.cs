using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers a multi-column container whose content is block-level boxes: the children are shared over
/// the columns whole, a forced column break ends a column early, and the box's intrinsic width is the
/// columns' width side by side.
/// </summary>
[TestFixture]
public sealed class MulticolBlockTests
{
    [Test]
    public async Task TheChildrenShareTheColumnsAndTheBoxIsAsTallAsTheyBalanceTo()
    {
        using var view = await Render(
            "<style>#c { width: 300px; columns: 2; column-gap: 20px }" +
            " .b { height: 40px }</style>" +
            "<div id='c'><div class='b'></div><div class='b'></div></div>");

        var children = Blocks(view);

        Assert.That(children.Count, Is.EqualTo(2), "both children are laid out");
        Assert.That(children[0].BorderBox.X, Is.EqualTo(0), "the first stands in the first column");
        Assert.That(children[1].BorderBox.X, Is.EqualTo(160), "and the second in the column after the gap");
        Assert.That(children.Select(child => child.BorderBox.Y), Is.All.EqualTo(0), "each column begins at the top");
        Assert.That(Find(view, "c").BorderBox.Height, Is.EqualTo(40), "the box is as tall as the columns were balanced to");
    }

    [Test]
    public async Task AForcedColumnBreakEndsTheColumnEvenWhereMoreWouldFit()
    {
        using var view = await Render(
            "<style>#c { width: 300px; columns: 2; column-gap: 20px; column-fill: auto; height: 100px }" +
            " .b { height: 20px } .next { break-before: column }</style>" +
            "<div id='c'><div class='b'></div><div class='b next'></div><div class='b'></div></div>");

        var children = Blocks(view);

        Assert.That(children[0].BorderBox.X, Is.EqualTo(0), "the first child stays in the first column");
        Assert.That(children[1].BorderBox.X, Is.EqualTo(160), "the forced break carries the second to the next column");
        Assert.That(children[2].BorderBox.X, Is.EqualTo(160), "and the third follows it there");
        Assert.That(children[1].BorderBox.Y, Is.EqualTo(0), "which begins at its top");
    }

    [Test]
    public async Task TheIntrinsicWidthIsTheColumnsSideBySide()
    {
        using var view = await Render(
            "<style>body { font-size: 10px; line-height: 10px }" +
            " #c { display: inline-block; columns: 2; column-gap: 10px }</style>" +
            "<div id='c'>XXXXXXXX<br>XX</div>");

        Assert.That(
            Find(view, "c").BorderBox.Width,
            Is.EqualTo(90),
            "two columns of the widest line, forty, and the gap between them");
    }

    [Test]
    public async Task ADecoratedFloatParagraphKeepsItsEdgesOnTheFragmentsThatOwnThem()
    {
        var words = String.Join(" ", Enumerable.Repeat("aaaa", 30));

        using var view = await Render(
            "<style>#c { width: 300px; columns: 2; column-gap: 20px }" +
            " #p { margin: 0; border: 2px solid; padding: 4px }" +
            " #f { float: right; width: 60px; height: 30px }</style>" +
            "<div id='c'><p id='p'><span id='f'></span>" + words + "</p></div>");

        var fragments = Walk(Find(view, "c"))
            .Where(box => box.Source.Element?.GetAttribute("id") == "p")
            .ToList();

        Assert.That(fragments, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(fragments[0].Border.Top, Is.EqualTo(2), "the first fragment owns the top border");
            Assert.That(fragments[0].Padding.Top, Is.EqualTo(4));
            Assert.That(fragments[0].Border.Bottom, Is.EqualTo(0), "and not the bottom one");
            Assert.That(fragments[0].Padding.Bottom, Is.EqualTo(0));
            Assert.That(fragments[1].Border.Top, Is.EqualTo(0), "which belongs to the last fragment");
            Assert.That(fragments[1].Border.Bottom, Is.EqualTo(2));

            var firstLine = Walk(fragments[0]).First(box => box.Kind == LayoutKind.Line);
            var rest = Walk(fragments[1]).Where(box => box.Kind == LayoutKind.Line).ToList();

            Assert.That(firstLine.BorderBox.Width, Is.EqualTo(68).Within(0.5), "140 less the edges and the float");
            Assert.That(rest.All(line => Math.Abs(line.BorderBox.Width - 128) < 0.5), Is.True, "the next column plans inside the edges");
            Assert.That(Walk(fragments[0]).Count(box => box.Source.Element?.GetAttribute("id") == "f"), Is.EqualTo(1), "the float is drawn once");
            Assert.That(Walk(fragments[1]).Count(box => box.Source.Element?.GetAttribute("id") == "f"), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task AClonedFloatParagraphWrapsEveryColumnInItsOwnEdges()
    {
        var words = String.Join(" ", Enumerable.Repeat("aaaa", 30));

        using var view = await Render(
            "<style>#c { width: 300px; columns: 2; column-gap: 20px }" +
            " #p { margin: 0; border: 2px solid; padding: 4px; box-decoration-break: clone }" +
            " #f { float: right; width: 60px; height: 30px }</style>" +
            "<div id='c'><p id='p'><span id='f'></span>" + words + "</p></div>");

        var fragments = Walk(Find(view, "c"))
            .Where(box => box.Source.Element?.GetAttribute("id") == "p")
            .ToList();

        Assert.That(fragments, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            foreach (var fragment in fragments)
            {
                Assert.That(fragment.Border.Top, Is.EqualTo(2), "a cloned fragment draws the top border");
                Assert.That(fragment.Border.Bottom, Is.EqualTo(2), "and the bottom one");
                Assert.That(fragment.Padding.Top, Is.EqualTo(4));
                Assert.That(fragment.Padding.Bottom, Is.EqualTo(4));
            }

            Assert.That(Walk(fragments[0]).Count(box => box.Source.Element?.GetAttribute("id") == "f"), Is.EqualTo(1), "the float is still drawn once");
        });
    }

    [Test]
    public async Task AFloatParagraphBreaksIntoEveryColumnItsTextNeeds()
    {
        var words = String.Join(" ", Enumerable.Repeat("aaaa", 40));

        using var view = await Render(
            "<style>#c { width: 300px; columns: 3; column-gap: 10px }" +
            " #p { margin: 10px 0 20px } #f { float: right; width: 40px; height: 40px }</style>" +
            "<div id='c'><p id='p'><span id='f'></span>" + words + "</p></div>");

        var fragments = Walk(Find(view, "c"))
            .Where(box => box.Source.Element?.GetAttribute("id") == "p")
            .ToList();

        Assert.That(fragments, Has.Count.EqualTo(3), "the text is shared over all three columns");
        Assert.Multiple(() =>
        {
            Assert.That(fragments[0].BorderBox.X, Is.EqualTo(0));
            Assert.That(fragments[1].BorderBox.X, Is.EqualTo(103.33).Within(0.5), "each after the gap");
            Assert.That(fragments[2].BorderBox.X, Is.EqualTo(206.67).Within(0.5));
            Assert.That(fragments[0].BorderBox.Y, Is.EqualTo(10), "the paragraph's top margin stands above the first fragment");
            Assert.That(fragments[1].BorderBox.Y, Is.EqualTo(0), "and not above the ones after it");

            var firstLine = Walk(fragments[0]).First(box => box.Kind == LayoutKind.Line);
            var rest = fragments.Skip(1).SelectMany(Walk).Where(box => box.Kind == LayoutKind.Line).ToList();

            Assert.That(firstLine.BorderBox.Width, Is.EqualTo(53.33).Within(0.5), "the float shortens the first column's lines");
            Assert.That(rest, Is.Not.Empty);
            Assert.That(rest.All(line => Math.Abs(line.BorderBox.Width - 93.33) < 0.5), Is.True, "the others run at the column's width");
            Assert.That(Walk(fragments[0]).Count(box => box.Source.Element?.GetAttribute("id") == "f"), Is.EqualTo(1), "the float stands only in the first fragment");
        });
    }

    [Test]
    public async Task AFloatInAParagraphShortensOnlyTheColumnItStartsIn()
    {
        using var view = await Render(
            "<style>#c { width: 300px; columns: 2; column-gap: 20px }" +
            " #p { margin: 0 } #f { float: right; width: 60px; height: 40px }</style>" +
            "<div id='c'><p id='p'><span id='f'></span>" +
            "aaaa bbbb cccc dddd eeee ffff gggg hhhh iiii jjjj kkkk llll mmmm nnnn oooo</p></div>");

        var fragments = Walk(Find(view, "c"))
            .Where(box => box.Source.Element?.GetAttribute("id") == "p")
            .ToList();

        Assert.That(fragments, Has.Count.EqualTo(2), "the paragraph is broken across the two columns");
        Assert.Multiple(() =>
        {
            Assert.That(fragments[0].BorderBox.X, Is.EqualTo(0), "the first fragment opens the first column");
            Assert.That(fragments[1].BorderBox.X, Is.EqualTo(160), "the second stands after the gap");

            var firstLines = Walk(fragments[0]).Where(box => box.Kind == LayoutKind.Line).ToList();
            var secondLines = Walk(fragments[1]).Where(box => box.Kind == LayoutKind.Line).ToList();

            Assert.That(firstLines, Is.Not.Empty);
            Assert.That(secondLines, Is.Not.Empty);
            Assert.That(firstLines.All(line => line.BorderBox.Width == 80), Is.True, "the float shortens the first column's lines");
            Assert.That(secondLines.All(line => line.BorderBox.Width == 140), Is.True, "and leaves the second column's at their full width");
            Assert.That(Walk(fragments[0]).Count(box => box.Source.Element?.GetAttribute("id") == "f"), Is.EqualTo(1), "the float stands in the first fragment");
            Assert.That(Walk(fragments[1]).Count(box => box.Source.Element?.GetAttribute("id") == "f"), Is.EqualTo(0), "and only there");
        });
    }

    [Test]
    public async Task AFloatThatDoesNotFitInAColumnStartsTheNextOne()
    {
        using var view = await Render(
            "<style>#c { width: 300px; columns: 2; column-gap: 20px }" +
            " .f { float: left; height: 40px }</style>" +
            "<div id='c'><div class='f' id='f1' style='width:80px'></div>" +
            "<div class='f' id='f2' style='width:50px'></div>" +
            "<div class='f' id='f3' style='width:100px'></div></div>");

        Assert.Multiple(() =>
        {
            Assert.That(Find(view, "f1").BorderBox.X, Is.EqualTo(0), "the first float opens the first column");
            Assert.That(Find(view, "f2").BorderBox.X, Is.EqualTo(80), "the second stands beside it");
            Assert.That(
                Find(view, "f3").BorderBox.X,
                Is.EqualTo(160),
                "the third does not fit beside them, so it opens the column after the gap");
            Assert.That(Find(view, "c").BorderBox.Height, Is.EqualTo(40), "the box is as tall as its tallest column");
        });
    }

    [Test]
    public async Task ASpannerSpansEveryColumnBetweenTwoRuns()
    {
        using var view = await Render(
            "<style>#c { width: 300px; columns: 2; column-gap: 20px }" +
            " .b { height: 40px } .all { column-span: all; height: 10px }</style>" +
            "<div id='c'><div class='b' id='a1'></div><div class='b' id='a2'></div>" +
            "<div class='all' id='span'></div>" +
            "<div class='b' id='b1'></div><div class='b' id='b2'></div></div>");

        var first = Find(view, "a1");
        var second = Find(view, "a2");
        var span = Find(view, "span");
        var third = Find(view, "b1");
        var fourth = Find(view, "b2");

        Assert.That((first.BorderBox.X, first.BorderBox.Y), Is.EqualTo((0.0, 0.0)), "the first run fills the first column");
        Assert.That((second.BorderBox.X, second.BorderBox.Y), Is.EqualTo((160.0, 0.0)), "and its second child the second");
        Assert.That((span.BorderBox.X, span.BorderBox.Y), Is.EqualTo((0.0, 40.0)), "the spanner begins below the run");
        Assert.That(span.BorderBox.Width, Is.EqualTo(300), "and spans both columns and the gap");
        Assert.That((third.BorderBox.X, third.BorderBox.Y), Is.EqualTo((0.0, 50.0)), "the next run begins below the spanner");
        Assert.That((fourth.BorderBox.X, fourth.BorderBox.Y), Is.EqualTo((160.0, 50.0)), "in columns of its own");
        Assert.That(Find(view, "c").BorderBox.Height, Is.EqualTo(90), "the container is as tall as every row it holds");
    }

    private static async Task<HutuViewInternal> Render(String content)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>div { margin: 0 }</style></head>" +
            "<body style='margin:0;font-size:10px;line-height:10px'>" + content + "</body></html>");
        return view;
    }

    private static LayoutBox Find(HutuViewInternal view, String id) =>
        Walk(view.Layout!.Root).First(box => box.Source.Element?.GetAttribute("id") == id);

    private static List<LayoutBox> Blocks(HutuViewInternal view) =>
        Walk(Find(view, "c"))
            .Where(box => box != Find(view, "c") && box.Kind == LayoutKind.Block)
            .ToList();

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

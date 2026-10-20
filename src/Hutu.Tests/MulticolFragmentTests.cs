using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers a block child of a multi-column container that is taller than a column: its own lines are
/// shared over the columns, the fragment that resumes a break begins at the top of the next column, and
/// a child that refuses to break - or that brings something to be drawn at a fragment edge - stays whole
/// (CSS Multicol 3 §3.1, CSS Break 3 §4.4).
/// </summary>
/// <remarks>
/// The stand-in measurer gives every character five pixels and the lines stand ten apart, so a box 300
/// wide with two columns and a 20 pixel gap holds its child at 140 and one line is a tenth of a column
/// that is two lines tall.
/// </remarks>
[TestFixture]
public sealed class MulticolFragmentTests
{
    [Test]
    public async Task ABlockChildIsBrokenAtItsOwnLines()
    {
        using var view = await Render(
            "<style>#c { width: 300px; columns: 2; column-gap: 20px; column-fill: auto; height: 20px }</style>" +
            "<div id='c'><div>AAAA<br>BBBB<br>CCCC<br>DDDD</div></div>");

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "AAAA").BorderBox.X, Is.EqualTo(0), "the first line stands in the first column");
            Assert.That(Text(view, "AAAA").BorderBox.Y, Is.EqualTo(0), "at its top");
            Assert.That(Text(view, "BBBB").BorderBox.Y, Is.EqualTo(10), "the second follows it down");
            Assert.That(Text(view, "CCCC").BorderBox.X, Is.EqualTo(160), "the third falls to the next column");
            Assert.That(Text(view, "CCCC").BorderBox.Y, Is.EqualTo(0), "and begins that column at its top");
        });
    }

    [Test]
    public async Task AShortTailIsKeptWhenTheOrphansDemandIt()
    {
        using var view = await Render(
            "<style>#c { width: 300px; columns: 2; column-gap: 20px; column-fill: auto; height: 20px }</style>" +
            "<div id='c'><div>AAAA<br>BBBB<br>CCCC</div></div>");

        // Two lines fit the column and three are there. The break may not take the column below the two
        // orphans it owes, so the widows go short at the end instead - which is what Chrome gives the
        // same counts (CSS Break 3 §3.3).
        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "BBBB").BorderBox.X, Is.EqualTo(0), "the first column keeps two lines");
            Assert.That(Text(view, "CCCC").BorderBox.X, Is.EqualTo(160), "and the tail of one goes to the next");
        });
    }

    [Test]
    public async Task TheLinesOfAChildBalanceOverTheColumns()
    {
        using var view = await Render(
            "<style>#c { width: 300px; columns: 2; column-gap: 20px }</style>" +
            "<div id='c'><div>AAAA<br>BBBB<br>CCCC<br>DDDD</div></div>");

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "BBBB").BorderBox.X, Is.EqualTo(0), "two lines fill the first column");
            Assert.That(Text(view, "CCCC").BorderBox.X, Is.EqualTo(160), "and the next two the second");

            // The balanced extent is the height the search settled at, which comes to the line pair to
            // within the tolerance a fragment of that height accepts its last line by.
            Assert.That(Find(view, "c").BorderBox.Height, Is.EqualTo(20).Within(0.01), "the box is as tall as the columns balanced to");
        });
    }

    [Test]
    public async Task EveryLineOfTheChildIsDrawnOnce()
    {
        using var view = await Render(
            "<style>#c { width: 300px; columns: 2; column-gap: 20px; column-fill: auto; height: 20px }</style>" +
            "<div id='c'><div>AAAA<br>BBBB<br>CCCC</div></div>");

        Assert.That(
            Texts(view).Select(box => box.Text).ToList(),
            Is.EquivalentTo(new[] { "AAAA", "BBBB", "CCCC" }),
            "the fragments between them hold the content and nothing twice");
    }

    [Test]
    public async Task AChildThatRefusesToBreakStaysWhole()
    {
        using var view = await Render(
            "<style>#c { width: 300px; columns: 2; column-gap: 20px }" +
            " #p { break-inside: avoid }</style>" +
            "<div id='c'><div id='p'>AAAA<br>BBBB<br>CCCC<br>DDDD</div></div>");

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "CCCC").BorderBox.X, Is.EqualTo(0), "the break is not taken inside the child");
            Assert.That(Find(view, "c").BorderBox.Height, Is.EqualTo(40), "and the column takes the whole of it");
        });
    }

    [Test]
    public async Task AChildWithSomethingToDrawAtItsEdgeStaysWhole()
    {
        // A background, a border or padding would each have to be shared over the fragments by
        // `box-decoration-break`, which the columns do not do: such a child is not broken at all.
        using var view = await Render(
            "<style>#c { width: 300px; columns: 2; column-gap: 20px }" +
            " #p { background: rgb(1,2,3) }</style>" +
            "<div id='c'><div id='p'>AAAA<br>BBBB<br>CCCC<br>DDDD</div></div>");

        Assert.That(Text(view, "CCCC").BorderBox.X, Is.EqualTo(0), "the child is moved whole instead");
    }

    [Test]
    public async Task AChildWithADeclaredHeightStaysWhole()
    {
        using var view = await Render(
            "<style>#c { width: 300px; columns: 2; column-gap: 20px }" +
            " #p { height: 40px }</style>" +
            "<div id='c'><div id='p'>AAAA<br>BBBB<br>CCCC<br>DDDD</div></div>");

        Assert.That(Text(view, "CCCC").BorderBox.X, Is.EqualTo(0), "the height it states is not cut in half");
    }

    [Test]
    public async Task AChildWithBlockChildrenIsNotBrokenAtLines()
    {
        // Its own children break first, and that is the fragmentainer's next step: until it is here the
        // child stands whole rather than have its lines pulled apart under a block that could not split.
        using var view = await Render(
            "<style>#c { width: 300px; columns: 2; column-gap: 20px }" +
            " .b { height: 10px }</style>" +
            "<div id='c'><div><div class='b'></div><div class='b'></div><div class='b'></div><div class='b'></div></div></div>");

        var children = Find(view, "c").Children.Count;

        Assert.That(children, Is.EqualTo(1), "the one child is placed as a whole");
    }

    [Test]
    public async Task TheBreakInsideAChildKeepsItsOrphansBeforeItsWidows()
    {
        using var view = await Render(
            "<style>#c { width: 30px; columns: 3; column-gap: 0; column-fill: auto; height: 40px }" +
            " #p { orphans: 3; widows: 3 }</style>" +
            "<div id='c'><div id='p'>A<br>B<br>C<br>D<br>E<br>F<br>G<br>H<br>I</div></div>");

        // Nine lines, three of each asked for at both ends: the columns take four, three and two, which
        // is what Chrome's matrix gives the same counts (CSS Break 3 §3.3).
        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "D").BorderBox.X, Is.EqualTo(0), "the first column takes four lines");
            Assert.That(Text(view, "E").BorderBox.X, Is.EqualTo(10), "the second begins with the fifth");
            Assert.That(Text(view, "G").BorderBox.X, Is.EqualTo(10), "and holds three");
            Assert.That(Text(view, "H").BorderBox.X, Is.EqualTo(20), "the tail of two goes to the third");
        });
    }

    [Test]
    public async Task AChildAskingForNoLinesIsSpreadOverTheColumns()
    {
        using var view = await Render(
            "<style>#c { width: 20px; columns: 2; column-gap: 0 } #p { orphans: 1; widows: 1 }</style>" +
            "<div id='c'><div id='p'>AA<br>BB<br>CC</div></div>");

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "BB").BorderBox.X, Is.EqualTo(0), "two lines fill the first column");
            Assert.That(Text(view, "CC").BorderBox.X, Is.EqualTo(10), "and the third begins the next");
            Assert.That(Find(view, "c").BorderBox.Height, Is.EqualTo(20).Within(0.01), "the columns balance at two lines");
        });
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

    private static LayoutBox Text(HutuViewInternal view, String text) =>
        Texts(view).First(box => box.Text == text);

    private static IEnumerable<LayoutBox> Texts(HutuViewInternal view) =>
        Walk(view.Layout!.Root).Where(box => box.Kind == LayoutKind.Text);

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

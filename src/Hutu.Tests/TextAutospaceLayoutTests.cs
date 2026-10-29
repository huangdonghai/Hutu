using Hutu.Rendering.Layout;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers where <c>text-autospace</c> puts its room: between an ideograph and a letter or a digit
/// inside one run, across the edge of an inline element, in a column, and nowhere at all beside text
/// a column stands upright (CSS Text 4 §8.4).
/// </summary>
/// <remarks>
/// The fake face advances every character by five pixels, so an eighth of a CJK advance is five
/// eighths and a run that holds one such boundary is that much wider than its characters.
/// </remarks>
[TestFixture]
public sealed class TextAutospaceLayoutTests
{
    private const Double Room = 5 * 0.125;

    [Test]
    public async Task AnIdeographAndALetterAreSetApart()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<p style='margin:0;font-size:10px'>国A</p>"));

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "国A").BorderBox.Width, Is.EqualTo(10 + Room).Within(0.001));
            Assert.That(Text(view, "国A").AutoSpaces, Is.EqualTo("\u0001\0"));
        });
    }

    [Test]
    public async Task ALatinWordAndAnIdeographAre()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<p style='margin:0;font-size:22px'>abc永</p>"));

        Assert.That(Text(view, "abc永").BorderBox.Width, Is.EqualTo(20 + Room).Within(0.001));
    }

    [Test]
    public async Task TwoIdeographsAreNot()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<p style='margin:0;font-size:10px'>国国</p>"));

        Assert.That(Text(view, "国国").BorderBox.Width, Is.EqualTo(10).Within(0.001));
    }

    [Test]
    public async Task OnlyTheClassTheValueNamesIsSpaced()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            "<p style='margin:0;font-size:10px;text-autospace:ideograph-numeric'>国A国1</p>"));

        // The digit takes the room the letter does not, and the value is inherited by the paragraph.
        Assert.That(Text(view, "国A国1").BorderBox.Width, Is.EqualTo(20 + Room).Within(0.001));
    }

    [Test]
    public async Task NoAutospaceLeavesTheRoomToTheCharacters()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<p style='margin:0;font-size:10px;text-autospace:no-autospace'>国A</p>"));

        Assert.That(Text(view, "国A").BorderBox.Width, Is.EqualTo(10).Within(0.001));
    }

    [Test]
    public async Task ALetterInItsOwnElementIsStillANeighbour()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<p style='margin:0;font-size:10px'>国<span>A</span></p>"));

        // The boundary is between two runs, so the room is the gap between their boxes rather than a
        // mask inside one of them.
        var ideograph = Text(view, "国");
        var letter = Text(view, "A");

        Assert.Multiple(() =>
        {
            Assert.That(letter.BorderBox.X, Is.EqualTo(5 + Room).Within(0.001), "the letter stands after the room");
            Assert.That(ideograph.BorderBox.Right, Is.EqualTo(5 + Room).Within(0.001), "the room closes the run before");
            Assert.That(ideograph.AutoSpaces, Is.Null);
            Assert.That(letter.AutoSpaces, Is.Null);
        });
    }

    [Test]
    public async Task AColumnSetsItsNeighboursApartDownTheColumn()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            "<div style='writing-mode:vertical-rl;font-size:10px;line-height:1'>国X国</div>"));

        // The column is three runs - the turned X between two upright ideographs - and the last of
        // them ends two boundaries below where its characters alone would.
        Assert.That(Text(view, "国").BorderBox.Height, Is.EqualTo(10 + Room).Within(0.001));
        Assert.That(Walk(view).Where(node => node.Kind == LayoutKind.Text).Last().BorderBox.Bottom,
            Is.EqualTo(25 + 2 * Room).Within(0.001));
    }

    [Test]
    public async Task AnUprightNeighbourIsNoNeighbour()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            "<div style='writing-mode:vertical-rl;font-size:10px;line-height:1'>国" +
            "<span style='text-orientation:upright'>X</span>国</div>"));

        Assert.That(Text(view, "X").BorderBox.Height, Is.EqualTo(10).Within(0.001));
        Assert.That(Text(view, "国").BorderBox.Height, Is.EqualTo(10).Within(0.001));
    }

    [Test]
    public async Task ASelectorStandingOnTheIdeographKeepsTheBoundary()
    {
        using var view = CreateView();

        // The supplementary selector continues the ideograph, so the letter beside the two is still
        // one the property sets apart - the boundary stands after the character the selector completes.
        await view.SetHtml(Document("<p style='margin:0;font-size:10px'>国0A</p>"));

        Assert.That(Text(view, "国0A").BorderBox.Width, Is.EqualTo(20 + Room).Within(0.001));
    }

    [Test]
    public async Task ReplaceTakesThePlaceOfTheSpaceItFinds()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            "<p style='margin:0;font-size:10px;text-autospace:ideograph-alpha replace'>国 A</p>"));

        // Three characters' room less the space's own five pixels, plus the eighth the boundary is.
        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "国 A").BorderBox.Width, Is.EqualTo(10 + Room).Within(0.001));
            Assert.That(Text(view, "国 A").AutoSpaces, Is.EqualTo("\0\u0002\0"));
        });
    }

    [Test]
    public async Task TheDefaultSheetLeavesSourceLikeTextAlone()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<pre style='margin:0;font-size:10px'>国A</pre>"));

        Assert.That(Text(view, "国A").BorderBox.Width, Is.EqualTo(10).Within(0.001));
    }

    private static String Document(String content) =>
        "<html><body style='margin:0;font-size:16px;line-height:1'>" + content + "</body></html>";

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(600, 600);
        return view;
    }

    private static LayoutBox Text(HutuViewInternal view, String text) =>
        Walk(view).First(node => node.Kind == LayoutKind.Text && node.Text == text);

    private static IEnumerable<LayoutBox> Walk(HutuViewInternal view)
    {
        var root = view.Layout?.Root;

        return root is null ? Enumerable.Empty<LayoutBox>() : Walk(root);
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
}

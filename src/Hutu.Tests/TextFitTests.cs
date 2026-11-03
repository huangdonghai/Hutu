using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers <c>text-fit</c>: the lines a block scales so their text fills the line box, which lines the
/// scaling reaches, and the limit a percentage puts on the factor (CSS Text 5 §1.1).
/// </summary>
/// <remarks>
/// The fake measurer advances every character by five pixels, so a line's factor is the arithmetic of the
/// widths asserted here and nothing the machine's fonts decide. <c>white-space: pre</c> keeps the lines
/// where the page put them, which is what makes each line's extent a known number.
/// </remarks>
[TestFixture]
public sealed class TextFitTests
{
    /// <summary>
    /// A hundred pixels of line, four characters on the first and ten on the second: the first asks for
    /// five times its size, the second for twice.
    /// </summary>
    private static String Fitted(String rule) =>
        "<html><body style='margin:0'><p style='margin:0;width:100px;font-size:10px;line-height:1;" +
        "white-space:pre;" + rule + "'>" + new String('あ', 4) + "\n" + new String('あ', 10) +
        "</p></body></html>";

    [Test]
    public async Task AConsistentFitScalesEveryLineByTheLeastTheLinesAskedFor()
    {
        using var view = CreateView();

        await view.SetHtml(Fitted("text-fit:grow"));

        var runs = Runs(view);

        Assert.That(runs, Has.Count.EqualTo(2), "the break the page wrote stands, and nothing wraps");
        Assert.That(runs[0].BorderBox.Width, Is.EqualTo(40).Within(0.5), "twenty pixels of text grown by the smaller factor");
        Assert.That(runs[1].BorderBox.Width, Is.EqualTo(100).Within(0.5), "and the second line filled exactly, as it asked");
    }

    [Test]
    public async Task APerLineFitLeavesTheLastLineAndAForcedBreakAlone()
    {
        using var view = CreateView();

        // Both of the lines this block holds are ones `per-line` holds back: one ends where the page
        // forced a break, the other is the last line of the block (CSS Text 5 §1.1).
        await view.SetHtml(Fitted("text-fit:grow per-line"));

        var runs = Runs(view);

        Assert.That(runs[0].BorderBox.Width, Is.EqualTo(20).Within(0.5), "the line the break ended stands at its written size");
        Assert.That(runs[1].BorderBox.Width, Is.EqualTo(50).Within(0.5), "and so does the line that ends the block");
    }

    [Test]
    public async Task APerLineAllFitScalesTheLastLineToo()
    {
        using var view = CreateView();

        await view.SetHtml(Fitted("text-fit:grow per-line-all"));

        var runs = Runs(view);

        Assert.That(runs[0].BorderBox.Width, Is.EqualTo(100).Within(0.5), "each line grows to its own edge");
        Assert.That(runs[1].BorderBox.Width, Is.EqualTo(100).Within(0.5));
    }

    [Test]
    public async Task APerLineFitReachesALineTheTextBroughtToAnEnd()
    {
        using var view = CreateView();

        // Three lines of ten characters each, broken where the room ran out: the first two are grown to
        // fill the hundred pixels they stand in, while the last is the one `per-line' holds back.
        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;width:100px;font-size:10px;line-height:1;" +
            "text-fit:grow per-line'>" +
            new String('x', 10) + " " + new String('x', 10) + " " + new String('x', 10) +
            "</p></body></html>");

        var runs = Runs(view);

        Assert.That(runs, Has.Count.EqualTo(3));
        Assert.That(runs[0].BorderBox.Width, Is.EqualTo(100).Within(0.5), "a line the text wrapped at is scaled");
        Assert.That(runs[1].BorderBox.Width, Is.EqualTo(100).Within(0.5));
        Assert.That(runs[2].BorderBox.Width, Is.EqualTo(50).Within(0.5), "the line that ends the block is not");
    }

    [Test]
    public async Task AShrinkFitLeavesACommandLineWithRoomToSpareAlone()
    {
        using var view = CreateView();

        // Forty characters ask for two hundred pixels of a line that has a hundred, and are halved to it.
        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;width:100px;font-size:10px;line-height:1;" +
            "white-space:pre;text-fit:shrink'>" + new String('あ', 40) + "</p></body></html>");

        Assert.That(Runs(view)[0].BorderBox.Width, Is.EqualTo(100).Within(0.5), "the text is halved to its hundred pixels");
    }

    [Test]
    public async Task APercentageLimitsHowFarAShrinkFitMayGo()
    {
        using var view = CreateView();

        // The factor the line asks for is a half, and the 75% the page wrote is the floor it is kept above:
        // the line overflows its box rather than go smaller than three quarters.
        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;width:100px;font-size:10px;line-height:1;" +
            "white-space:pre;text-fit:shrink per-line-all 75%'>" + new String('あ', 40) + "</p></body></html>");

        Assert.That(Runs(view)[0].BorderBox.Width, Is.EqualTo(150).Within(0.5));
    }

    [Test]
    public async Task AFitIsInheritedDownToTheTextOfTheBoxThatAskedForIt()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'><div style='width:100px;text-fit:grow'>" +
            "<p style='margin:0;font-size:10px;line-height:1;white-space:pre'>" + new String('あ', 10) + "</p>" +
            "</div></body></html>");

        Assert.That(Runs(view)[0].BorderBox.Width, Is.EqualTo(100).Within(0.5), "the paragraph fits to the box the sheet carried the property on");
    }

    [Test]
    public async Task ALineHeightWrittenAsAMultipleGrowsWithTheFitWhileALengthStands()
    {
        using var view = CreateView();

        // Both lines are grown to twice their size, so ten characters fill the hundred pixels they stand
        // in. A line height of `1` is a multiple of the font the text is drawn at and grows with it; one
        // written as ten pixels is the page's own number and stays (CSS Text 5 §1.1).
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='margin:0;width:100px;font-size:10px;line-height:1;white-space:pre;text-fit:grow'>" +
            new String('x', 10) + "</p>" +
            "<p style='margin:0;width:100px;font-size:10px;line-height:10px;white-space:pre;text-fit:grow'>" +
            new String('x', 10) + "</p></body></html>");

        var lines = Walk(view.Layout!.Root).Where(node => node.Kind == LayoutKind.Line).ToList();

        Assert.That(lines, Has.Count.EqualTo(2));
        Assert.That(lines[0].BorderBox.Height, Is.EqualTo(20).Within(0.5), "the multiple follows the grown font");
        Assert.That(lines[1].BorderBox.Height, Is.EqualTo(10).Within(0.5), "the length stands at what the page wrote");
    }

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(600, 400);
        return view;
    }

    /// <summary>Gets the text boxes of the layout, which is one per run and so one per line here.</summary>
    private static List<LayoutBox> Runs(HutuViewInternal view) =>
        Walk(view.Layout!.Root).Where(node => node.Kind == LayoutKind.Text).ToList();

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

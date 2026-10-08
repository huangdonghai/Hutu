using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the indentation of a box's first line: the distance it is set in by, which the line it is
/// set on has to leave room for, and which a negative one hangs the line out of the box by.
/// </summary>
/// <remarks>
/// The measurer is the fixed-width one - five pixels a character, the font size being what a column
/// advances an upright character by - so the geometry here is arithmetic.
/// </remarks>
[TestFixture]
public sealed class TextIndentTests
{
    /// <summary>
    /// A hundred pixels of line, and thirty characters that break one by one: a line holds twenty of
    /// them.
    /// </summary>
    private static String Wrapped(String rule) =>
        "<html><body style='margin:0'><p style='margin:0;width:100px;font-size:10px;line-height:1;" + rule + "'>" +
        new String('あ', 30) + "</p></body></html>";

    [Test]
    public async Task TheFirstLineOfARowIsSetIn()
    {
        using var view = CreateView();

        await view.SetHtml(Wrapped("text-indent:20px"));

        var runs = Runs(view);

        Assert.That(runs, Has.Count.EqualTo(2), "thirty characters are more than the sixteen the indentation leaves on one line");
        Assert.That(runs[0].BorderBox.X, Is.EqualTo(20).Within(0.5), "and the first of them is set in by the indentation");
        Assert.That(runs[1].BorderBox.X, Is.EqualTo(0).Within(0.5), "while the line after it begins where the box does");
    }

    [Test]
    public async Task TheFirstLineOfAColumnIsSetInAlongIt()
    {
        using var view = CreateView();

        // Twenty pixels of column is two characters of it, which the first line is set down by.
        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;height:200px;margin:0;font-size:10px;line-height:1'>" +
            "<p style='margin:0;text-indent:20px'>" + new String('あ', 30) + "</p></div></body></html>");

        var runs = Runs(view);

        Assert.That(runs, Has.Count.EqualTo(2), "eighteen characters fit down the column once two of them are given to the indentation");
        Assert.That(runs[0].BorderBox.Y, Is.EqualTo(20).Within(0.5), "and the first of them begins two characters down it");
        Assert.That(runs[1].BorderBox.Y, Is.EqualTo(0).Within(0.5), "while the column after it begins at the top");
    }

    [Test]
    public async Task AnIndentationHangsTheFirstLineOutOfTheBoxWhenItIsNegative()
    {
        using var view = CreateView();

        await view.SetHtml(Wrapped("text-indent:-20px"));

        var runs = Runs(view);

        Assert.That(runs[0].BorderBox.X, Is.EqualTo(-20).Within(0.5), "a negative indentation sets the first line out of the box");
        Assert.That(
            runs,
            Has.Count.EqualTo(2),
            "and it is twenty pixels more room the line has, not less: twenty-four characters and then six");
        Assert.That(runs[0].BorderBox.Width, Is.EqualTo(120).Within(0.5));
    }

    [Test]
    public async Task AnIndentationInPercentIsAShareOfTheContainingBlock()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'><div style='width:200px'>" +
            "<p style='margin:0;font-size:10px;line-height:1;text-indent:10%'>text</p>" +
            "</div></body></html>");

        Assert.That(
            Runs(view)[0].BorderBox.X,
            Is.EqualTo(20).Within(0.5),
            "a tenth of the two hundred the text sits in");
    }

    [Test]
    public async Task AnIndentationIsInherited()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0;text-indent:20px'>" +
            "<p style='margin:0;font-size:10px;line-height:1'>text</p></body></html>");

        Assert.That(Runs(view)[0].BorderBox.X, Is.EqualTo(20).Within(0.5));
    }

    [Test]
    public async Task TheContentOfAnIndentedBoxAsksForTheExtraRoom()
    {
        using var view = CreateView();

        // An inline-block is as wide as its content asks for, and its content is a line set in by the
        // indentation - so the box is the indentation wider than the text on it.
        await view.SetHtml(
            "<html><body style='margin:0'><div style='display:inline-block;font-size:10px;line-height:1;text-indent:20px'>abcd</div></body></html>");

        Assert.That(Box(view, "div").BorderBox.Width, Is.EqualTo(40).Within(0.5), "twenty pixels of text and twenty of indentation");
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

    private static LayoutBox Box(HutuViewInternal view, String tag) =>
        Walk(view.Layout!.Root).First(node => node.Source.Element?.LocalName == tag);

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

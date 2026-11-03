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

    /// <summary>
    /// A run of characters a hundred-pixel line holds, a forced break, and one character after it. At
    /// twenty the line is filled exactly, so the break is the only thing that begins a second line; at
    /// twenty-one the text wraps once first, and the line that wrap began is one no forced break did.
    /// </summary>
    private static String Broken(String rule, Int32 before = 20) =>
        "<html><body style='margin:0'><p style='margin:0;width:100px;font-size:10px;line-height:1;" + rule + "'>" +
        new String('あ', before) + "<br>" + new String('あ', 1) + "</p></body></html>";

    [Test]
    public async Task AHangingIndentationSetsEveryLineButTheFirst()
    {
        using var view = CreateView();

        await view.SetHtml(Wrapped("text-indent:20px hanging"));

        var runs = Runs(view);

        Assert.That(runs, Has.Count.EqualTo(2));
        Assert.That(runs[0].BorderBox.X, Is.EqualTo(0).Within(0.5), "the line the block begins with is the one left out");
        Assert.That(runs[1].BorderBox.X, Is.EqualTo(20).Within(0.5), "and every other line carries the distance");
    }

    [Test]
    public async Task AnEachLineIndentationSetsTheLineAForcedBreakBegan()
    {
        using var view = CreateView();

        await view.SetHtml(Broken("text-indent:20px each-line"));

        var runs = Runs(view);

        Assert.That(runs, Has.Count.EqualTo(3));
        Assert.That(runs[0].BorderBox.X, Is.EqualTo(20).Within(0.5), "the block's first line, as without the modifier");
        Assert.That(runs[1].BorderBox.X, Is.EqualTo(0).Within(0.5), "a line the text wrapped at is not one the property names");
        Assert.That(runs[2].BorderBox.X, Is.EqualTo(20).Within(0.5), "while the line the break began is");
    }

    [Test]
    public async Task AHangingIndentationLeavesOutTheLineAForcedBreakBeganToo()
    {
        using var view = CreateView();

        // Both modifiers together: the named lines are the block's first and the one after the break,
        // and hanging puts the distance on the line between them and nowhere else. Twenty-one
        // characters, because a line filled exactly by the first of them asks for no second line.
        await view.SetHtml(Broken("text-indent:20px each-line hanging", before: 21));

        var runs = Runs(view);

        Assert.That(runs, Has.Count.EqualTo(3));
        Assert.That(runs[0].BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(runs[1].BorderBox.X, Is.EqualTo(20).Within(0.5));
        Assert.That(runs[2].BorderBox.X, Is.EqualTo(0).Within(0.5));
    }

    [Test]
    public async Task AnIndentationDoesNotReachTheRunsABlockChildLeavesBehind()
    {
        using var view = CreateView();

        // The words beside the inner block are wrapped in anonymous blocks, and only the first of them
        // begins the box the indentation was written for.
        await view.SetHtml(
            "<html><body style='margin:0'><div style='margin:0;width:100px;font-size:10px;line-height:1;text-indent:20px'>" +
            "ab<div style='font-size:10px'>cd</div>ef</div></body></html>");

        var runs = Runs(view);

        Assert.That(runs, Has.Count.EqualTo(3));
        Assert.That(runs[0].BorderBox.X, Is.EqualTo(20).Within(0.5), "the run the box begins with is its first line");
        Assert.That(runs[1].BorderBox.X, Is.EqualTo(20).Within(0.5), "a block inside it is a box with an indentation of its own");
        Assert.That(runs[2].BorderBox.X, Is.EqualTo(0).Within(0.5), "the run that resumes after the block begins no line of the box");
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

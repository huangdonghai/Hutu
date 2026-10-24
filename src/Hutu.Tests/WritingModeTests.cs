using Hutu.Rendering.Layout;
using NUnit.Framework;

namespace Hutu.Tests;

/// <summary>
/// Covers the axis mapping that vertical writing is built on: which physical edge each logical axis
/// starts at, and where a logical frame lands on a rectangle. Every other vertical path is written in
/// these terms, so a mistake here would show up everywhere.
/// </summary>
[TestFixture]
public sealed class WritingModeTests
{
    private static readonly Rect Frame = new(10, 20, 100, 200);

    [Test]
    public void HorizontalRunsInlineAcrossTheWidthAndBlockDownTheHeight()
    {
        var flow = WritingModeInfo.Horizontal;

        Assert.That(flow.IsVertical, Is.False);
        Assert.That(flow.InlineSign, Is.EqualTo(1));
        Assert.That(flow.BlockSign, Is.EqualTo(1));
        Assert.That(flow.InlineExtent(100, 200), Is.EqualTo(100));
        Assert.That(flow.BlockExtent(100, 200), Is.EqualTo(200));

        // An inline offset moves along x and a block offset down y.
        var mapped = flow.Map(Frame, 5, 30, 7, 40);

        Assert.That(mapped.X, Is.EqualTo(15));
        Assert.That(mapped.Y, Is.EqualTo(27));
        Assert.That(mapped.Width, Is.EqualTo(30));
        Assert.That(mapped.Height, Is.EqualTo(40));

        // A distance from the block-start edge is already measured from the left one.
        Assert.That(flow.BlockOffsetFromLeft(200, 8), Is.EqualTo(8));

        var size = flow.SizeOf(40, 30);

        Assert.That(size.Width, Is.EqualTo(40));
        Assert.That(size.Height, Is.EqualTo(30));
    }

    [Test]
    public void VerticalRlRunsInlineDownTheHeightAndBlockLeftward()
    {
        var flow = new WritingModeInfo(WritingMode.VerticalRl);

        Assert.That(flow.IsVertical, Is.True);
        Assert.That(flow.InlineSign, Is.EqualTo(1));
        Assert.That(flow.BlockSign, Is.EqualTo(-1));
        Assert.That(flow.InlineExtent(100, 200), Is.EqualTo(200));
        Assert.That(flow.BlockExtent(100, 200), Is.EqualTo(100));

        // The first column sits against the right edge and runs down from the top.
        var first = flow.Map(Frame, 0, 40, 0, 30);

        Assert.That(first.X, Is.EqualTo(80));
        Assert.That(first.Y, Is.EqualTo(20));
        Assert.That(first.Width, Is.EqualTo(30));
        Assert.That(first.Height, Is.EqualTo(40));

        // The column after it is the one to its left.
        var second = flow.Map(Frame, 0, 40, 30, 30);

        Assert.That(second.X, Is.EqualTo(50));
        Assert.That(second.Right, Is.EqualTo(first.X));

        // A distance measured from the right edge is mirrored when measured from the left.
        Assert.That(flow.BlockOffsetFromLeft(30, 8), Is.EqualTo(22));

        var size = flow.SizeOf(40, 30);

        Assert.That(size.Width, Is.EqualTo(30));
        Assert.That(size.Height, Is.EqualTo(40));
    }

    [Test]
    public void VerticalLrStacksColumnsToTheRight()
    {
        var flow = new WritingModeInfo(WritingMode.VerticalLr);

        Assert.That(flow.IsVertical, Is.True);
        Assert.That(flow.BlockSign, Is.EqualTo(1));

        var first = flow.Map(Frame, 0, 40, 0, 30);
        var second = flow.Map(Frame, 0, 40, 30, 30);

        Assert.That(first.X, Is.EqualTo(10));
        Assert.That(second.X, Is.EqualTo(40));

        // A column's glyphs are turned about the baseline, so the distance the layout holds from the
        // block-start edge is mirrored to be expressed from the left whichever way the columns run -
        // measured in Chrome: a 20px glyph in a 20px column of vertical-lr inks at x=0, not x=12.
        Assert.That(flow.BlockOffsetFromLeft(30, 8), Is.EqualTo(22));
    }

    [Test]
    public void SidewaysModesTurnEveryCharacterHoweverWideEachModeWrites()
    {
        var rl = new WritingModeInfo(WritingMode.SidewaysRl);

        Assert.That(rl.IsVertical, Is.True);
        Assert.That(rl.InlineSign, Is.EqualTo(1));
        Assert.That(rl.BlockSign, Is.EqualTo(-1));
        Assert.That(rl.IsUpright('あ'), Is.False, "the whole line is turned in a sideways mode");

        var lr = new WritingModeInfo(WritingMode.SidewaysLr);

        Assert.That(lr.InlineSign, Is.EqualTo(-1));
        Assert.That(lr.BlockSign, Is.EqualTo(1));
    }

    [Test]
    public void MixedUprightsIdeographsAndTurnsLetters()
    {
        var flow = new WritingModeInfo(WritingMode.VerticalRl, TextOrientation.Mixed);

        Assert.That(flow.IsUpright('あ'), Is.True);
        Assert.That(flow.IsUpright('漢'), Is.True);
        Assert.That(flow.IsUpright('。'), Is.True);
        Assert.That(flow.IsUpright('A'), Is.False);
        Assert.That(flow.IsUpright('7'), Is.False);
    }

    [Test]
    public void UprightStandsLettersUpAndSidewaysLaysIdeographsDown()
    {
        var upright = new WritingModeInfo(WritingMode.VerticalRl, TextOrientation.Upright);

        Assert.That(upright.IsUpright('A'), Is.True);
        Assert.That(upright.IsUpright('あ'), Is.True);

        var sideways = new WritingModeInfo(WritingMode.VerticalRl, TextOrientation.Sideways);

        Assert.That(sideways.IsUpright('あ'), Is.False);
        Assert.That(sideways.IsUpright('A'), Is.False);
    }

    [Test]
    public void EachAxisStartsAtThePhysicalEdgeItsModeReadsFrom()
    {
        Assert.That(WritingModeInfo.Horizontal.InlineStart(Frame), Is.EqualTo(10));
        Assert.That(WritingModeInfo.Horizontal.BlockStart(Frame), Is.EqualTo(20));

        // A right-to-left column starts at the right edge; one that reads the other way at the left.
        Assert.That(new WritingModeInfo(WritingMode.VerticalRl).BlockStart(Frame), Is.EqualTo(110));
        Assert.That(new WritingModeInfo(WritingMode.VerticalLr).BlockStart(Frame), Is.EqualTo(10));

        // Text runs down a column in every mode but the one that reads upward.
        Assert.That(new WritingModeInfo(WritingMode.VerticalRl).InlineStart(Frame), Is.EqualTo(20));
        Assert.That(new WritingModeInfo(WritingMode.VerticalLr).InlineStart(Frame), Is.EqualTo(20));
        Assert.That(new WritingModeInfo(WritingMode.SidewaysLr).InlineStart(Frame), Is.EqualTo(220));
    }

    /// <summary>
    /// A <c>direction: rtl</c> turns a column's line around - the text begins at its foot and runs up
    /// the page - while the columns themselves keep marching the way the writing mode put them. The
    /// two signs are therefore decided by different properties, which is what a row does not show:
    /// there the same property decides both. Measured in Chrome for every mode.
    /// </summary>
    [Test]
    public void RightToLeftDirectionRunsAColumnBackwardAndKeepsItsColumns()
    {
        var rl = Flow(WritingMode.VerticalRl, TextDirection.RightToLeft);
        var lr = Flow(WritingMode.VerticalLr, TextDirection.RightToLeft);
        var sidewaysLr = Flow(WritingMode.SidewaysLr, TextDirection.RightToLeft);
        var sidewaysRl = Flow(WritingMode.SidewaysRl, TextDirection.RightToLeft);

        Assert.That(rl.InlineSign, Is.EqualTo(-1), "the line reads up the page");
        Assert.That(rl.InlineStart(Frame), Is.EqualTo(220), "and begins at its foot");
        Assert.That(rl.BlockSign, Is.EqualTo(-1), "the columns still start at the right edge");
        Assert.That(rl.BlockStart(Frame), Is.EqualTo(110));

        Assert.That(lr.InlineSign, Is.EqualTo(-1));
        Assert.That(lr.BlockSign, Is.EqualTo(1), "and vertical-lr still stacks to the right");

        // The sideways modes already read one way up the page, and the direction turns each back.
        Assert.That(sidewaysLr.InlineSign, Is.EqualTo(1));
        Assert.That(sidewaysRl.InlineSign, Is.EqualTo(-1));
    }

    /// <summary>
    /// Which end of a column a float side names. The logical spellings name the ends of the line, so a
    /// direction that turns the line turns them with it; the physical ones name line-left and
    /// line-right, which are the head and the foot of a column whichever way the text runs. Measured in
    /// Chrome, where a right-to-left column keeps its <c>left</c> float at the head and moves its
    /// <c>inline-start</c> one to the foot.
    /// </summary>
    [Test]
    public void AFloatSideNamesTheEndOfTheLineItsKeywordsPointAt()
    {
        var down = Flow(WritingMode.VerticalRl, TextDirection.LeftToRight);
        var up = Flow(WritingMode.VerticalRl, TextDirection.RightToLeft);

        Assert.That(down.FloatsAtTheInlineStart(FloatSide.Left), Is.True);
        Assert.That(down.FloatsAtTheInlineStart(FloatSide.Right), Is.False);
        Assert.That(down.FloatsAtTheInlineStart(FloatSide.InlineStart), Is.True);
        Assert.That(down.FloatsAtTheInlineStart(FloatSide.InlineEnd), Is.False);

        // The head of a line that reads upward is its bottom edge, so the physical pair changes sides
        // with respect to the line while the page keeps still.
        Assert.That(up.FloatsAtTheInlineStart(FloatSide.Left), Is.False);
        Assert.That(up.FloatsAtTheInlineStart(FloatSide.Right), Is.True);
        Assert.That(up.FloatsAtTheInlineStart(FloatSide.InlineStart), Is.True);
        Assert.That(up.FloatsAtTheInlineStart(FloatSide.InlineEnd), Is.False);

        // A row is the same rule along the other axis: the physical left is where the line begins only
        // while the line reads rightwards.
        Assert.That(Flow(WritingMode.HorizontalTb, TextDirection.RightToLeft).FloatsAtTheInlineStart(FloatSide.Left), Is.False);
        Assert.That(WritingModeInfo.Horizontal.FloatsAtTheInlineStart(FloatSide.Left), Is.True);
    }

    private static WritingModeInfo Flow(WritingMode mode, TextDirection direction) =>
        new(mode, TextOrientation.Mixed, false, direction);
}

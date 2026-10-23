using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Hutu.Tests;

/// <summary>
/// Covers how a block's children are cut at a fragment's end (CSS Break 3 §4): the children that fit,
/// the avoid pairs that keep neighbours together, and the forced values that end a fragment.
/// </summary>
[TestFixture]
public sealed class ChildSlicerTests
{
    [Test]
    public void TheChildrenThatFitAreTaken()
    {
        var cut = ChildSlicer.Fitting(Children(3), 25);

        Assert.That(cut.Fits, Is.EqualTo(2), "two ten-pixel children fit in twenty-five");
        Assert.That(cut.Forced, Is.False);
    }

    [Test]
    public void EverythingThatFitsIsTaken()
    {
        Assert.That(ChildSlicer.Fitting(Children(3), 100).Fits, Is.EqualTo(3));
    }

    [Test]
    public void AForcedBreakBeforeAChildEndsTheFragmentThere()
    {
        var children = Children(3);
        children[1] = new FragmentChild(10, BreakValue.Page, BreakValue.Auto, BreakValue.Auto);

        var cut = ChildSlicer.Fitting(children, 100);

        Assert.That(cut.Fits, Is.EqualTo(1), "the page break stands between the first and the second");
        Assert.That(cut.Forced, Is.True, "and a fresh fragment begins after it");
    }

    [Test]
    public void AForcedBreakAfterAChildEndsTheFragmentThere()
    {
        var children = Children(3);
        children[0] = new FragmentChild(10, BreakValue.Auto, BreakValue.Column, BreakValue.Auto);

        var cut = ChildSlicer.Fitting(children, 100);

        Assert.That(cut.Fits, Is.EqualTo(1));
        Assert.That(cut.Forced, Is.True);
    }

    [Test]
    public void AForcedBreakBeforeTheFirstChildIsAlreadySatisfied()
    {
        var children = Children(2);
        children[0] = new FragmentChild(10, BreakValue.Page, BreakValue.Auto, BreakValue.Auto);

        Assert.That(ChildSlicer.Fitting(children, 100).Fits, Is.EqualTo(2), "the fragment begins where the break asked it to");
    }

    [Test]
    public void ANeighbourAPageKeepsWithIsMovedWhole()
    {
        var children = Children(3);
        children[0] = new FragmentChild(10, BreakValue.Auto, BreakValue.Avoid, BreakValue.Auto);

        var cut = ChildSlicer.Fitting(children, 15);

        Assert.That(cut.Fits, Is.EqualTo(1), "the break may not fall between the first child and the second, so the fragment overflows with the first");
    }

    [Test]
    public void AnAvoidBeforeTheNextChildMovesTheCutBack()
    {
        var children = Children(4);
        children[2] = new FragmentChild(10, BreakValue.Avoid, BreakValue.Auto, BreakValue.Auto);

        var cut = ChildSlicer.Fitting(children, 25);

        Assert.That(cut.Fits, Is.EqualTo(1), "two children fit, but the break before the third is forbidden and the one before the second is left");
    }

    [Test]
    public void AChildThatDoesNotFitBeginsTheNextFragment()
    {
        var children = Children(2);
        children[1] = new FragmentChild(40, BreakValue.Auto, BreakValue.Auto, BreakValue.Avoid);

        Assert.That(ChildSlicer.Fitting(children, 25).Fits, Is.EqualTo(1), "an unbreakable child taller than what is left moves whole");
    }

    [Test]
    public void ARunThatCannotKeepItsOrphansBeginsInTheNextFragment()
    {
        var children = new List<FragmentChild>
        {
            new(10, BreakValue.Auto, BreakValue.Auto, BreakValue.Auto, run: 0, orphans: 2, widows: 2),
            new(10, BreakValue.Auto, BreakValue.Auto, BreakValue.Auto, run: 0, orphans: 2, widows: 2),
        };

        Assert.That(
            ChildSlicer.Fitting(children, 15).Fits,
            Is.EqualTo(0),
            "one line of a two-line block would break the orphans, so the block starts in the next fragment");
    }

    [Test]
    public void AnOrphansRuleYieldsToAnAvoidedBoundary()
    {
        var children = new List<FragmentChild>
        {
            new(10, BreakValue.Auto, BreakValue.Avoid, BreakValue.Auto),
            new(10, BreakValue.Avoid, BreakValue.Auto, BreakValue.Auto, run: 0, orphans: 2, widows: 2),
            new(10, BreakValue.Auto, BreakValue.Auto, BreakValue.Auto, run: 0, orphans: 2, widows: 2),
        };

        Assert.That(
            ChildSlicer.Fitting(children, 25).Fits,
            Is.EqualTo(2),
            "the run may not move past a break-before/after: avoid, so the line that fits stays and the orphans go short");
    }

    private static List<FragmentChild> Children(Int32 count)
    {
        var children = new List<FragmentChild>(count);

        for (var i = 0; i < count; i++)
        {
            children.Add(new FragmentChild(10, BreakValue.Auto, BreakValue.Auto, BreakValue.Auto));
        }

        return children;
    }
}

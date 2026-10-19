using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Hutu.Tests;

/// <summary>
/// Covers how a block's lines are cut at a fragment's end (CSS Break 3 §3.3): the lines that fit, the
/// orphans kept before the break and the widows kept after it.
/// </summary>
[TestFixture]
public sealed class FragmentSlicerTests
{
    [Test]
    public void TheLinesThatFitAreTaken()
    {
        Assert.That(Fit(5, 25), Is.EqualTo(2), "two ten-pixel lines fit in twenty-five");
    }

    [Test]
    public void EverythingThatFitsIsTaken()
    {
        Assert.That(Fit(5, 100), Is.EqualTo(5), "the whole block fits");
    }

    [Test]
    public void TheWidowsMoveTheBreakEarlier()
    {
        Assert.That(Fit(5, 45, orphans: 2, widows: 2), Is.EqualTo(3), "four would leave a single line after the break, and two are owed");
    }

    [Test]
    public void ABlockThatCannotKeepItsOrphansMovesWhole()
    {
        Assert.That(Fit(3, 25, orphans: 2, widows: 2, startsHere: true), Is.EqualTo(0), "one line before the break is fewer than the orphans");
    }

    [Test]
    public void ABlockShorterThanTheCountsIsKeptTogether()
    {
        Assert.That(Fit(1, 5, orphans: 2, widows: 2, startsHere: true), Is.EqualTo(0), "one line cannot be split, and it does not fit");
        Assert.That(Fit(1, 100, orphans: 2, widows: 2, startsHere: true), Is.EqualTo(1), "it fits whole");
    }

    [Test]
    public void AContinuationTakesALineEvenWhereNoneFits()
    {
        Assert.That(Fit(5, 5, startsHere: false), Is.EqualTo(1), "the flow must advance, so a line taller than the fragment overflows it");
    }

    [Test]
    public void AStartWithNoRoomTakesNothing()
    {
        Assert.That(Fit(5, 5, startsHere: true), Is.EqualTo(0), "the block can begin on the next fragmentainer instead");
    }

    [Test]
    public void ABlockOfNoLinesTakesNoRoom()
    {
        Assert.That(FragmentSlicer.Fitting(Array.Empty<Double>(), 40, 2, 2, true), Is.EqualTo(0));
    }

    private static Int32 Fit(Int32 lines, Double limit, Int32 orphans = 2, Int32 widows = 2, Boolean startsHere = true) =>
        FragmentSlicer.Fitting(Heights(lines), limit, orphans, widows, startsHere);

    private static List<Double> Heights(Int32 count)
    {
        var heights = new List<Double>(count);

        for (var i = 0; i < count; i++)
        {
            heights.Add(10);
        }

        return heights;
    }
}

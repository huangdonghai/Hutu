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
    public void AFragmentFillsEvenWhereTheOrphansCannotAllBeKept()
    {
        // Three lines with room for two and three of each owed: Chrome puts two in the first column and
        // one in the second rather than moving the block whole, and the same holds with larger counts
        // everywhere the room runs out.
        Assert.That(Fit(3, 25, orphans: 2, widows: 2), Is.EqualTo(2));
    }

    [Test]
    public void ALoneLineIsTakenWholeOrOverflows()
    {
        Assert.That(Fit(1, 100, orphans: 2, widows: 2), Is.EqualTo(1), "a block of one line fits whole");
        Assert.That(Fit(1, 5, orphans: 2, widows: 2), Is.EqualTo(1), "and overflows a fragment shorter than itself");
    }

    [Test]
    public void AFragmentWithNoRoomStillTakesALine()
    {
        Assert.That(Fit(5, 5), Is.EqualTo(1), "the flow must advance, so a line taller than the fragment overflows it");
    }

    [Test]
    public void ABlockOfNoLinesTakesNoRoom()
    {
        Assert.That(FragmentSlicer.Fitting(Array.Empty<Double>(), 40, 2, 2), Is.EqualTo(0));
    }

    [Test]
    public void TheOrphansStopTheWidowsMovingTheBreakTooFarBack()
    {
        // Nine ten-pixel lines with a four-line fragment: taking four leaves one line for the widows'
        // three, and moving the break back far enough to leave three would leave two lines in this
        // fragment, fewer than its orphans. The orphans hold and the widows go short, which is how
        // Chrome distributes widows-orphans-018.
        Assert.That(Fit(9, 40, orphans: 3, widows: 3, start: 4), Is.EqualTo(3));
    }

    private static Int32 Fit(
        Int32 lines, Double limit, Int32 orphans = 2, Int32 widows = 2, Int32 start = 0) =>
        FragmentSlicer.Fitting(Heights(lines), limit, orphans, widows, start);

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

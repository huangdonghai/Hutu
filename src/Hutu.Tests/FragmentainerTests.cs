using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Hutu.Tests;

/// <summary>
/// Covers sharing a block's children over the fragments a container gives them, and the breaks the
/// block's edges carry from its children (CSS Break 3 §3.1.1).
/// </summary>
[TestFixture]
public sealed class FragmentainerTests
{
    [Test]
    public void TheFragmentsTakeTheirShareInTurn()
    {
        Assert.That(Fill(3, 25, 25), Is.EqualTo(new[] { 2, 1 }), "two children in the first fragment and the last in the second");
    }

    [Test]
    public void FillingStopsWhenEverythingIsPlaced()
    {
        Assert.That(Fill(2, 100, 100, 100), Is.EqualTo(new[] { 2 }), "no empty fragments after the content");
    }

    [Test]
    public void AForcedBreakStartsTheNextFragment()
    {
        var children = Children(3);
        children[1] = new FragmentChild(10, BreakValue.Page, BreakValue.Auto, BreakValue.Auto);

        Assert.That(Fill(children, 100, 100), Is.EqualTo(new[] { 1, 2 }), "the page break ends the first fragment after one child");
    }

    [Test]
    public void AnAvoidedBoundaryOverflowsTheFragmentRatherThanBreaking()
    {
        var children = Children(3);
        children[0] = new FragmentChild(10, BreakValue.Auto, BreakValue.Avoid, BreakValue.Auto);

        Assert.That(Fill(children, 15, 100), Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void ABalancedFillFindsTheLeastExtentThatHoldsEverything()
    {
        Assert.That(Balance(3, 3), Is.EqualTo(10).Within(0.001), "one child per fragment");
        Assert.That(Balance(4, 3), Is.EqualTo(20).Within(0.001), "two, one and one");
        Assert.That(Balance(5, 2), Is.EqualTo(30).Within(0.001), "three and two");
        Assert.That(Balance(6, 4), Is.EqualTo(20).Within(0.001), "two, two and two leaves the fourth fragment empty");
    }

    [Test]
    public void ABalanceOfOneFragmentIsTheWholeBlock()
    {
        Assert.That(Balance(3, 1), Is.EqualTo(30).Within(0.001));
    }

    [Test]
    public void TheFirstChildsBreakIsTheBlocksOwn()
    {
        var children = Children(2);
        children[0] = new FragmentChild(10, BreakValue.Page, BreakValue.Auto, BreakValue.Auto);

        Assert.That(Fragmentainer.Before(children, BreakValue.Auto), Is.EqualTo(BreakValue.Page), "the page break before the first child stands for the block's");
        Assert.That(Fragmentainer.Before(children, BreakValue.Avoid), Is.EqualTo(BreakValue.Page), "and a forced value wins over the block's avoid");
    }

    [Test]
    public void TheLastChildsAvoidIsTheBlocksOwn()
    {
        var children = Children(2);
        children[^1] = new FragmentChild(10, BreakValue.Auto, BreakValue.Avoid, BreakValue.Auto);

        Assert.That(Fragmentainer.After(children, BreakValue.Auto), Is.EqualTo(BreakValue.Avoid), "the last child's avoid keeps the block with what follows");
    }

    private static Int32[] Fill(Int32 children, params Double[] extents) =>
        Fragmentainer.Fill(Children(children), extents).ToArray();

    private static Int32[] Fill(List<FragmentChild> children, params Double[] extents) =>
        Fragmentainer.Fill(children, extents).ToArray();

    private static Double Balance(Int32 children, Int32 fragments) =>
        Fragmentainer.Balance(Children(children), fragments);

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

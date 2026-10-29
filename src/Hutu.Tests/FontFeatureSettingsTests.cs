using Hutu.Rendering;
using NUnit.Framework;
using System;

namespace Hutu.Tests;

/// <summary>
/// Covers the reading of <c>font-feature-settings</c>: the list of tags and their weights a page asks
/// a face to be set with, resolved to the canonical form the backends key their shaped runs by
/// (CSS Fonts 4 §6.4).
/// </summary>
[TestFixture]
public sealed class FontFeatureSettingsTests
{
    [Test]
    public void NothingAskedForLeavesTheFaceAsItIs()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ComputedStyle.ReadFontFeatures(null), Is.Null);
            Assert.That(ComputedStyle.ReadFontFeatures("  "), Is.Null);
            Assert.That(ComputedStyle.ReadFontFeatures("normal"), Is.Null);
            Assert.That(ComputedStyle.ReadFontFeatures("inherit"), Is.Null);
            Assert.That(ComputedStyle.ReadFontFeatures("initial"), Is.Null);
        });
    }

    [Test]
    public void ATagWrittenWithoutAWeightIsTurnedOn()
    {
        // `[ <integer> | on | off ]?` - an omitted weight is `on`, which is 1.
        Assert.Multiple(() =>
        {
            Assert.That(ComputedStyle.ReadFontFeatures("'halt'"), Is.EqualTo("halt=1"));
            Assert.That(ComputedStyle.ReadFontFeatures("\"vhal\""), Is.EqualTo("vhal=1"));
            Assert.That(ComputedStyle.ReadFontFeatures("'halt' on"), Is.EqualTo("halt=1"));
            Assert.That(ComputedStyle.ReadFontFeatures("'halt' 3"), Is.EqualTo("halt=3"));
            Assert.That(ComputedStyle.ReadFontFeatures("'halt' off"), Is.EqualTo("halt=0"));
            Assert.That(ComputedStyle.ReadFontFeatures("'halt' 0"), Is.EqualTo("halt=0"));
        });
    }

    [Test]
    public void SeveralTagsAreKeptInTheOrderThePageWroteThem()
    {
        // Order is part of the value: the engine hands the list to a shaper as written, and two pages
        // that ask for the same tags in different orders are two different declarations.
        Assert.That(
            ComputedStyle.ReadFontFeatures("'vert' 1, 'halt' 1, 'vhal' 0"),
            Is.EqualTo("vert=1,halt=1,vhal=0"));
    }

    [Test]
    public void ATagNoFeatureCanBeNamedIsDroppedAndTheRestStand()
    {
        // An OpenType tag is at most four characters; `'halting'` names nothing, so the entry says
        // nothing while the entry beside it is still a feature the page asked for.
        Assert.Multiple(() =>
        {
            Assert.That(ComputedStyle.ReadFontFeatures("'halting' 1, 'halt' 1"), Is.EqualTo("halt=1"));
            Assert.That(ComputedStyle.ReadFontFeatures("'halting' 1"), Is.Null);
            Assert.That(ComputedStyle.ReadFontFeatures("'halt' maybe"), Is.Null);
        });
    }

    [Test]
    public void AWeightIsCarriedAsTheNumberTheTagWasGiven()
    {
        Assert.That(ComputedStyle.ReadFontFeatures("'salt' 12"), Is.EqualTo("salt=12"));
    }
}

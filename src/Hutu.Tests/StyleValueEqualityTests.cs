using Hutu.Rendering;
using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Hutu.Tests;

/// <summary>
/// The equality the length types answer with, pinned down to the byte.
/// </summary>
/// <remarks>
/// These types are compared whenever two boxes' areas are decided to be one area, so their equality is
/// the thing a shared style rests on - which is why the boundary cases are tested rather than assumed.
/// Two decisions are recorded here:
/// <list type="bullet">
/// <item><description>a number is equal to another number by its <em>bits</em>, the way these structs
/// compared themselves before they answered the question themselves, so <c>0</c> is not <c>-0</c>;
/// and</description></item>
/// <item><description>a math function is equal to another by the <em>content</em> of its arguments, not
/// by the array holding them, because the same <c>min()</c> written twice is parsed twice.</description>
/// </item>
/// </list>
/// The tests that compare a hash with an equality are the ones that keep the sharing honest: an
/// equality that disagrees with the hash splits two equal values into two buckets, and the table then
/// quietly holds two areas that should have been one.
/// </remarks>
[TestFixture]
public sealed class StyleValueEqualityTests
{
    [Test]
    public void TheTwoSignedZeroesAreNotOneLength()
    {
        var positive = StyleLength.FromPixels(0);
        var negative = StyleLength.FromPixels(-0.0);

        Assert.Multiple(() =>
        {
            // A bare double calls these one number; a length does not, because the bytes are what the
            // engine has always gone by. Keeping them apart is the conservative side: two areas holding
            // them are then never shared, which costs an object and cannot cost a pixel.
            Assert.That(0.0 == -0.0, Is.True, "what the number itself would have said");
            Assert.That(positive.Equals(negative), Is.False, "the length answers by its bits");
            Assert.That(positive == negative, Is.False,
                "and its == is the same answer as its Equals, not a third one");
            Assert.That(positive.GetHashCode(), Is.Not.EqualTo(negative.GetHashCode()),
                "and the hash follows it, so the two never land in one bucket by accident");
        });
    }

    [Test]
    public void TheSameNaNIsTheSameLength()
    {
        var one = StyleLength.FromPixels(Double.NaN);
        var other = StyleLength.FromPixels(Double.NaN);

        Assert.Multiple(() =>
        {
            // The other way round from the zeroes: a bare double calls a NaN unequal to itself, while
            // two quiet NaNs carry the same bits - and it is the bits a value type used to walk. Compared
            // through two locals, because the compiler is right that one NaN written twice is not two
            // numbers being asked about.
            var oneDouble = one.Value;
            var otherDouble = other.Value;

            Assert.That(oneDouble == otherDouble, Is.False, "what the number itself would have said");
            Assert.That(one.Equals(other), Is.True, "two quiet NaNs carry the same bits, so one length");
            Assert.That(one == other, Is.True, "and == says the same thing Equals says");
            Assert.That(one.GetHashCode(), Is.EqualTo(other.GetHashCode()),
                "and the hash has to follow the equality, not the other way");
        });
    }

    [Test]
    public void AFunctionOfTheSameArgumentsIsOneLength()
    {
        var first = StyleLength.Min(new[] { StyleLength.FromPercent(0.5), StyleLength.FromPixels(640) });
        var second = StyleLength.Min(new[] { StyleLength.FromPercent(0.5), StyleLength.FromPixels(640) });

        Assert.Multiple(() =>
        {
            // Two parses of one declaration make two arrays. Comparing those by reference would say the
            // lengths differ, which is not wrong but forfeits the sharing the equality is for.
            Assert.That(first.Equals(second), Is.True, "the arguments decide it, not the array");
            Assert.That(first.GetHashCode(), Is.EqualTo(second.GetHashCode()));
        });
    }

    [Test]
    public void AFunctionOfDifferentArgumentsOrOfAnotherFunctionIsNot()
    {
        var pixels = StyleLength.FromPixels(640);

        var narrow = StyleLength.Min(new[] { StyleLength.FromPercent(0.5), pixels });
        var wider = StyleLength.Min(new[] { StyleLength.FromPercent(0.6), pixels });
        var maximal = StyleLength.Max(new[] { StyleLength.FromPercent(0.5), pixels });
        var clamped = StyleLength.Clamp(new[] { StyleLength.Zero, StyleLength.FromPercent(0.5), pixels });
        var shorter = StyleLength.Min(new[] { StyleLength.FromPercent(0.5) });

        Assert.Multiple(() =>
        {
            Assert.That(narrow.Equals(wider), Is.False, "an argument that differs is a different length");
            Assert.That(narrow.Equals(maximal), Is.False, "min is not max over the same arguments");
            Assert.That(narrow.Equals(clamped), Is.False, "nor is it clamp");
            Assert.That(narrow.Equals(shorter), Is.False, "and two argument lists of different lengths differ");
            Assert.That(clamped.Equals(shorter), Is.False);
        });
    }

    [Test]
    public void AKindIsPartOfTheLength()
    {
        var zero = StyleLength.FromPixels(0);

        Assert.Multiple(() =>
        {
            // The number alone would call these the same thing, and they are not: `auto` and `none` are
            // answers a box model cannot resolve the way it resolves a number.
            Assert.That(zero.Equals(StyleLength.Zero), Is.True, "the same number and kind");
            Assert.That(zero.Equals(StyleLength.Auto), Is.False, "auto is not zero");
            Assert.That(zero.Equals(StyleLength.None), Is.False, "none is not zero");
            Assert.That(StyleLength.Auto.Equals(StyleLength.None), Is.False, "nor is either the other");
            Assert.That(zero.Equals(StyleLength.FromPercent(0)), Is.False, "a zero share is not a zero width");
        });
    }

    [Test]
    public void TheThicknessAndTheCornersAndTheStrokesCompareByWhatTheyHold()
    {
        var half = StyleLength.FromPercent(0.5);
        var pixels = StyleLength.FromPixels(4);

        var one = new StyleThickness(half, pixels, half, pixels);
        var other = new StyleThickness(half, pixels, half, pixels);
        var turned = new StyleThickness(half, half, pixels, pixels);

        var corner = new StyleCornerRadius(half, pixels);
        var sameCorner = new StyleCornerRadius(half, pixels);
        var ellipse = new StyleCornerRadius(half, half);

        var radii = new StyleCornerRadii(corner, corner, corner, corner);
        var otherRadii = new StyleCornerRadii(sameCorner, sameCorner, sameCorner, sameCorner);
        var oneRound = new StyleCornerRadii(ellipse, StyleCornerRadius.Zero, StyleCornerRadius.Zero, StyleCornerRadius.Zero);

        var strokes = new BorderStyles(BorderLineStyle.Solid, BorderLineStyle.Dashed, BorderLineStyle.Solid, BorderLineStyle.None);
        var sameStrokes = new BorderStyles(BorderLineStyle.Solid, BorderLineStyle.Dashed, BorderLineStyle.Solid, BorderLineStyle.None);
        var squareDotted = new BorderStyles(BorderLineStyle.Dotted, BorderLineStyle.Dotted, BorderLineStyle.Dotted, BorderLineStyle.Dotted);

        Assert.Multiple(() =>
        {
            Assert.That(one.Equals(other), Is.True, "the same four sides are the same insets");
            Assert.That(one.Equals(turned), Is.False, "which side carries which length is part of it");
            Assert.That(one.GetHashCode(), Is.EqualTo(other.GetHashCode()));

            Assert.That(radii.Equals(otherRadii), Is.True);
            Assert.That(radii.Equals(oneRound), Is.False, "an ellipse is not a circle");
            Assert.That(radii.GetHashCode(), Is.EqualTo(otherRadii.GetHashCode()));

            Assert.That(strokes.Equals(sameStrokes), Is.True);
            Assert.That(strokes.Equals(squareDotted), Is.False, "a uniform border is not a mixed one");
            Assert.That(strokes.GetHashCode(), Is.EqualTo(sameStrokes.GetHashCode()));
        });
    }

    [Test]
    public void TheEngineAsksTheseTypesForThemselvesRatherThanBoxingThem()
    {
        // A value type that does not answer IEquatable<T> is compared by the fallback comparer, which
        // boxes both sides of every comparison and one side of every hash - the cost these four were
        // written to stop paying. The interface is the whole of that fix, so it is what gets pinned.
        Assert.Multiple(() =>
        {
            Assert.That(typeof(IEquatable<StyleLength>).IsAssignableFrom(typeof(StyleLength)), Is.True,
                "StyleLength has to answer for itself");
            Assert.That(typeof(IEquatable<StyleThickness>).IsAssignableFrom(typeof(StyleThickness)), Is.True);
            Assert.That(typeof(IEquatable<BorderStyles>).IsAssignableFrom(typeof(BorderStyles)), Is.True);
            Assert.That(typeof(IEquatable<StyleCornerRadii>).IsAssignableFrom(typeof(StyleCornerRadii)), Is.True);
            Assert.That(typeof(IEquatable<StyleCornerRadius>).IsAssignableFrom(typeof(StyleCornerRadius)), Is.True);
        });
    }

    [Test]
    public void ATableKeepsOneEntryForTwoEqualValues()
    {
        // The contract the sharing depends on, said the way a table enforces it: values that agree have
        // to land in one bucket, or two areas that should have been one are kept side by side forever.
        var table = new Dictionary<StyleLength, String>();

        table[StyleLength.Min(new[] { StyleLength.FromPercent(0.5), StyleLength.FromPixels(640) })] = "first";
        table[StyleLength.Min(new[] { StyleLength.FromPercent(0.5), StyleLength.FromPixels(640) })] = "second";

        Assert.That(table, Has.Count.EqualTo(1), "one entry for two equal keys");
        Assert.That(table[StyleLength.Min(new[] { StyleLength.FromPercent(0.5), StyleLength.FromPixels(640) })],
            Is.EqualTo("second"), "and the second write overwrote the first rather than joining it");
    }
}

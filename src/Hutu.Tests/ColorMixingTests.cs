using Hutu.Rendering;
using NUnit.Framework;

namespace Hutu.Tests;

/// <summary>
/// The colour a ramp holds halfway between its ends, when the page names the space the two are mixed in.
/// </summary>
/// <remarks>
/// Every number here is the result the reference conversion of CSS Color 4 §19 gives for the pair, taken
/// to the fraction named and then back to the eight bits a screen shows. A mix in sRGB - what the painter
/// does when nothing is said - is the same pair taken in the space the colours were already written in,
/// so a test that only asked for the ends would pass without the space being read at all. The middle is
/// where the spaces disagree, and by a lot: red to blue is a purple in Oklab and a magenta in HSL.
/// </remarks>
[TestFixture]
public sealed class ColorMixingTests
{
    private const UInt32 Red = 0xFFFF0000;
    private const UInt32 Green = 0xFF00FF00;
    private const UInt32 Blue = 0xFF0000FF;
    private const UInt32 Yellow = 0xFFFFFF00;
    private const UInt32 Grey = 0xFF7F7F7F;
    private const UInt32 White = 0xFFFFFFFF;
    private const UInt32 Black = 0xFF000000;
    private const UInt32 Transparent = 0x00000000;

    [Test]
    public void AMixInThePaintersOwnSpaceStandsBetweenTheTwoChannels([Values(0.0, 0.25, 0.5, 1.0)] Double at)
    {
        var mixed = ColorMixing.Mix(Red, Blue, at, ColorSpace.Srgb, HueMethod.Shorter);

        Assert.That(mixed, Is.EqualTo(Blend(Red, Blue, at)), "a named sRGB space asks for what the painter does anyway");
    }

    [Test]
    public void AMixInOklabTakesTheMiddleThePerceptualSpaceFinds([Values(0.25, 0.5)] Double at)
    {
        var expected = at == 0.25 ? 0xFFC6496D : 0xFF8C53A2;

        Assert.That(ColorMixing.Mix(Red, Blue, at, ColorSpace.Oklab, HueMethod.Shorter), Is.EqualTo(expected));
    }

    [Test]
    public void AMixInLabKeepsTheLightnessTheEyeWeighs()
    {
        Assert.That(ColorMixing.Mix(Red, Blue, 0.5, ColorSpace.Lab, HueMethod.Shorter), Is.EqualTo(0xFFC10088));
        Assert.That(ColorMixing.Mix(Red, Green, 0.5, ColorSpace.Lab, HueMethod.Shorter), Is.EqualTo(0xFFC8AC00));
    }

    [Test]
    public void AMixInLchCarriesTheChromaRoundRatherThanThroughIt()
    {
        Assert.That(ColorMixing.Mix(Red, Blue, 0.5, ColorSpace.Lch, HueMethod.Shorter), Is.EqualTo(0xFFF50086));
        Assert.That(ColorMixing.Mix(Red, Green, 0.5, ColorSpace.Lch, HueMethod.Shorter), Is.EqualTo(0xFFD1A900));
    }

    [Test]
    public void AMixInOklchKeepsTheChromaOfTheArcItTakes()
    {
        Assert.That(ColorMixing.Mix(Red, Blue, 0.5, ColorSpace.Oklch, HueMethod.Shorter), Is.EqualTo(0xFFBA00C2));
        Assert.That(ColorMixing.Mix(Red, Green, 0.5, ColorSpace.Oklch, HueMethod.Shorter), Is.EqualTo(0xFFF99500));
    }

    [Test]
    public void AMixInLightDoublesTheLightTheTwoAdd()
    {
        Assert.That(ColorMixing.Mix(Red, Blue, 0.5, ColorSpace.SrgbLinear, HueMethod.Shorter), Is.EqualTo(0xFFBC00BC));
        Assert.That(ColorMixing.Mix(White, Black, 0.5, ColorSpace.SrgbLinear, HueMethod.Shorter), Is.EqualTo(0xFFBCBCBC));
    }

    [Test]
    public void AMixInWideGamutRedReachesTheColourThatSpaceHolds()
    {
        Assert.That(ColorMixing.Mix(Red, Blue, 0.5, ColorSpace.DisplayP3, HueMethod.Shorter), Is.EqualTo(0xFF800A91));
        Assert.That(ColorMixing.Mix(Red, Green, 0.5, ColorSpace.DisplayP3, HueMethod.Shorter), Is.EqualTo(0xFFB4961A));
    }

    [Test]
    public void AMixInHueSaturationLightnessTurnsThroughTheHuesItIsGiven()
    {
        Assert.That(ColorMixing.Mix(Red, Blue, 0.5, ColorSpace.Hsl, HueMethod.Shorter), Is.EqualTo(0xFFFF00FF));
        Assert.That(ColorMixing.Mix(Red, Blue, 0.25, ColorSpace.Hsl, HueMethod.Shorter), Is.EqualTo(0xFFFF0080));
    }

    [Test]
    public void ALongerHueTakesTheArcRoundTheOtherWay()
    {
        Assert.That(ColorMixing.Mix(Red, Blue, 0.5, ColorSpace.Hsl, HueMethod.Longer), Is.EqualTo(0xFF00FF00),
            "the longer arc of the colour wheel goes the way the third of its primaries lies");
        Assert.That(ColorMixing.Mix(Red, Blue, 0.5, ColorSpace.Oklch, HueMethod.Longer), Is.EqualTo(0xFF009300));
    }

    [Test]
    public void AnIncreasingHueClimbsFromTheFirstAngleToTheSecond()
    {
        // 0 degrees rising to 240 is the same arc the longer method takes, and falling from 0 to 240 is
        // the same arc the shorter one takes.
        Assert.That(ColorMixing.Mix(Red, Blue, 0.5, ColorSpace.Hsl, HueMethod.Increasing), Is.EqualTo(0xFF00FF00));
        Assert.That(ColorMixing.Mix(Red, Blue, 0.5, ColorSpace.Oklch, HueMethod.Increasing), Is.EqualTo(0xFF009300));
    }

    [Test]
    public void ADecreasingHueFallsFromTheFirstAngleToTheSecond()
    {
        Assert.That(ColorMixing.Mix(Red, Blue, 0.5, ColorSpace.Hsl, HueMethod.Decreasing), Is.EqualTo(0xFFFF00FF));
        Assert.That(ColorMixing.Mix(Red, Blue, 0.5, ColorSpace.Oklch, HueMethod.Decreasing), Is.EqualTo(0xFFBA00C2));
    }

    [Test]
    public void AColourWithNoChromaHasNoHueAndTakesTheOneBesideIt()
    {
        // Grey is the one colour of the ramp whose hue is powerless, so the arc is red's own hue kept
        // all the way along rather than a turn towards nothing.
        Assert.That(ColorMixing.Mix(Grey, Red, 0.5, ColorSpace.Oklch, HueMethod.Shorter), Is.EqualTo(0xFFC56255));
        Assert.That(ColorMixing.Mix(Red, Grey, 0.5, ColorSpace.Oklch, HueMethod.Shorter), Is.EqualTo(0xFFC56255),
            "and which end the grey stands on makes no difference");
    }

    [Test]
    public void ATranslucentEndIsWeighedByHowMuchLightItCarries()
    {
        // Premultiplied (CSS Color 4 §13.4): nothing added no light, so halfway between a colour and
        // nothing is the colour itself, at half the alpha - not a darkened version of it.
        Assert.That(ColorMixing.Mix(Red, Transparent, 0.5, ColorSpace.Oklab, HueMethod.Shorter), Is.EqualTo(0x80FF0000));
        Assert.That(ColorMixing.Mix(Red, Transparent, 0.5, ColorSpace.Srgb, HueMethod.Shorter), Is.EqualTo(0x80FF0000));
    }

    [Test]
    public void AnEndOfTheRampIsTheColourItStandsOn()
    {
        // Every space the head can name has to bring its two ends back to the colours they were written
        // as, or the conversion that carries a ramp through it has lost something on the way.
        foreach (var space in (ColorSpace[])Enum.GetValues(typeof(ColorSpace)))
        {
            Assert.That(ColorMixing.Mix(Red, Blue, 0, space, HueMethod.Shorter), Is.EqualTo(Red), $"the start in {space}");
            Assert.That(ColorMixing.Mix(Red, Blue, 1, space, HueMethod.Shorter), Is.EqualTo(Blue), $"the end in {space}");
        }
    }

    /// <summary>The mix the painter makes when nothing names a space: each byte taken between on its own.</summary>
    private static UInt32 Blend(UInt32 from, UInt32 to, Double at)
    {
        Byte Channel(Int32 shift)
        {
            var start = (Double)((from >> shift) & 0xFF);
            var end = (Double)((to >> shift) & 0xFF);

            return (Byte)Math.Round(start + (end - start) * at, MidpointRounding.AwayFromZero);
        }

        return (UInt32)((Channel(24) << 24) | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0));
    }
}

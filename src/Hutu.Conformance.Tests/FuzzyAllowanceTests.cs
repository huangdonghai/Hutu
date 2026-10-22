using SkiaSharp;
using NUnit.Framework;

namespace Hutu.Conformance;

/// <summary>
/// The allowance a reftest states, and the rule the reftest suite of the web platform tests applies to
/// it.
/// </summary>
[TestFixture]
public sealed class FuzzyAllowanceTests
{
    [TestCase(@"<meta name=""fuzzy"" content=""maxDifference=15;totalPixels=300"">", 15, 15, 300, 300)]
    [TestCase(@"<meta name=""fuzzy"" content=""15;300"">", 15, 15, 300, 300)]
    [TestCase(@"<meta name=""fuzzy"" content=""totalPixels=300;maxDifference=15"">", 15, 15, 300, 300)]
    [TestCase(@"<meta name=""fuzzy"" content=""maxDifference=10-15;totalPixels=200-300"">", 10, 15, 200, 300)]
    [TestCase(@"<meta name=""fuzzy"" content=""10-15;200-300"">", 10, 15, 200, 300)]
    [TestCase(@"<meta name=""fuzzy"" content=""maxDifference=0-1; totalPixels=0-10"">", 0, 1, 0, 10)]
    [TestCase(@"<meta name=""fuzzy"" content=""maxDifference=4;totalPixels=0-6"">", 4, 4, 0, 6)]
    public void ReadsTheAllowanceAPageStates(String meta, Int32 channelLow, Int32 channelHigh, Int32 pixelLow, Int32 pixelHigh)
    {
        var allowance = FuzzyAllowance.Of($"<html><head>{meta}</head><body></body></html>");

        Assert.That(allowance, Is.Not.Null);
        Assert.That(allowance!.Channel, Is.EqualTo((channelLow, channelHigh)));
        Assert.That(allowance.Pixels, Is.EqualTo((pixelLow, pixelHigh)));
    }

    [TestCase("")]
    [TestCase("<html><head></head><body></body></html>")]
    [TestCase(@"<meta name=""assert"" content=""something"">")]
    [TestCase(@"<meta name=""fuzzy"" content=""maxDifference=15"">")]
    [TestCase(@"<meta name=""fuzzy"" content=""15"">")]
    [TestCase(@"<meta name=""fuzzy"" content=""maxDifference=a;totalPixels=b"">")]
    [TestCase(@"<meta name=""fuzzy"" content=""maxDifference=15;totalPixels=300;extra=1"">")]
    public void APageThatStatesNoAllowanceHasNone(String html)
    {
        Assert.That(FuzzyAllowance.Of(html), Is.Null);
    }

    [Test]
    public void ADifferenceInsideBothBandsIsTheSamePicture()
    {
        var allowance = FuzzyAllowance.Of(@"<meta name=""fuzzy"" content=""maxDifference=0-5;totalPixels=0-100"">")!;

        Assert.Multiple(() =>
        {
            Assert.That(allowance.Allows(differing: 12, widest: 3), Is.True);
            Assert.That(allowance.Allows(differing: 0, widest: 0), Is.True);
            Assert.That(allowance.Allows(differing: 100, widest: 5), Is.True);
        });
    }

    [Test]
    public void ADifferenceOutsideEitherBandIsNot()
    {
        var allowance = FuzzyAllowance.Of(@"<meta name=""fuzzy"" content=""maxDifference=0-5;totalPixels=0-100"">")!;

        Assert.Multiple(() =>
        {
            // More pixels than the page allows.
            Assert.That(allowance.Allows(differing: 101, widest: 1), Is.False);
            // A channel further apart than the page allows.
            Assert.That(allowance.Allows(differing: 10, widest: 6), Is.False);
            // Fewer pixels than the band starts at: the band is what the difference has to be, not a
            // ceiling it may be under.
            Assert.That(allowance.Allows(differing: 0, widest: 0), Is.True);
        });
    }

    [Test]
    public void APictureThatAgreesIsSatisfiedWhateverTheBandsSay()
    {
        // The reftest suite of the web platform tests lets a picture that agrees outright satisfy a pair
        // whose channel band starts at zero, even when its pixel band starts above zero: the band is
        // what the difference has to be, and there is no difference to have.
        var allowance = FuzzyAllowance.Of(@"<meta name=""fuzzy"" content=""maxDifference=0-1;totalPixels=5-10"">")!;

        Assert.That(allowance.Allows(differing: 0, widest: 0), Is.True);
    }

    [Test]
    public void APixelBeyondTheBandIsNotForgiven()
    {
        var allowance = FuzzyAllowance.Of(@"<meta name=""fuzzy"" content=""maxDifference=0-1;totalPixels=0-0"">")!;

        Assert.Multiple(() =>
        {
            Assert.That(allowance.Allows(differing: 0, widest: 0), Is.True);
            Assert.That(allowance.Allows(differing: 1, widest: 3), Is.False);
        });
    }

    [Test]
    public void TheComparisonTakesTheAllowanceThePairStates()
    {
        using var test = new SKBitmap(4, 4);
        using var reference = new SKBitmap(4, 4);

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                test.SetPixel(x, y, SKColors.White);
                reference.SetPixel(x, y, SKColors.White);
            }
        }

        test.SetPixel(1, 1, new SKColor(250, 250, 250));

        var allowance = FuzzyAllowance.Of(@"<meta name=""fuzzy"" content=""maxDifference=0-5;totalPixels=0-10"">")!;

        Assert.Multiple(() =>
        {
            var forgiven = ReftestRunner.Compare(test, reference, "fuzzy", writeDiff: false, allowance: allowance);
            var exact = ReftestRunner.Compare(test, reference, "exact", writeDiff: false);

            Assert.That(forgiven.Matched, Is.True, "a difference the pair allows is not a difference it failed on");
            Assert.That(exact.Matched, Is.False, "a pair that states no allowance has to match outright");
            Assert.That(exact.Differing, Is.EqualTo(1));
            Assert.That(exact.Widest, Is.EqualTo(5));
        });
    }
}

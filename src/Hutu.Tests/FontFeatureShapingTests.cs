using Hutu.Rendering.Layout;
using Hutu.Skia;
using NUnit.Framework;
using SkiaSharp;
using System;
using System.IO;

namespace Hutu.Tests;

/// <summary>
/// Covers what a face does when the page asks for an OpenType feature: the glyphs it substitutes, and
/// therefore the width the line is laid out to (CSS Fonts 4 §6.4).
/// </summary>
/// <remarks>
/// The face measured here is the subset the borrowed text-spacing-trim corpus brings: its <c>halt</c>
/// feature sets the full-width punctuation in half-width forms, which is the whole reason that corpus's
/// references can show a trimmed line without asking the property under test.
/// </remarks>
[TestFixture]
public sealed class FontFeatureShapingTests
{
    [Test]
    public void TheHalfWidthFeatureNarrowsThePunctuationItAppliesTo()
    {
        using var measurer = new SkiaTextMeasurer();
        using var typeface = SKTypeface.FromFile(SubsetFont());

        if (typeface is null)
        {
            Assert.Ignore("the subset face the corpus brings could not be opened");
            return;
        }

        measurer.Fonts.Register("halt-font", typeface);

        var plain = measurer.MeasureWidth("国（国", new FontStyle("halt-font", 20));
        var halted = measurer.MeasureWidth("国（国", new FontStyle("halt-font", 20, features: "halt=1"));

        Assert.Multiple(() =>
        {
            Assert.That(plain, Is.EqualTo(60).Within(0.5), "three full-width characters");
            Assert.That(halted, Is.EqualTo(50).Within(0.5), "the punctuation takes half a em, and the two ideographs do not");
        });
    }

    [Test]
    public void APunctuationRunOnItsOwnTakesTheFeatureToo()
    {
        // The layout splits a line at every font change, so the run that carries the feature is often
        // the punctuation alone - which is exactly the shape a reftest reference writes.
        using var measurer = new SkiaTextMeasurer();
        using var typeface = SKTypeface.FromFile(SubsetFont());

        if (typeface is null)
        {
            Assert.Ignore("the subset face the corpus brings could not be opened");
            return;
        }

        measurer.Fonts.Register("halt-font", typeface);

        Assert.That(
            measurer.MeasureWidth("（", new FontStyle("halt-font", 20, features: "halt=1")),
            Is.EqualTo(10).Within(0.5),
            "half a em for a run that is the punctuation by itself");
    }

    [Test]
    public void AFeatureOfTheSameTagTurnedOffLeavesTheWidthAlone()
    {
        using var measurer = new SkiaTextMeasurer();
        using var typeface = SKTypeface.FromFile(SubsetFont());

        if (typeface is null)
        {
            Assert.Ignore("the subset face the corpus brings could not be opened");
            return;
        }

        measurer.Fonts.Register("halt-font", typeface);

        Assert.That(
            measurer.MeasureWidth("国（国", new FontStyle("halt-font", 20, features: "halt=0")),
            Is.EqualTo(60).Within(0.5),
            "a feature written off is the face as it came");
    }

    /// <summary>
    /// A face the engine loaded itself - which is how every <c>@font-face</c> arrives, from a stream
    /// over the bytes it fetched - is one the shaper can still ask for a feature.
    /// </summary>
    [Test]
    public void AFaceOpenedFromAStreamIsShapedWithItsFeatures()
    {
        using var measurer = new SkiaTextMeasurer();
        using var typeface = SKTypeface.FromStream(new MemoryStream(File.ReadAllBytes(SubsetFont())));

        if (typeface is null)
        {
            Assert.Ignore("the subset face the corpus brings could not be opened");
            return;
        }

        measurer.Fonts.Register("halt-font", typeface);

        Assert.That(
            measurer.MeasureWidth("国（国", new FontStyle("halt-font", 20, features: "halt=1")),
            Is.EqualTo(50).Within(0.5),
            "half a em for the punctuation, whatever the face was opened from");
    }

    /// <summary>The checked-in subset, found from wherever the tests are run.</summary>
    private static String SubsetFont()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? AppContext.BaseDirectory,
            "conformance",
            "fonts",
            "noto",
            "cjk",
            "NotoSansCJKjp-Regular-subset-halt.otf");
    }
}

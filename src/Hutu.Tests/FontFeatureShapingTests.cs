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
    public void EveryGlyphOfAShapedRunKeepsItsPlace()
    {
        // A run shaped with features is built from the library's own positions, which are offsets from
        // the pen rather than the places the pen walked to. Read as the latter, every glyph of a run
        // lands on the first - which is what two half-width brackets drawn on top of each other look
        // like: one bracket, in the room of two.
        using var measurer = new SkiaTextMeasurer();
        using var typeface = SKTypeface.FromFile(SubsetFont());

        if (typeface is null)
        {
            Assert.Ignore("the subset face the corpus brings could not be opened");
            return;
        }

        measurer.Fonts.Register("halt-font", typeface);

        // The features the corpus's references ask for, both of them: a face is free to answer `halt`
        // alone with glyphs it offsets, and the pair a reference writes asks for the vertical half
        // width beside it as well.
        var run = measurer.Decompose("（（", new FontStyle("halt-font", 20, features: "halt=1,vhal=1")).Single();

        Assert.Multiple(() =>
        {
            // Where the first glyph stands is the face's own business - a half-width bracket carries an
            // offset the pen is moved by - but the step from one to the next is the advance, and the two
            // of them share out the run rather than sitting on top of each other.
            Assert.That(
                run.Shaped.Points[1].X - run.Shaped.Points[0].X,
                Is.EqualTo(10).Within(0.5),
                "the second glyph stands a half em past the first");
            Assert.That(run.Advance, Is.EqualTo(20).Within(0.5), "and the run is the two of them together");
        });
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

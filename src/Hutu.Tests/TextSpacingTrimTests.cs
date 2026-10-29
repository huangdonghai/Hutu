using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Hutu.Tests;

/// <summary>
/// Covers the rules of <c>text-spacing-trim</c>: the value grammar, the punctuation classes, the
/// language conventions, and the three positions - start, between neighbors, end - at which a
/// punctuation is set half-width (CSS Text 4 §8.5).
/// </summary>
[TestFixture]
public sealed class TextSpacingTrimTests
{
    private static readonly TextSpacingTrimValue Inherited =
        Value(SpacingTrimStart.Trim, adjacent: false, SpacingTrimEnd.Trim);

    [Test]
    public void TheInitialValueIsNormal()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TextSpacingTrimValue.Initial.Start, Is.EqualTo(SpacingTrimStart.Space));
            Assert.That(TextSpacingTrimValue.Initial.Adjacent, Is.True);
            Assert.That(TextSpacingTrimValue.Initial.End, Is.EqualTo(SpacingTrimEnd.Allow));
            Assert.That(TextSpacingTrimValue.Initial.All, Is.False);
        });
    }

    [Test]
    public void EachKeywordExpandsAsTheSpecificationSays()
    {
        var cases = new (String Declared, TextSpacingTrimValue Expected)[]
        {
            ("normal", Value(SpacingTrimStart.Space, true, SpacingTrimEnd.Allow)),
            ("space-all", Value(SpacingTrimStart.Space, false, SpacingTrimEnd.Space)),
            ("trim-start", Value(SpacingTrimStart.Trim, true, SpacingTrimEnd.Allow)),
            ("space-first", Value(SpacingTrimStart.SpaceFirst, true, SpacingTrimEnd.Allow)),
            ("trim-all", Value(SpacingTrimStart.Trim, true, SpacingTrimEnd.Trim, all: true)),
            ("trim-both", Value(SpacingTrimStart.Trim, true, SpacingTrimEnd.Trim)),
            ("auto", TextSpacingTrimValue.Initial),
        };

        Assert.Multiple(() =>
        {
            foreach (var (declared, expected) in cases)
            {
                Assert.That(Parse(declared), Is.EqualTo(expected), declared);
            }
        });
    }

    [Test]
    public void TheLonghandGroupsTakeTheOmittedDefaults()
    {
        var cases = new (String Declared, TextSpacingTrimValue Expected)[]
        {
            ("trim-end", Value(SpacingTrimStart.Trim, true, SpacingTrimEnd.Trim)),
            ("space-end", Value(SpacingTrimStart.Trim, true, SpacingTrimEnd.Space)),
            ("space-start", Value(SpacingTrimStart.Space, true, SpacingTrimEnd.Allow)),
            ("space-adjacent", Value(SpacingTrimStart.Trim, false, SpacingTrimEnd.Allow)),
            ("allow-end", Value(SpacingTrimStart.Trim, true, SpacingTrimEnd.Allow)),
            ("trim-end trim-start", Value(SpacingTrimStart.Trim, true, SpacingTrimEnd.Trim)),
            ("space-first trim-end", Value(SpacingTrimStart.SpaceFirst, true, SpacingTrimEnd.Trim)),
            ("space-start trim-adjacent space-end", Value(SpacingTrimStart.Space, true, SpacingTrimEnd.Space)),
            ("trim-start space-adjacent trim-end", Value(SpacingTrimStart.Trim, false, SpacingTrimEnd.Trim)),
            ("allow-end trim-adjacent trim-start", Value(SpacingTrimStart.Trim, true, SpacingTrimEnd.Allow)),
        };

        Assert.Multiple(() =>
        {
            foreach (var (declared, expected) in cases)
            {
                Assert.That(Parse(declared), Is.EqualTo(expected), declared);
            }
        });
    }

    [Test]
    public void StandaloneKeywordsAndDuplicatePositionsAreRejected()
    {
        var cases = new[]
        {
            "normal trim-all",
            "space-all space-adjacent",
            "trim-all trim-end",
            "auto trim-start",
            "trim-both trim-end",
            "trim-start space-start",
            "space-first space-start",
            "trim-adjacent space-adjacent",
            "trim-end allow-end",
            "trim-start allow-end trim-start",
        };

        Assert.Multiple(() =>
        {
            foreach (var declared in cases)
            {
                var parsed = TextSpacingTrimValue.TryParse(declared, Inherited, out var value);

                Assert.That(parsed, Is.False, declared);
                Assert.That(value, Is.EqualTo(Inherited), declared);
            }
        });
    }

    [Test]
    public void AnUnknownOrEmptyValueIsRejected()
    {
        var cases = new[] { "", " ", "foo", "trim", "trim-start;", "inherit trim-start" };

        Assert.Multiple(() =>
        {
            foreach (var declared in cases)
            {
                var parsed = TextSpacingTrimValue.TryParse(declared, Inherited, out var value);

                Assert.That(parsed, Is.False, declared);
                Assert.That(value, Is.EqualTo(Inherited), declared);
            }
        });
    }

    [Test]
    public void CssWideKeywordsResolveAsThePropertyRequires()
    {
        Assert.Multiple(() =>
        {
            foreach (var declared in new[] { "inherit", "unset", "revert", "revert-layer", "INHERIT" })
            {
                Assert.That(Parse(declared), Is.EqualTo(Inherited), declared);
            }

            Assert.That(Parse("initial"), Is.EqualTo(TextSpacingTrimValue.Initial));
            Assert.That(Parse("INITIAL"), Is.EqualTo(TextSpacingTrimValue.Initial));
        });
    }

    [Test]
    public void ATagIsReadWithoutRegardToCaseOrSurroundingSpace()
    {
        Assert.That(
            Parse("  TRIM-START   trim-END "),
            Is.EqualTo(Value(SpacingTrimStart.Trim, true, SpacingTrimEnd.Trim)));
    }

    [Test]
    public void TheClassesCoverFullwidthNarrowAndQuotePunctuation()
    {
        var cases = new (Int32 CodePoint, PunctuationClass Expected)[]
        {
            (0xFF08, PunctuationClass.Opening),   // （
            (0xFF09, PunctuationClass.Closing),   // ）
            (0x300C, PunctuationClass.Opening),   // 「
            (0x300D, PunctuationClass.Closing),   // 」
            (0x3010, PunctuationClass.Opening),   // 【
            (0x3011, PunctuationClass.Closing),   // 】
            (0x2018, PunctuationClass.Opening),   // ‘
            (0x2019, PunctuationClass.Closing),   // ’
            (0x201C, PunctuationClass.Opening),   // “
            (0x201D, PunctuationClass.Closing),   // ”
            (0x30FB, PunctuationClass.Middle),    // ・
            (0x00B7, PunctuationClass.Middle),    // ·
            (0x2027, PunctuationClass.Middle),    // ‧
            (0x0028, PunctuationClass.Opening),   // (
            (0x0029, PunctuationClass.Closing),   // )
            (0x005B, PunctuationClass.Opening),   // [
            (0x005D, PunctuationClass.Closing),   // ]
            (0x56FD, PunctuationClass.None),      // 国
            (0x0041, PunctuationClass.None),      // A
            (0x0020, PunctuationClass.None),      // space
            (0x3000, PunctuationClass.None),      // ideographic space
            (0xFF1F, PunctuationClass.None),      // ？
            (0xFF01, PunctuationClass.None),      // ！
        };

        Assert.Multiple(() =>
        {
            foreach (var (codePoint, expected) in cases)
            {
                Assert.That(
                    TextSpacingTrim.Classify(codePoint, TrimLanguage.Other),
                    Is.EqualTo(expected),
                    $"U+{codePoint:X4}");
            }
        });
    }

    [Test]
    public void ColonAndDotFollowTheLanguagesConventions()
    {
        var dots = new[] { 0x3001, 0x3002, 0xFF0C, 0xFF0E };
        var colons = new[] { 0xFF1A, 0xFF1B };

        var dotClasses = new (TrimLanguage Language, PunctuationClass Expected)[]
        {
            (TrimLanguage.SimplifiedChinese, PunctuationClass.Closing),
            (TrimLanguage.TraditionalChinese, PunctuationClass.Middle),
            (TrimLanguage.Japanese, PunctuationClass.Closing),
            (TrimLanguage.Korean, PunctuationClass.Closing),
            (TrimLanguage.Other, PunctuationClass.Closing),
        };

        var colonClasses = new (TrimLanguage Language, PunctuationClass Expected)[]
        {
            (TrimLanguage.SimplifiedChinese, PunctuationClass.Closing),
            (TrimLanguage.TraditionalChinese, PunctuationClass.Middle),
            (TrimLanguage.Japanese, PunctuationClass.Middle),
            (TrimLanguage.Korean, PunctuationClass.Middle),
            (TrimLanguage.Other, PunctuationClass.Middle),
        };

        Assert.Multiple(() =>
        {
            foreach (var (language, expected) in dotClasses)
            {
                foreach (var codePoint in dots)
                {
                    Assert.That(
                        TextSpacingTrim.Classify(codePoint, language),
                        Is.EqualTo(expected),
                        $"U+{codePoint:X4} {language}");
                }
            }

            foreach (var (language, expected) in colonClasses)
            {
                foreach (var codePoint in colons)
                {
                    Assert.That(
                        TextSpacingTrim.Classify(codePoint, language),
                        Is.EqualTo(expected),
                        $"U+{codePoint:X4} {language}");
                }
            }
        });
    }

    [Test]
    public void LanguageTagsResolveToTheConventionsThatPlacePunctuation()
    {
        var cases = new (String? Tag, TrimLanguage Expected)[]
        {
            (null, TrimLanguage.Other),
            ("", TrimLanguage.Other),
            (" ", TrimLanguage.Other),
            ("ja", TrimLanguage.Japanese),
            ("JA", TrimLanguage.Japanese),
            ("ja-JP", TrimLanguage.Japanese),
            ("ko", TrimLanguage.Korean),
            ("ko-KR", TrimLanguage.Korean),
            ("zh", TrimLanguage.SimplifiedChinese),
            ("zh-CN", TrimLanguage.SimplifiedChinese),
            ("zh-SG", TrimLanguage.SimplifiedChinese),
            ("zh-Hans", TrimLanguage.SimplifiedChinese),
            ("zh-Hans-CN", TrimLanguage.SimplifiedChinese),
            ("zh-TW", TrimLanguage.TraditionalChinese),
            ("zh-HK", TrimLanguage.TraditionalChinese),
            ("zh-MO", TrimLanguage.TraditionalChinese),
            ("zh-Hant", TrimLanguage.TraditionalChinese),
            ("zh-Hant-TW", TrimLanguage.TraditionalChinese),
            ("zh-Hans-TW", TrimLanguage.SimplifiedChinese),
            ("zh-Hant-CN", TrimLanguage.TraditionalChinese),
            ("en-US", TrimLanguage.Other),
            (" fr ", TrimLanguage.Other),
        };

        Assert.Multiple(() =>
        {
            foreach (var (tag, expected) in cases)
            {
                Assert.That(TextSpacingTrim.LanguageOf(tag), Is.EqualTo(expected), tag ?? "null");
            }
        });
    }

    [Test]
    public void OnlyAGlyphStillAtTheFullwidthAdvanceIsTrimmed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TextSpacingTrim.IsFullwidth(20, 20), Is.True);
            Assert.That(TextSpacingTrim.IsFullwidth(19.9, 20), Is.True);
            Assert.That(TextSpacingTrim.IsFullwidth(19.5, 20), Is.False, "a proportional glyph is already narrower");
            Assert.That(TextSpacingTrim.IsFullwidth(10, 20), Is.False);
            Assert.That(TextSpacingTrim.IsFullwidth(5.56, 20), Is.False, "a narrow quote");
            Assert.That(TextSpacingTrim.IsFullwidth(0, 20), Is.False);
            Assert.That(TextSpacingTrim.IsFullwidth(10, 10), Is.True);
            Assert.That(TextSpacingTrim.IsFullwidth(5, 10), Is.False);
            Assert.That(TextSpacingTrim.IsFullwidth(20, 0), Is.False);
        });
    }

    [Test]
    public void AnOpeningPunctuationCollapsesAfterAnotherPunctuation()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TextSpacingTrim.TrimsOpening(new(PunctuationClass.Opening), 20), Is.True);
            Assert.That(TextSpacingTrim.TrimsOpening(new(PunctuationClass.Middle), 20), Is.True);
            Assert.That(
                TextSpacingTrim.TrimsOpening(
                    new(PunctuationClass.Closing, Fullwidth: true, Size: 20), 20),
                Is.True);
            Assert.That(
                TextSpacingTrim.TrimsOpening(
                    new(PunctuationClass.Closing, Fullwidth: true, Size: 24), 20),
                Is.True);
            Assert.That(
                TextSpacingTrim.TrimsOpening(
                    new(PunctuationClass.Closing, Fullwidth: true, Size: 19), 20),
                Is.False,
                "a closing punctuation smaller than the opening does not collapse it");
            Assert.That(
                TextSpacingTrim.TrimsOpening(
                    new(PunctuationClass.Closing, Fullwidth: false, Size: 20), 20),
                Is.False,
                "a narrow closing quote does not collapse the opening beside it");
            Assert.That(
                TextSpacingTrim.TrimsOpening(
                    new(PunctuationClass.None, IdeographicSpace: true), 20),
                Is.True);
            Assert.That(TextSpacingTrim.TrimsOpening(new(PunctuationClass.None), 20), Is.False);
        });
    }

    [Test]
    public void AClosingPunctuationCollapsesBeforeAnotherPunctuation()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TextSpacingTrim.TrimsClosing(new(PunctuationClass.Closing), 20), Is.True);
            Assert.That(
                TextSpacingTrim.TrimsClosing(
                    new(PunctuationClass.Closing, Fullwidth: false), 20),
                Is.True,
                "a narrow closing quote still collapses the closing punctuation before it");
            Assert.That(TextSpacingTrim.TrimsClosing(new(PunctuationClass.Middle), 20), Is.True);
            Assert.That(
                TextSpacingTrim.TrimsClosing(
                    new(PunctuationClass.None, IdeographicSpace: true), 20),
                Is.True);
            Assert.That(
                TextSpacingTrim.TrimsClosing(new(PunctuationClass.Opening, Size: 24), 20),
                Is.True);
            Assert.That(
                TextSpacingTrim.TrimsClosing(new(PunctuationClass.Opening, Size: 20), 20),
                Is.False,
                "an opening of the same size does not collapse the closing before it");
            Assert.That(
                TextSpacingTrim.TrimsClosing(new(PunctuationClass.Opening, Size: 16), 20),
                Is.False);
            Assert.That(TextSpacingTrim.TrimsClosing(new(PunctuationClass.None), 20), Is.False);
        });
    }

    [Test]
    public void TrimAllTrimsEveryPunctuationWhereverItStands()
    {
        var all = Value(SpacingTrimStart.Trim, true, SpacingTrimEnd.Trim, all: true);
        var none = new TrimNeighbor(PunctuationClass.None);

        Assert.Multiple(() =>
        {
            foreach (var kind in new[]
                     {
                         PunctuationClass.Opening,
                         PunctuationClass.Closing,
                         PunctuationClass.Middle,
                     })
            {
                Assert.That(TextSpacingTrim.TrimsByAdjacency(all, kind, none, none, 20), Is.True);
                Assert.That(TextSpacingTrim.TrimsAtLineStart(all, kind, true), Is.True);
                Assert.That(TextSpacingTrim.TrimsAtLineEnd(all, kind, false), Is.True);
            }

            Assert.That(TextSpacingTrim.TrimsByAdjacency(all, PunctuationClass.None, none, none, 20), Is.False);
        });
    }

    [Test]
    public void SpaceAllTrimsNothing()
    {
        var spaceAll = Value(SpacingTrimStart.Space, false, SpacingTrimEnd.Space);
        var previous = new TrimNeighbor(PunctuationClass.Opening);
        var next = new TrimNeighbor(PunctuationClass.Closing);

        Assert.Multiple(() =>
        {
            foreach (var kind in new[]
                     {
                         PunctuationClass.Opening,
                         PunctuationClass.Closing,
                         PunctuationClass.Middle,
                     })
            {
                Assert.That(TextSpacingTrim.TrimsByAdjacency(spaceAll, kind, previous, next, 20), Is.False);
                Assert.That(TextSpacingTrim.TrimsAtLineStart(spaceAll, kind, false), Is.False);
                Assert.That(TextSpacingTrim.TrimsAtLineEnd(spaceAll, kind, true), Is.False);
            }
        });
    }

    [Test]
    public void AdjacencyIsOffWhereTheValueSaysSpaceAdjacent()
    {
        var spaceAdjacent = Value(SpacingTrimStart.Trim, false, SpacingTrimEnd.Allow);
        var previous = new TrimNeighbor(PunctuationClass.Opening);

        Assert.That(
            TextSpacingTrim.TrimsByAdjacency(
                spaceAdjacent, PunctuationClass.Opening, previous, default, 20),
            Is.False);
    }

    [Test]
    public void ALineStartFollowsTheThreeStartModes()
    {
        var normal = TextSpacingTrimValue.Initial;
        var trimStart = Value(SpacingTrimStart.Trim, true, SpacingTrimEnd.Allow);
        var spaceFirst = Value(SpacingTrimStart.SpaceFirst, true, SpacingTrimEnd.Allow);

        Assert.Multiple(() =>
        {
            Assert.That(TextSpacingTrim.TrimsAtLineStart(normal, PunctuationClass.Opening, true), Is.False);
            Assert.That(TextSpacingTrim.TrimsAtLineStart(normal, PunctuationClass.Opening, false), Is.False);

            Assert.That(TextSpacingTrim.TrimsAtLineStart(trimStart, PunctuationClass.Opening, true), Is.True);
            Assert.That(TextSpacingTrim.TrimsAtLineStart(trimStart, PunctuationClass.Opening, false), Is.True);

            Assert.That(
                TextSpacingTrim.TrimsAtLineStart(spaceFirst, PunctuationClass.Opening, true),
                Is.False,
                "the first line and the line after a forced break keep the opening spaced");
            Assert.That(
                TextSpacingTrim.TrimsAtLineStart(spaceFirst, PunctuationClass.Opening, false),
                Is.True,
                "a wrapped line trims it");

            Assert.That(TextSpacingTrim.TrimsAtLineStart(trimStart, PunctuationClass.Closing, false), Is.False);
            Assert.That(TextSpacingTrim.TrimsAtLineStart(trimStart, PunctuationClass.Middle, false), Is.False);
        });
    }

    [Test]
    public void ALineEndFollowsTheThreeEndModes()
    {
        var space = Value(SpacingTrimStart.Trim, true, SpacingTrimEnd.Space);
        var trim = Value(SpacingTrimStart.Trim, true, SpacingTrimEnd.Trim);
        var allow = Value(SpacingTrimStart.Trim, true, SpacingTrimEnd.Allow);
        var normal = TextSpacingTrimValue.Initial;

        Assert.Multiple(() =>
        {
            Assert.That(TextSpacingTrim.TrimsAtLineEnd(space, PunctuationClass.Closing, true), Is.False);
            Assert.That(TextSpacingTrim.TrimsAtLineEnd(space, PunctuationClass.Closing, false), Is.False);

            Assert.That(TextSpacingTrim.TrimsAtLineEnd(trim, PunctuationClass.Closing, true), Is.True);
            Assert.That(TextSpacingTrim.TrimsAtLineEnd(trim, PunctuationClass.Closing, false), Is.True);

            Assert.That(
                TextSpacingTrim.TrimsAtLineEnd(allow, PunctuationClass.Closing, true),
                Is.True,
                "the closing punctuation only collapses where the line would not fit");
            Assert.That(
                TextSpacingTrim.TrimsAtLineEnd(allow, PunctuationClass.Closing, false),
                Is.False,
                "a line that fits keeps it full-width");

            Assert.That(TextSpacingTrim.TrimsAtLineEnd(normal, PunctuationClass.Closing, true), Is.True);
            Assert.That(TextSpacingTrim.TrimsAtLineEnd(normal, PunctuationClass.Opening, true), Is.False);
            Assert.That(TextSpacingTrim.TrimsAtLineEnd(normal, PunctuationClass.Middle, true), Is.False);
        });
    }

    [Test]
    public void TheWidthFeaturesTurnThePropertyOffRatherThanDoubleTrim()
    {
        var exclusive = new[]
        {
            "halt=1", "hwid=1", "palt=1", "pwid=1", "qwid=1", "twid=1",
            "valt=1", "vhal=1", "vpal=1", "kern=1,halt=1", "HALT=1", "halt",
            "halt=0,halt=1", "kern=1, halt=1",
        };

        var notExclusive = new[]
        {
            (String?)null, "", "kern=1", "liga=1", "chws=1", "vchw=1", "fwid=1",
            "halt=0", "palt=0,vpal=0",
        };

        Assert.Multiple(() =>
        {
            foreach (var features in exclusive)
            {
                Assert.That(TextSpacingTrim.HasExclusiveFeature(features), Is.True, features);
            }

            foreach (var features in notExclusive)
            {
                Assert.That(TextSpacingTrim.HasExclusiveFeature(features), Is.False, features ?? "null");
            }
        });
    }

    private static TextSpacingTrimValue Parse(String declared)
    {
        var parsed = TextSpacingTrimValue.TryParse(declared, Inherited, out var value);

        Assert.That(parsed, Is.True, declared);
        return value;
    }

    private static TextSpacingTrimValue Value(
        SpacingTrimStart start,
        Boolean adjacent,
        SpacingTrimEnd end,
        Boolean all = false) => new(start, adjacent, end) { All = all };
}

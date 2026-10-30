using NUnit.Framework;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Hutu.Conformance;

/// <summary>
/// Draws the imported print pairs as print jobs: every page in its own page box, compared page by
/// page. These are the first pairs whose declared relation depends on pages existing at all.
/// </summary>
[TestFixture]
public sealed class PrintPaginationTests
{
    [Test]
    public async Task TheTwoPagePairComesToTwoPages()
    {
        var test = await ReftestRunner.DrawPagesAsync(Page("basic-pagination-005-print.html"));
        var reference = await ReftestRunner.DrawPagesAsync(Page("basic-pagination-005-print-ref.html"));

        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(test.Count, Is.EqualTo(2), "the empty first box takes a page of its own");
                Assert.That(reference.Count, Is.EqualTo(1), "the reference has only the line");
                Assert.That(test[0].Width, Is.EqualTo(293), "the page box the rule states");
                Assert.That(test[0].Height, Is.EqualTo(293));
            });

            var comparison = ReftestRunner.ComparePages(
                test, reference, "basic-pagination-005", writeDiff: false);

            Assert.That(comparison.Matched, Is.False, "the pair declares that the two must differ: one page against two");
        }
        finally
        {
            Disposed(test);
            Disposed(reference);
        }
    }

    [Test]
    public async Task APairThatAgreesOnOnePageStillAgreesInPrint()
    {
        var test = await ReftestRunner.DrawPagesAsync(Page("basic-pagination-001-print.html"));
        var reference = await ReftestRunner.DrawPagesAsync(Page("basic-pagination-001-print-ref.html"));

        try
        {
            Assert.That(test.Count, Is.EqualTo(1), "a break after the last box makes no new page");
            Assert.That(reference.Count, Is.EqualTo(1));

            var comparison = ReftestRunner.ComparePages(
                test, reference, "basic-pagination-001", writeDiff: false);

            Assert.That(comparison.Matched, Is.True, comparison.Detail);
        }
        finally
        {
            Disposed(test);
            Disposed(reference);
        }
    }

    private static String Page(String name) =>
        Path.Combine(Corpus.Root, "conformance", "reftests", "wpt", "css-page", name);

    private static void Disposed(System.Collections.Generic.IReadOnlyList<SkiaSharp.SKBitmap> bitmaps)
    {
        foreach (var bitmap in bitmaps)
        {
            bitmap.Dispose();
        }
    }
}

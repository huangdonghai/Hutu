using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers a multi-column container whose content is its own lines: the lines break at the width of one
/// column and are shared over the columns, and the container is as tall as the columns come to.
/// </summary>
/// <remarks>
/// The fake measurer gives every character five pixels, so a five-letter word is twenty-five and a
/// hundred-pixel column holds three of them with the spaces between.
/// </remarks>
[TestFixture]
public sealed class MulticolInlineTests
{
    [Test]
    public async Task TheLinesBreakAtTheColumnWidthAndShareTheColumns()
    {
        using var view = await Render(Text(12));

        var lines = Lines(view);
        var columns = lines.Select(line => line.BorderBox.X).Distinct().OrderBy(x => x).ToList();

        Assert.That(lines.Count, Is.EqualTo(4), "the text breaks at the column's width, not the block's");
        Assert.That(columns.Count, Is.EqualTo(2), "and the lines stand in the columns that share them");
        Assert.That(columns[0], Is.EqualTo(0));
        Assert.That(columns[1], Is.EqualTo(103.333).Within(0.01), "the column's share plus the gap between them");
        Assert.That(lines.First(line => line.BorderBox.X > 1).BorderBox.Y, Is.EqualTo(0), "each column begins at the top");
    }

    [Test]
    public async Task TheContainerIsAsTallAsTheColumnsWereSharedAt()
    {
        using var view = await Render(Text(12));

        Assert.That(Find(view, "c").BorderBox.Height, Is.EqualTo(20).Within(0.01), "two lines in each column");
    }

    [Test]
    public async Task TheColumnsKeepTheirOrphansAndWidows()
    {
        using var view = await Render(Text(20));

        var lines = Lines(view);
        var first = lines.Count(line => line.BorderBox.X < 1);
        var second = lines.Count(line => line.BorderBox.X > 1 && line.BorderBox.X < 200);

        Assert.That(lines.Count, Is.EqualTo(7), "the text breaks into seven lines at the column width");
        Assert.That(lines.Select(line => Math.Round(line.BorderBox.X, 3)).Distinct().Count(), Is.EqualTo(3), "over the three columns");
        Assert.That(first, Is.GreaterThanOrEqualTo(2), "no column leaves fewer lines before the break than the orphans");
        Assert.That(second, Is.GreaterThanOrEqualTo(2), "nor fewer after it than the widows");
        Assert.That(first + second, Is.LessThan(lines.Count), "and the third column takes the rest");
    }

    [Test]
    public async Task TheOrphansHoldTheBreakWhereTheWidowsWouldTakeTooMany()
    {
        // Nine lines over columns of four, with three lines owed on either side of every break: the
        // first column takes its four, and the second takes three rather than two, because pulling the
        // break back far enough to leave three widows would strand two lines before it. Chrome leaves
        // the widows short instead, and the third column takes the last two lines.
        using var view = await Render(
            "<style>#c { width: 460px; height: 40px; columns: 4; column-gap: 20px;" +
            " column-fill: auto; orphans: 3; widows: 3 }</style>" +
            "<div id='c'>1<br>2<br>3<br>4<br>5<br>6<br>7<br>8<br>9</div>");

        var byColumn = Lines(view)
            .GroupBy(line => Math.Round(line.BorderBox.X, 3))
            .OrderBy(group => group.Key)
            .Select(group => group.Count())
            .ToList();

        Assert.That(byColumn, Is.EqualTo(new[] { 4, 3, 2 }));
    }

    [Test]
    public async Task AFillWithNoHeightToFillTakesTheFirstColumnAlone()
    {
        // `column-fill: auto` fragments against a column height, and a box that declares none has no
        // height to fill to: its first column takes the whole stack (Chrome, widows-orphans-017).
        using var view = await Render(
            "<style>#c { width: 300px; columns: 3; column-gap: 10px; column-fill: auto }</style>" +
            "<div id='c'>1<br>2<br>3<br>4<br>5<br>6</div>");

        var lines = Lines(view);

        Assert.That(lines.Count, Is.EqualTo(6), "every line is placed");
        Assert.That(
            lines.Select(line => Math.Round(line.BorderBox.X, 3)).Distinct().Count(),
            Is.EqualTo(1),
            "and none is fragmented into a column of its own");
        Assert.That(Find(view, "c").BorderBox.Height, Is.EqualTo(60), "the container is as tall as the lines");
    }

    [Test]
    public async Task EveryColumnTrimsTheLeadingOffItsFirstLine()
    {
        // CSS Inline 3 §6.2: on a multi-column container the trim reaches the first line of every
        // column, not only the block's first. The fake font gives ten pixels of content in a twenty
        // pixel line, so each column's first line box stands five pixels above the column's top.
        using var view = await Render(
            "<style>#c { width: 300px; columns: 3; column-gap: 10px; column-fill: auto; height: 40px;" +
            " line-height: 20px; text-box-trim: trim-both }</style>" +
            "<div id='c'>1<br>2<br>3<br>4</div>");

        var tops = Lines(view)
            .GroupBy(line => Math.Round(line.BorderBox.X, 3))
            .Select(group => group.Min(line => line.BorderBox.Y))
            .ToList();

        Assert.That(tops.Count, Is.EqualTo(2), "the four lines share two columns");
        Assert.That(tops, Is.All.EqualTo(-5), "each column begins at its first line's text edge");
    }

    private static String Text(Int32 words) =>
        "<style>#c { width: 300px; columns: 3; column-gap: 10px }</style><div id='c'>" +
        String.Join(" ", Enumerable.Repeat("AAAAA", words)) + "</div>";

    private static async Task<HutuViewInternal> Render(String content)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>div { margin: 0 }</style></head>" +
            "<body style='margin:0;font-size:10px;line-height:10px'>" + content + "</body></html>");
        return view;
    }

    private static LayoutBox Find(HutuViewInternal view, String id) =>
        Walk(view.Layout!.Root).First(box => box.Source.Element?.GetAttribute("id") == id);

    private static List<LayoutBox> Lines(HutuViewInternal view) =>
        Walk(view.Layout!.Root).Where(box => box.Kind == LayoutKind.Line).ToList();

    private static IEnumerable<LayoutBox> Walk(LayoutBox node)
    {
        yield return node;

        foreach (var child in node.Children)
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }
}

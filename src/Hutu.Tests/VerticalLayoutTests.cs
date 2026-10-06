using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Vertical writing asserted through whole documents: the columns the block children stack into, the
/// way a line runs down one, the orientation each character is turned to, and the ruby annotations set
/// beside their base.
/// </summary>
[TestFixture]
public sealed class VerticalLayoutTests
{
    /// <summary>A column of a known size, set in a font the fake measurer gives 10px em boxes.</summary>
    private const String Column = "writing-mode:vertical-rl;width:300px;height:200px;font-size:10px;line-height:1;margin:0";

    [Test]
    public async Task ColumnStacksBlockChildrenRightToLeft()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='" + Column + "'>" +
            "<div style='width:30px;height:20px;margin:0'></div>" +
            "<div style='width:30px;height:20px;margin:0'></div>" +
            "</div></body></html>");

        var boxes = Blocks(view, "div").ToList();

        Assert.That(boxes[0].BorderBox.X, Is.EqualTo(0));
        Assert.That(boxes[0].BorderBox.Width, Is.EqualTo(300), "the declared width is the extent the columns stack along");
        Assert.That(boxes[0].BorderBox.Height, Is.EqualTo(200), "the declared height is how far each column runs");

        Assert.That(boxes[1].BorderBox.X, Is.EqualTo(270), "the first column sits against the right edge");
        Assert.That(boxes[2].BorderBox.X, Is.EqualTo(240), "the next one is the one to its left");
        Assert.That(boxes[1].BorderBox.Y, Is.EqualTo(0));
        Assert.That(boxes[1].BorderBox.Width, Is.EqualTo(30));
        Assert.That(boxes[1].BorderBox.Height, Is.EqualTo(20));
    }

    [Test]
    public async Task ColumnLaysItsChildrenOutToTheRightInVerticalLr()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-lr;width:300px;height:200px;margin:0'>" +
            "<div style='width:30px;height:20px;margin:0'></div>" +
            "<div style='width:30px;height:20px;margin:0'></div>" +
            "</div></body></html>");

        var boxes = Blocks(view, "div").ToList();

        Assert.That(boxes[1].BorderBox.X, Is.EqualTo(0));
        Assert.That(boxes[2].BorderBox.X, Is.EqualTo(30));
    }

    [Test]
    public async Task ColumnBreaksTextIntoLinesThatAdvanceLeftward()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:60px;height:40px;margin:0;font-size:10px;line-height:1'>" +
            "<p style='margin:0'>ああああああああ</p>" +
            "</div></body></html>");

        var lines = Lines(view).ToList();

        Assert.That(lines.Count, Is.EqualTo(2), "eight characters at 10px each do not fit down a 40px column at once");
        Assert.That(lines[0].BorderBox.Height, Is.EqualTo(40), "a line runs the whole height it was given, so it is taller than it is wide");
        Assert.That(lines[0].BorderBox.Width, Is.EqualTo(10));
        Assert.That(lines[0].BorderBox.X, Is.EqualTo(50));
        Assert.That(lines[1].BorderBox.X, Is.EqualTo(40), "the line after it is the one to its left");

        // The baseline is 8px into the em box, and a column measures it from its left edge.
        Assert.That(lines[0].Baseline, Is.EqualTo(2));
    }

    [Test]
    public async Task MixedOrientationKeepsIdeographsUprightAndTurnsLatin()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:200px;height:200px;margin:0;font-size:10px;line-height:1'>" +
            "<p style='margin:0'>あA</p>" +
            "</div></body></html>");

        var cjk = Text(view, "あ");
        var latin = Text(view, "A");

        Assert.That(cjk.Flow.IsVertical, Is.True);
        Assert.That(cjk.Flow.Orientation, Is.EqualTo(TextOrientation.Upright));
        Assert.That(cjk.BorderBox.Height, Is.EqualTo(10), "an upright character takes a whole em down the column");

        Assert.That(latin.Flow.Orientation, Is.EqualTo(TextOrientation.Sideways));
        Assert.That(latin.BorderBox.Height, Is.EqualTo(5), "a turned run takes the width it would have had in a line");
    }

    [Test]
    public async Task UprightStandsLatinCharactersUpOneUnderTheOther()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:200px;height:200px;margin:0;font-size:10px;line-height:1;text-orientation:upright'>" +
            "<p style='margin:0'>AB</p>" +
            "</div></body></html>");

        var latin = Text(view, "AB");

        Assert.That(latin.Flow.Orientation, Is.EqualTo(TextOrientation.Upright));
        Assert.That(latin.BorderBox.Height, Is.EqualTo(20), "each letter is given an em of its own instead of its own width");
    }

    [Test]
    public async Task SidewaysTurnsIdeographsWithTheRest()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:200px;height:200px;margin:0;font-size:10px;line-height:1;text-orientation:sideways'>" +
            "<p style='margin:0'>ああ</p>" +
            "</div></body></html>");

        var cjk = Text(view, "ああ");

        Assert.That(cjk.Flow.Orientation, Is.EqualTo(TextOrientation.Sideways));
        Assert.That(cjk.BorderBox.Height, Is.EqualTo(10), "the pair advances by the width of the characters rather than their em boxes");
    }

    [Test]
    public async Task CombiningUprightSetsShortRunsAsASingleEm()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:200px;height:200px;margin:0;font-size:10px;line-height:1'>" +
            "<p style='margin:0'>あ<span style='text-combine-upright:all'>2026</span></p>" +
            "</div></body></html>");

        var digits = Text(view, "2026");

        Assert.That(digits.Flow.CombinesUpright, Is.True, "the run was asked to stand as one upright box");
        Assert.That(digits.BorderBox.Height, Is.EqualTo(10), "four digits are squeezed into the em the characters around them take");
    }

    [Test]
    public async Task RubySetsItsReadingOnTheFarSideOfTheColumn()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:80px;height:60px;margin:0;font-size:10px;line-height:1'>" +
            "<p style='margin:0'><ruby>春<rt>はる</rt></ruby></p>" +
            "</div></body></html>");

        var baseText = Text(view, "春");
        var annotation = Text(view, "はる");

        Assert.That(annotation.BorderBox.X, Is.EqualTo(70), "the reading is set against the edge the column started at");
        Assert.That(annotation.BorderBox.Right, Is.EqualTo(80));
        Assert.That(annotation.BorderBox.X, Is.GreaterThan(baseText.BorderBox.X), "the reading is on the block-start side of the base");
        Assert.That(annotation.Font.Size, Is.LessThan(baseText.Font.Size), "a reading is set smaller than what it annotates");
        Assert.That(Lines(view).First().BorderBox.Width, Is.EqualTo(20), "the line grew by the room the reading needed");

        // The base stands in the orientation its own character resolves to, so an ideograph under a
        // reading is drawn upright exactly as one without it would be.
        Assert.That(baseText.Flow.Orientation, Is.EqualTo(TextOrientation.Upright));
        Assert.That(annotation.Flow.Orientation, Is.EqualTo(TextOrientation.Upright));
    }

    [Test]
    public async Task RubyBaseSharesTheColumnLineOfTheCharactersAroundIt()
    {
        // The reading adds a strip at the block-start end of the line, and the base keeps the rest -
        // but the rest is centred on the band, so the base is drawn on the very line its neighbours
        // stand on rather than half a reading off it.
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:80px;height:60px;margin:0;font-size:10px;line-height:1'>" +
            "<p style='margin:0'>春<ruby>夏<rt>なつ</rt></ruby>秋</p>" +
            "</div></body></html>");

        var before = Text(view, "春");
        var baseText = Text(view, "夏");
        var after = Text(view, "秋");
        var annotation = Text(view, "なつ");

        Assert.That(Centre(baseText), Is.EqualTo(Centre(before)).Within(0.01), "the base is on the line the character before it stands on");
        Assert.That(Centre(baseText), Is.EqualTo(Centre(after)).Within(0.01), "and on the one the character after it stands on");
        Assert.That(Centre(annotation), Is.GreaterThan(Centre(baseText)), "the reading is set on the block-start side of the base");
    }


    [Test]
    public async Task RubyBaseIsTurnedWhenItsOwnCharactersAreLatin()
    {
        // The base is oriented by its own text rather than by the fact that it carries a reading, so
        // a Latin base is still turned.
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:80px;height:60px;margin:0;font-size:10px;line-height:1'>" +
            "<p style='margin:0'><ruby>Ab<rt>えー</rt></ruby></p>" +
            "</div></body></html>");

        Assert.That(Text(view, "Ab").Flow.Orientation, Is.EqualTo(TextOrientation.Sideways));
    }

    [Test]
    public async Task RubySetsItsReadingAboveTheBaseInHorizontalWriting()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:1'><ruby>春<rt>はる</rt></ruby></p></body></html>");

        var baseText = Text(view, "春");
        var annotation = Text(view, "はる");

        Assert.That(annotation.BorderBox.Y, Is.EqualTo(0), "the reading takes the top of the line");
        Assert.That(annotation.BorderBox.Bottom, Is.EqualTo(baseText.BorderBox.Y));
        Assert.That(baseText.BorderBox.Y, Is.EqualTo(10), "the base keeps its own ascent, pushed down by the reading over it");
    }

    [Test]
    public async Task RubyPositionUnderSetsTheReadingOnTheOtherSide()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:1;ruby-position:under'><ruby>春<rt>はる</rt></ruby></p></body></html>");

        var baseText = Text(view, "春");
        var annotation = Text(view, "はる");

        Assert.That(baseText.BorderBox.Y, Is.EqualTo(0), "the base stays where it would have been without a reading");
        Assert.That(annotation.BorderBox.Y, Is.EqualTo(baseText.BorderBox.Bottom));
    }

    [Test]
    public async Task HorizontalTextIgnoresTextOrientation()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:1;text-orientation:sideways'>あA</p></body></html>");

        var line = Lines(view).Single();

        Assert.That(line.Flow.IsVertical, Is.False);
        Assert.That(line.BorderBox.Height, Is.EqualTo(10), "a line is still as tall as one em box");
        Assert.That(Text(view, "あA").BorderBox.Width, Is.EqualTo(10), "and advances by the width of its characters");
    }

    private static HutuViewInternal CreateView(int viewportWidth)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static IEnumerable<LayoutBox> Blocks(HutuViewInternal view, String tag)
    {
        var root = view.Layout?.Root;

        if (root is null)
        {
            yield break;
        }

        foreach (var node in Walk(root))
        {
            if (node.Kind == LayoutKind.Block &&
                node.Source.Element is { } element &&
                String.Equals(element.LocalName, tag, StringComparison.OrdinalIgnoreCase))
            {
                yield return node;
            }
        }
    }

    private static IEnumerable<LayoutBox> Lines(HutuViewInternal view)
    {
        var root = view.Layout?.Root;

        return root is null
            ? Enumerable.Empty<LayoutBox>()
            : Walk(root).Where(node => node.Kind == LayoutKind.Line);
    }

    /// <summary>Finds the one text box drawn from the given characters, which is how a run is picked out.</summary>
    private static LayoutBox Text(HutuViewInternal view, String text)
    {
        var root = view.Layout?.Root;

        if (root is null)
        {
            throw new AssertionException("The document was not laid out.");
        }

        return Walk(root).FirstOrDefault(node => node.Kind == LayoutKind.Text && node.Text == text)
            ?? throw new AssertionException($"The layout holds no text box for '{text}'.");
    }

    /// <summary>
    /// Gets the block-axis centre of a box, which is the column line its glyph is drawn on: an upright
    /// character is centred across the box it was given, so two characters on one line share a centre.
    /// </summary>
    private static Double Centre(LayoutBox box) => box.BorderBox.X + (box.BorderBox.Width / 2);

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

using Hutu.Rendering.Paint;
using Hutu.Skia;
using NUnit.Framework;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the order a stacking context lays its own ink down in: CSS 2.1 §Appendix E gives it a pass over
/// the backgrounds and borders of the block-level boxes that stayed in the flow, and only then a pass over
/// the inline-level content - so a line that stands in an early box is drawn over the ground a later box
/// paints, however much later in the document that one is written.
/// </summary>
/// <remarks>
/// The pages these are read from are the ones the corpus is built on: a green box that must cover a red
/// one placed over the same ground. Children paint in document order, so a walk that paints each box
/// completely as it reaches it buries the earlier line under the later block's background.
/// </remarks>
[TestFixture]
public sealed class PaintOrderTests
{
    private static readonly SKColor Green = new(0, 255, 0);

    private static readonly SKColor Red = new(255, 0, 0);

    [Test]
    public async Task AnAtomicInlineStandsOverTheGroundOfTheBlockThatFollows()
    {
        // The green square is an inline-table standing on the first line of the first div; the second div
        // is pulled up over it by a negative margin and paints red. The reference for the corpus pair this
        // is read from draws green, because the line is the later pass.
        var drawn = await Paint(
            "<div style='width:100px;height:100px'>" +
            "<span style='display:inline-table;width:100px;height:100px;background:#00ff00'></span>" +
            "</div>" +
            "<div style='margin-top:-100px;width:100px;height:100px;background:#ff0000'></div>");

        Assert.That(Pixel(drawn, 50, 50), Is.EqualTo(Green), "the line stands over the later block's background");
    }

    [Test]
    public async Task ALineDeepInAnEarlyBoxStandsOverALaterBoxsBackground()
    {
        // Same answer one level down: the green run is inside a div inside the first div, and the block
        // whose background is pulled over it is a sibling of that first div.
        var drawn = await Paint(
            "<div style='width:100px;height:100px'><div style='height:100px'>" +
            "<span style='display:inline-block;width:100px;height:100px;background:#00ff00'></span>" +
            "</div></div>" +
            "<div style='margin-top:-100px;width:100px;height:100px;background:#ff0000'></div>");

        Assert.That(Pixel(drawn, 50, 50), Is.EqualTo(Green), "the pass is over the context, not one box");
    }

    [Test]
    public async Task AFloatStandsOverTheGroundOfTheBlockThatFollows()
    {
        // A float is its own pass, between the block backgrounds and the inline content, and carries its
        // own subtree with it: a later block's background must not cover it either.
        var drawn = await Paint(
            "<div style='width:100px;height:100px'>" +
            "<div style='float:left;width:100px;height:100px;background:#00ff00'></div>" +
            "</div>" +
            "<div style='margin-top:-100px;width:100px;height:100px;background:#ff0000'></div>");

        Assert.That(Pixel(drawn, 50, 50), Is.EqualTo(Green), "the float is not buried by the later background");
    }

    [Test]
    public async Task ABlockStillStandsOnItsOwnGround()
    {
        // The rule is about one context's passes, not about a box and its own background: a box that is
        // painted over by a later sibling's *background* keeps its own ground under its content, and a
        // block's background still lies under its descendants' ink.
        var drawn = await Paint(
            "<div style='position:relative;width:100px;height:100px;background:#ff0000'>" +
            "<div style='position:absolute;left:0;top:0;width:100px;height:100px;background:#00ff00'></div>" +
            "</div>");

        Assert.That(Pixel(drawn, 50, 50), Is.EqualTo(Green), "a positioned child still paints over the ground");
        Assert.That(Pixel(drawn, 150, 50), Is.Not.EqualTo(Red), "nothing reaches beside the box");
    }

    /// <summary>
    /// An inline element that states a level under `relative` joins the levels the context gathers, the
    /// negative ones reaching under the ground of every block they stand in (CSS 2.1 §Appendix E, step 5
    /// for the negative level and step 3 for the inline content). The box is built while the line is laid
    /// out rather than arranged after it, so it has to carry the position and the level itself - or the
    /// walk that gathers the levels never sees one, and the two flow passes then leave the box for a layer
    /// that was never made: the page draws the element's ground and its letters nowhere at all.
    /// </summary>
    [Test]
    public async Task APlacedInlineJoinsTheLevelItsOwnStates()
    {
        var negative = await Order(
            "<div style='display:inline;background:#00ff00'>xx" +
            "<span style='position:relative;z-index:-1;background:#ff0000'>y</span></div>");

        var level = await Order(
            "<div style='display:inline;background:#00ff00'>xx" +
            "<span style='position:relative;background:#ff0000'>y</span></div>");

        Assert.Multiple(() =>
        {
            Assert.That(negative, Is.EqualTo(new[] { "red", "text y", "green", "text xx" }),
                "the negative level is laid down first, whole, under the ground it stands in");
            Assert.That(level, Is.EqualTo(new[] { "green", "text xx", "red", "text y" }),
                "at the automatic level the same box waits for the inline pass and paints over the ground");
        });
    }

    private static async Task<SKBitmap> Paint(String body)
    {
        using var view = new HutuViewInternal(new SkiaTextMeasurer());
        view.SetViewportSize(300, 300);
        await view.SetHtmlDocumentAsync(
            "<html><body style='margin:0;font-size:0'>" + body + "</body></html>",
            "file:///probe/order.html");

        using var surface = SKSurface.Create(new SKImageInfo(300, 300));
        var executor = new SkiaPaintExecutor(new SkiaTextMeasurer());

        executor.Execute(view.Paint!, surface.Canvas);

        using var image = surface.Snapshot();

        return SKBitmap.FromImage(image);
    }

    /// <summary>
    /// The commands a page paints with, named in the order they are laid down: the ground of either box
    /// by its colour and each run of letters by what it says. The order is the rule being read here, and
    /// it is read from the list rather than from a pixel, because a pixel cannot tell a box painted under
    /// a ground from a box that was never painted at all.
    /// </summary>
    private static async Task<List<String>> Order(String body)
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(300, 300);
        await view.SetHtml(
            $"<html><body style='margin:0;font-size:60px'>{body}</body></html>");

        return view.Paint!.Commands.Select(command => command switch
        {
            FillCommand { Color: 0xFF00FF00 } => "green",
            FillCommand { Color: 0xFFFF0000 } => "red",
            TextCommand text => $"text {text.Text}",
            _ => command.GetType().Name,
        }).ToList();
    }

    private static SKColor Pixel(SKBitmap bitmap, Int32 x, Int32 y) => bitmap.GetPixel(x, y);
}

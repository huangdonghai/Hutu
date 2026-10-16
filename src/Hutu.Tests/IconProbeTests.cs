using AngleSharp.Dom;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using Hutu.Skia;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading;

namespace Hutu.Tests;

/// <summary>
/// Prints the box subtree, the resolved style and the paint commands of the elements of a live page
/// whose class carries one of a list of needles, so what the engine measured for a box can be read
/// against what a browser reports for the same element - the way a glyph-driven box (an
/// <c>i.DCD_Icon</c> whose glyph comes from <c>::before</c> content), a nowrap label or an image on a
/// baseline is followed up. Not a test.
/// </summary>
[TestFixture]
[Explicit]
public sealed class IconProbeTests
{
    /// <summary>The class substrings to report on; change them for the element under the probe.</summary>
    private static readonly String[] Needles = { "vector-page-titlebar", "vector-page-toolbar" };

    [Test]
    [NonParallelizable]
    [CancelAfter(600_000)]
    public async Task Detail()
    {
        using var images = new SkiaImageLoader();
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer, images);
        view.SetViewportSize(3840, 2160);

        await view.LoadUrlAsync("https://zh.wikipedia.org/wiki/Wikipedia:%E9%A6%96%E9%A1%B5", CancellationToken.None);

        var document = view.Document!;
        var layout = view.Layout!;

        var needles = Needles;

        foreach (var needle in needles)
        {
            TestContext.Progress.WriteLine($"=== {needle}");

            foreach (var element in document.QuerySelectorAll($"*[class*='{needle}']").OfType<IElement>().Take(2))
            {
                TestContext.Progress.WriteLine(
                    $"  <{element.LocalName} class='{Truncate(element.GetAttribute("class") ?? "", 70)}'>" +
                    $" text='{Truncate(element.TextContent ?? "", 24)}'");

                var anchor = element;

                if (layout.FindBox(anchor) is not { } box)
                {
                    TestContext.Progress.WriteLine("      no box");
                    continue;
                }

                Dump(box, 6, 0);

                void Dump(LayoutBox node, Int32 room, Int32 depth)
                {
                    if (depth > room)
                    {
                        return;
                    }

                    var style = node.Source.Style;

                    TestContext.Progress.WriteLine(
                        $"      {new String(' ', depth * 2)}{node.Kind,-8}" +
                        $" {node.BorderBox.X,8:F1},{node.BorderBox.Y,7:F1} {node.BorderBox.Width,7:F1}x{node.BorderBox.Height,6:F1}" +
                        $" text='{Escape(node.Text ?? "")}'" +
                        $" fs={style.FontSize} lh={style.LineHeight?.ToString("F1") ?? "-"}" +
                        $" H={(style.Height.HasValue ? style.Height.Resolve(600).ToString("F1") : "auto")}" +
                        $" W={(style.Width.HasValue ? style.Width.Resolve(1600).ToString("F1") : "auto")}" +
                        $" disp={style.Display} of={style.ClipsOverflow}" +
                        $" pos={style.Position.Position}" +
                        $" mar={style.Margin.Resolve(3840).Top:F1}/{style.Margin.Resolve(3840).Bottom:F1}" +
                        $" pad={style.Padding.Resolve(3840).Top:F1}/{style.Padding.Resolve(3840).Bottom:F1}");

                    foreach (var child in node.Children)
                    {
                        Dump(child, room, depth + 1);
                    }
                }
            }
        }

        TestContext.Progress.WriteLine("=== paint around the reported cell (x 2100-2900, y 540-640)");
        var clip = 0;

        foreach (var command in view.Paint!.Commands)
        {
            var b = command.Bounds;

            if (b.X > 2100 && b.X < 2900 && b.Y > 530 && b.Y < 700)
            {
                var kind = command.GetType().Name;
                var text = command is Hutu.Rendering.Paint.TextCommand t ? "'" + Truncate(t.Text, 18) + "'" : String.Empty;

                if (command is Hutu.Rendering.Paint.PushClipCommand) clip++;

                if (kind is "PushClipCommand" or "PopClipCommand" or "TextCommand")
                {
                    TestContext.Progress.WriteLine($"  {kind,-18} {b.X,7:F1},{b.Y,7:F1} {b.Width,6:F1}x{b.Height,6:F1} {text}");
                }
            }
        }

        TestContext.Progress.WriteLine($"  push-clip commands in the area: {clip}");
        TestContext.Progress.WriteLine("=== short text commands (glyphs)");
        foreach (var command in view.Paint!.Commands.OfType<TextCommand>().Where(c => c.Text.Length is > 0 and <= 3).Take(14))
        {
            TestContext.Progress.WriteLine(
                $"  '{Escape(command.Text)}' size={command.Font.Size:F1} at {command.Bounds.X:F1},{command.Bounds.Y:F1}" +
                $" {command.Bounds.Width:F1}x{command.Bounds.Height:F1}");
        }

        TestContext.Progress.WriteLine("=== @font-face families seen by the engine");
        foreach (var face in Hutu.Rendering.FontFaceTable.From(document).Requests())
        {
            TestContext.Progress.WriteLine($"  {face.Family} {Truncate(face.Url, 90)}");
        }
    }

    private static String Escape(String text) =>
        String.Concat(text.Select(c => c < 0x20 || c > 0x7E ? $"\\u{(Int32)c:x4}" : c.ToString()));

    private static String Truncate(String text, Int32 room) =>
        text.Length <= room ? text : text[..room] + "...";
}

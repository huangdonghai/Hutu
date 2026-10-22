using AngleSharp.Dom;
using Hutu.Rendering;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Media;
using Hutu.Skia;
using NUnit.Framework;
using System;
using System.IO;
using System.Text;

namespace Hutu.Tests;

/// <summary>
/// Dumps the box tree of one page, so a box that never reaches the layout can be told from one the
/// layout lost.
/// </summary>
/// <remarks>
/// <c>LayoutProbe</c>, in the conformance project, reads the tree after the geometry has been settled,
/// which is the right thing to look at when a box is in the wrong place. It is the wrong thing to look at
/// when a box is missing: a flex item that was never wrapped, a run of words the container swallowed and a
/// child the positioning pass dropped all leave nothing behind to read, and the dump says only that the
/// container has no children. This prints the stage before that one, with the three things that decide
/// where a box goes - its kind, whether it carries a style of its own, and the position and display the
/// reader came to - so the question "was it built?" is answered separately from "was it placed?".
/// <para>
/// It checks nothing, which is why it is explicit and driven by the environment: with no page set it
/// ignores itself.
/// </para>
/// <list type="bullet">
/// <item><c>HUTU_BOX_PAGE</c> - the html file to read.</item>
/// <item><c>HUTU_BOX_OUT</c> - where to write the tree; defaults to <c>build/box-tree.txt</c>.</item>
/// </list>
/// </remarks>
[TestFixture]
[Explicit("it reads one page for a person, and checks nothing")]
public sealed class BoxTreeProbeTests
{
    [Test]
    [NonParallelizable]
    public async Task DumpBoxes()
    {
        var page = Environment.GetEnvironmentVariable("HUTU_BOX_PAGE");

        if (String.IsNullOrWhiteSpace(page) || !File.Exists(page))
        {
            Assert.Ignore("set HUTU_BOX_PAGE to the html file to read");
            return;
        }

        using var images = new SkiaImageLoader();
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer, images);
        view.SetViewportSize(491, 300);
        await view.SetHtml(File.ReadAllText(page), new Uri(page).AbsoluteUri);

        var device = new HutuRenderDevice(491, 300);
        var root = BoxBuilder.TryBuildFused(view.Document!, device, (IImageProvider?)null);

        Assert.That(root, Is.Not.Null, "the fused build abandoned the document");

        var text = new StringBuilder();
        Walk(root!, 0, text);

        var target = Environment.GetEnvironmentVariable("HUTU_BOX_OUT") ?? Path.Combine("build", "box-tree.txt");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        File.WriteAllText(target, text.ToString());
        TestContext.Out.WriteLine(text);
    }

    private static void Walk(Box box, Int32 depth, StringBuilder text)
    {
        var border = box.Style.ResolvedBorder;

        text.Append(new String(' ', depth * 2))
            .Append(box.Kind).Append(' ')
            .Append(box.Element?.TagName ?? "~anon")
            .Append(box.Element?.Id is { Length: > 0 } id ? "#" + id : String.Empty)
            .Append(box.IsAnonymous ? " anonymous" : String.Empty)
            .Append(box.IsItem ? " item" : String.Empty)
            .Append(box.IsInlineOuter ? " inlineOuter" : String.Empty)
            .Append(" pos=").Append(box.Style.Position.Position)
            .Append(" display=").Append(box.Style.Display)
            .Append(" border=").Append(border.TopWidth).Append('/')
            .Append(border.RightWidth).Append('/')
            .Append(border.BottomWidth).Append('/')
            .Append(border.LeftWidth).Append('=')
            .Append(border.TopStyle).Append('/').Append(border.BottomStyle)
            .Append(" ink=").Append(box.Style.BorderBottomColor?.CssText ?? "null");

        if (box.Text is { Length: > 0 } words)
        {
            text.Append(" text=").Append(words.Replace("\n", "\\n"));
        }

        text.AppendLine();

        foreach (var child in box.Children)
        {
            Walk(child, depth + 1, text);
        }
    }
}

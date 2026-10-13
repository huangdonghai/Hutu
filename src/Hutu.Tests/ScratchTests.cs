using Hutu.Rendering.Layout;
using Hutu.Skia;
using NUnit.Framework;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>A scratch pad for the qq.com findings; not part of the suite's story.</summary>
[Explicit]
public sealed class ScratchTests
{
    [Test]
    public async Task TwoValueFlexShorthand()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(400, 200);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px'>" +
            "<span id='a' style='flex:1 1'>热点精选</span>" +
            "<a id='b' style='flex:0 0 auto'>更多</a>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");

        NUnit.Framework.TestContext.Progress.WriteLine(
            $"a {a.BorderBox.Width}x{a.BorderBox.Height} at {a.BorderBox.X}, " +
            $"b {b.BorderBox.Width}x{b.BorderBox.Height} at {b.BorderBox.X}");
    }

    [Test]
    public async Task ZeroBasisWithGrow()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(400, 200);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px'>" +
            "<div id='a' style='flex:1 1 0%'>热点精选</div>" +
            "<div id='b' style='width:60px'></div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");

        NUnit.Framework.TestContext.Progress.WriteLine(
            $"a {a.BorderBox.Width}x{a.BorderBox.Height} at {a.BorderBox.X}, " +
            $"b {b.BorderBox.Width}x{b.BorderBox.Height} at {b.BorderBox.X}");
    }

    [Test]
    public async Task SpanWithGrowLonghand()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(400, 200);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px'>" +
            "<span id='a' style='flex-grow:1'>热点精选</span>" +
            "<a id='b'>更多</a>" +
            "</div>" +
            "</body></html>");

        Report(view, "a", "b");
    }

    [Test]
    public async Task SpanWithExplicitZeroBasis()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(400, 200);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px'>" +
            "<span id='a' style='flex:1 1 0%'>热点精选</span>" +
            "<a id='b'>更多</a>" +
            "</div>" +
            "</body></html>");

        Report(view, "a", "b");
    }

    [Test]
    public async Task DivWithTwoValueShorthand()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(400, 200);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px'>" +
            "<div id='a' style='flex:1 1'>热点精选</div>" +
            "<a id='b'>更多</a>" +
            "</div>" +
            "</body></html>");

        Report(view, "a", "b");
    }

    [Test]
    public async Task SubtreeOfInlineFlexItem()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(400, 200);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px'>" +
            "<span id='a' style='flex:1 1 0%'>热点精选</span>" +
            "<a id='b'>更多</a>" +
            "</div>" +
            "</body></html>");

        foreach (var box in Walk(view.Layout!.Root))
        {
            NUnit.Framework.TestContext.Progress.WriteLine(
                $"  {box.Source.Element?.GetAttribute("id") ?? box.Source.Element?.LocalName ?? box.Kind.ToString(),-12} " +
                $"kind={box.Kind} {box.BorderBox.Width:F1}x{box.BorderBox.Height:F1} at {box.BorderBox.X}");
        }
    }

    private static void Report(HutuViewInternal view, string a, string b)
    {
        var boxA = Find(view, a);
        var boxB = Find(view, b);

        var elementA = boxA?.Source.Element ?? view.Document!.QuerySelector("#" + a);
        var computed = view.GetComputedStyle(elementA!);

        NUnit.Framework.TestContext.Progress.WriteLine(
            $"{a} {boxA?.BorderBox.Width:F1}x{boxA?.BorderBox.Height:F1} at {boxA?.BorderBox.X} | " +
            $"display={computed.GetPropertyValue("display")} " +
            $"flex={computed.GetPropertyValue("flex")} " +
            $"grow={computed.GetPropertyValue("flex-grow")} " +
            $"shrink={computed.GetPropertyValue("flex-shrink")} " +
            $"basis={computed.GetPropertyValue("flex-basis")} | " +
            $"b {boxB?.BorderBox.Width:F1} at {boxB?.BorderBox.X}");
    }

    private static LayoutBox? Find(HutuViewInternal view, string id) =>
        Walk(view.Layout!.Root).FirstOrDefault(box => box.Source.Element?.GetAttribute("id") == id);

    private static System.Collections.Generic.IEnumerable<LayoutBox> Walk(LayoutBox node)
    {
        yield return node;

        foreach (var child in node.Children)
        {
            foreach (var held in Walk(child))
            {
                yield return held;
            }
        }
    }
}

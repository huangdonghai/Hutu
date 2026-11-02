using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the box a select makes: a menu list shows the one option it selects, while a picker of
/// several keeps its list (HTML 5.13.5 selected option, 15.4 rendering).
/// </summary>
[TestFixture]
public sealed class MenuListTests
{
    [Test]
    public async Task AMenuListShowsOneOptionAndNoOther()
    {
        using var view = await Shown("<select id='s'><option>AAA</option><option>BBB</option></select>");

        Assert.Multiple(() =>
        {
            Assert.That(Options(view).Select(Label).ToList(), Is.EqualTo(new[] { "AAA" }),
                "the option it selects is the first one it offers");
            Assert.That(Tall(view, "s"), Is.EqualTo(10).Within(0.01), "one line of the chosen label");
        });
    }

    [Test]
    public async Task TheOptionThePageMarkedIsTheOneShown()
    {
        using var view = await Shown("<select id='s'><option>AAA</option><option selected>BBB</option></select>");

        Assert.That(Options(view).Select(Label).ToList(), Is.EqualTo(new[] { "BBB" }));
    }

    [Test]
    public async Task APickerOfSeveralKeepsItsWholeList()
    {
        using var view = await Shown("<select id='s' multiple><option>AAA</option><option>BBB</option></select>");

        Assert.That(Options(view).Select(Label).ToList(), Is.EqualTo(new[] { "AAA", "BBB" }),
            "a list box is not a button, and its options are boxes of their own");
    }

    private static async Task<HutuViewInternal> Shown(String content)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>select, option { margin: 0; padding: 0; border: 0;" +
            "font: 10px/10px serif }</style></head><body style='margin:0'>" + content + "</body></html>");
        return view;
    }

    private static List<LayoutBox> Options(HutuViewInternal view) =>
        Walk(view).Where(box => String.Equals(box.Source.Element?.LocalName, "option", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static String Label(LayoutBox option) =>
        option.Source.Element?.TextContent ?? "";

    private static Double Tall(HutuViewInternal view, String id) =>
        Walk(view).First(box => box.Source.Element?.GetAttribute("id") == id).BorderBox.Height;

    private static IEnumerable<LayoutBox> Walk(HutuViewInternal view)
    {
        foreach (var box in DepthFirst(view.Layout!.Root))
        {
            yield return box;
        }
    }

    private static IEnumerable<LayoutBox> DepthFirst(LayoutBox node)
    {
        yield return node;

        foreach (var child in node.Children)
        {
            foreach (var below in DepthFirst(child))
            {
                yield return below;
            }
        }
    }
}

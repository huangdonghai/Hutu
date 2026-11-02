using AngleSharp.Css;
using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers what the engine says about a document that has scripting: the <c>scripting</c> media feature
/// in a boolean query and in a stated state, over the screen and the print medium, and the content a
/// <c>noscript</c> element keeps for such a document (CSS Media Queries 5 A6, HTML 5.3 A12.3.6).
/// </summary>
[TestFixture]
public sealed class ScriptingFeatureTests
{
    [Test]
    public async Task ScriptingAskedWithoutAValueAnswersForBothMedia()
    {
        using var view = await Shown(
            "<style>@media (scripting) { #d { height: 40px } }</style><div id='d'></div>");

        // A screen that has scripting answers enabled; the query with no value stands for every state
        // but none, so both media answer it the same way.
        Assert.That(Tall(view, "d"), Is.EqualTo(40).Within(0.01), "the screen reads the boolean query");

        view.SetMediaType(DeviceCategory.Printer);

        Assert.That(Tall(view, "d"), Is.EqualTo(40).Within(0.01), "and so does the printer");
    }

    [Test]
    public async Task AStatedScriptingStateBelongsToTheMediumThatCarriesIt()
    {
        using var view = await Shown(
            "<style>@media (scripting: enabled) { #e { height: 30px } }" +
            "@media (scripting: initial-only) { #i { height: 30px } }</style>" +
            "<div id='e'></div><div id='i'></div>");

        Assert.Multiple(() =>
        {
            Assert.That(Tall(view, "e"), Is.EqualTo(30).Within(0.01), "a screen with scripting is enabled");
            Assert.That(Tall(view, "i"), Is.EqualTo(0).Within(0.01), "and says so, not initial-only");
        });

        view.SetMediaType(DeviceCategory.Printer);

        Assert.Multiple(() =>
        {
            // Printing a document does not load its scripts again, which is the state the medium names.
            Assert.That(Tall(view, "e"), Is.EqualTo(0).Within(0.01), "print is past the first load");
            Assert.That(Tall(view, "i"), Is.EqualTo(30).Within(0.01), "and answers initial-only");
        });
    }

    [Test]
    public async Task ANoscriptElementKeepsItsContentFromTheLayout()
    {
        using var view = await Shown(
            "<div id='a' style='height:20px'></div>" +
            "<noscript><div id='kept' style='height:50px'></div></noscript>" +
            "<div id='b' style='height:20px'></div>");

        Assert.That(
            Walk(view).Any(box => box.Source.Element?.GetAttribute("id") == "kept"),
            Is.False,
            "what a noscript holds is a script's place, not a box");

        Assert.That(Tall(view, "b"), Is.EqualTo(20).Within(0.01));
        Assert.That(
            Top(view, "b"),
            Is.EqualTo(Bottom(view, "a")).Within(0.01),
            "and the flow closes over it");
    }

    private static async Task<HutuViewInternal> Shown(String content)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>div { margin: 0 }</style></head>" +
            "<body style='margin:0;font-size:10px;line-height:10px'>" + content + "</body></html>");
        return view;
    }

    private static Double Tall(HutuViewInternal view, String id) =>
        Find(view, id).BorderBox.Height;

    private static Double Top(HutuViewInternal view, String id) =>
        Find(view, id).BorderBox.Y;

    private static Double Bottom(HutuViewInternal view, String id) =>
        Find(view, id).BorderBox.Bottom;

    private static LayoutBox Find(HutuViewInternal view, String id) =>
        Walk(view).First(box => box.Source.Element?.GetAttribute("id") == id);

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

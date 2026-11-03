using Hutu.Rendering;
using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the computed value of <c>text-spacing-trim</c> reaching the text it applies to, and the
/// room a trimmed punctuation takes on a line.
/// </summary>
[TestFixture]
public sealed class TextSpacingTrimLayoutTests
{
    [Test]
    public async Task ThePropertyReachesTheTextBelowTheElementThatStatesIt()
    {
        using var view = await Rendered(
            "<html class='trim'><head><style>.trim { text-spacing-trim: trim-start }</style></head>" +
            "<body style='margin:0'>" +
            "<div id='a'>国</div>" +
            "<div id='b' style='text-spacing-trim: trim-all'>国</div>" +
            "<div id='c' style='text-spacing-trim: space-all'>国</div>" +
            "</body></html>");

        Assert.Multiple(() =>
        {
            Assert.That(Style(view, "a").TextSpacingTrim.Start, Is.EqualTo(SpacingTrimStart.Trim));
            Assert.That(Style(view, "b").TextSpacingTrim.All, Is.True);
            Assert.That(Style(view, "c").TextSpacingTrim.Start, Is.EqualTo(SpacingTrimStart.Space));
            Assert.That(Style(view, "c").TextSpacingTrim.Adjacent, Is.False);
            Assert.That(Style(view, "c").TextSpacingTrim.End, Is.EqualTo(SpacingTrimEnd.Space));
        });
    }

    [Test]
    public async Task TheShorthandSetsBothLonghandsItStandsFor()
    {
        using var view = await Rendered(
            "<html><body style='margin:0'>" +
            "<div id='a' style='text-spacing: none'>国</div>" +
            "<div id='b' style='text-spacing: trim-all'>国</div>" +
            "<div id='c' style='text-spacing: ideograph-numeric trim-both'>国</div>" +
            "</body></html>");

        Assert.Multiple(() =>
        {
            Assert.That(Style(view, "a").TextSpacingTrim.Start, Is.EqualTo(SpacingTrimStart.Space));
            Assert.That(Style(view, "a").TextSpacingTrim.Adjacent, Is.False);
            Assert.That(Style(view, "a").TextSpacingTrim.End, Is.EqualTo(SpacingTrimEnd.Space));
            Assert.That(Style(view, "a").TextAutospace.Off, Is.True, "none turns autospacing off");

            Assert.That(Style(view, "b").TextSpacingTrim.All, Is.True);
            Assert.That(
                Style(view, "b").TextAutospace.Kinds,
                Is.EqualTo(TextAutospaceValue.Initial.Kinds),
                "the longhand the value does not name takes its initial value");

            Assert.That(Style(view, "c").TextSpacingTrim.Start, Is.EqualTo(SpacingTrimStart.Trim));
            Assert.That(Style(view, "c").TextSpacingTrim.Adjacent, Is.True);
            Assert.That(Style(view, "c").TextSpacingTrim.End, Is.EqualTo(SpacingTrimEnd.Trim));
            Assert.That(Style(view, "c").TextAutospace.Kinds, Is.EqualTo(AutoSpaceKind.Numeric));
        });
    }

    private static async Task<HutuViewInternal> Rendered(String html)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(800, 600);
        await view.SetHtml(html);
        return view;
    }

    private static ComputedStyle Style(HutuViewInternal view, String id) =>
        Walk(view.Layout!.Root).First(box => box.Source.Element?.GetAttribute("id") == id).Source.Style;

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

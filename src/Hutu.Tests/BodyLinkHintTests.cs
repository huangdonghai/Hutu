using Hutu.Rendering;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the colours a page hints at with the <c>body</c> element's <c>link</c>, <c>vlink</c> and
/// <c>alink</c> attributes, which HTML 2.3.6 says name the colour of every element in the document
/// matching <c>:link</c>, <c>:visited</c> or <c>:active</c>.
/// </summary>
/// <remarks>
/// A hint sits exactly where the engine's own link colour already sat: below anything the page writes for
/// the link, above the colour the link would take from its parent. Chrome answers both of those - an
/// author <c>* { color: ... }</c> takes a link back from a hint, and a hint keeps a link out of the colour
/// a parent carries - which is what the tests here hold the engine to.
/// </remarks>
[TestFixture]
public sealed class BodyLinkHintTests
{
    private const UInt32 Pink = 0xFFFFC0CB;
    private const UInt32 Green = 0xFF008000;
    private const UInt32 Blue = 0xFF0000FF;
    private const UInt32 Navy = 0xFF000080;
    private const UInt32 Black = 0xFF000000;
    private const UInt32 EngineBlue = 0xFF0000EE;

    [Test]
    public async Task ThePageNamesTheColourOfALinkItHasNotBeenTo()
    {
        Assert.That(
            await Ink("<body style='margin:0' link='#008000'><a href='next.htm'>link</a>"),
            Is.EqualTo(Green),
            "the hint is the colour the link is drawn in");
    }

    [Test]
    public async Task ThePageNamesTheColourOfALinkItHasBeenTo()
    {
        using var view = CreateView();
        await view.SetHtml(Page("<body style='margin:0' link='#ff0000' vlink='#008000'>" +
            "<a href='next.htm'>link</a>"));

        Assert.That(Ink(view, "link"), Is.EqualTo(0xFFFF0000u), "unvisited, so the first of the two");

        view.IsVisited = _ => true;

        Assert.That(view.RefreshVisited(), Is.True, "the link has been read, so the document is built again");
        Assert.That(Ink(view, "link"), Is.EqualTo(Green), "and the colour named for a link that has been read");
    }

    [Test]
    public async Task ThePageNamesTheColourOfTheLinkBeingFollowed()
    {
        using var view = CreateView();
        await view.SetHtml(Page("<body style='margin:0' link='#ff0000' alink='#000080'>" +
            "<a href='next.htm'>link</a>"));

        var run = TextRun(view, "link");

        Assert.That(Ink(view, "link"), Is.EqualTo(0xFFFF0000u), "nothing is being followed yet");
        Assert.That(view.PointerPressed(run.BorderBox.X + 1, run.BorderBox.Y + 1), Is.True);
        Assert.That(Ink(view, "link"), Is.EqualTo(Navy), "the colour named for the one being followed now");
    }

    [Test]
    public async Task AHintStandsBelowTheColourThePageWritesForTheLink()
    {
        Assert.That(
            await Ink("<body style='margin:0' link='#008000'><style>a { color: #0000ff }</style>" +
                "<a href='next.htm'>link</a>"),
            Is.EqualTo(Blue),
            "a rule the page wrote takes the link back from the hint");

        Assert.That(
            await Ink("<body style='margin:0' link='#008000'><style>* { color: #0000ff }</style>" +
                "<a href='next.htm'>link</a>"),
            Is.EqualTo(Blue),
            "and so does a rule that names no selector at all");
    }

    [Test]
    public async Task AHintStandsAboveTheColourTakenFromAParent()
    {
        Assert.That(
            await Ink("<body style='margin:0' link='#008000'><div style='color:#0000ff'>" +
                "<a href='next.htm'>link</a></div>"),
            Is.EqualTo(Green),
            "the hint is a colour the link itself is given, and inheritance answers nothing given");
    }

    [Test]
    public async Task AHintSaysNothingAboutWordsThatAreNotLinks()
    {
        using var view = CreateView();
        await view.SetHtml(Page(
            "<body style='margin:0' link='#008000'><a>plain</a><p>words</p></body>"));

        Assert.That(Ink(view, "plain"), Is.EqualTo(Black), "an anchor with nowhere to go is no link");
        Assert.That(Ink(view, "words"), Is.EqualTo(Black), "and neither is a paragraph");
    }

    [TestCase("")]
    [TestCase("transparent")]
    public async Task AValueThatNamesNoColourLeavesTheEnginesOwnLinkColour(String value)
    {
        var ink = await Ink("<body style='margin:0' link='" + value + "'><a href='next.htm'>link</a>");

        Assert.That(ink, Is.EqualTo(EngineBlue), "the hint fails, so the engine answers for the link");
    }

    [Test]
    public async Task TheHintIsReadTheWayTheLegacyAttributesAreRead()
    {
        // A value that is no colour at all is still read, out of its digits, the way these attributes
        // have always been read - and it reaches the page as the colour those digits spell.
        Assert.That(
            await Ink("<body style='margin:0' link='notacolor'><a href='next.htm'>link</a>"),
            Is.EqualTo(0xFF00AC00u),
            "the digits of the value stand in place of a colour it does not name");
    }

    [Test]
    public async Task TwoLinksShareTheColourTheirDocumentNames()
    {
        using var view = CreateView();
        await view.SetHtml(Page(
            "<body style='margin:0' link='#008000'><a href='one.htm'>first</a><a href='two.htm'>second</a>"));

        Assert.That(Ink(view, "first"), Is.EqualTo(Green));
        Assert.That(Ink(view, "second"), Is.EqualTo(Green), "the hint is written for the document, not one link");
    }

    private static async Task<UInt32> Ink(String body)
    {
        using var view = CreateView();
        await view.SetHtml(Page(body));

        return Ink(view, "link");
    }

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        return view;
    }

    private static String Page(String body) => "<html>" + body + "</html>";

    private static UInt32 Ink(HutuViewInternal view, String text) =>
        view.Paint!.Commands.OfType<TextCommand>().First(command => command.Text == text).Color;

    private static LayoutBox TextRun(HutuViewInternal view, String text) =>
        Walk(view.Layout!.Root).First(box => box.Kind == LayoutKind.Text && box.Text == text);

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

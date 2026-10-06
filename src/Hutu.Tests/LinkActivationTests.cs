using Hutu.Rendering.Interaction;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Following a link: what an anchor resolves to, which links the document can settle by itself, and
/// which ones the host is asked about.
/// </summary>
/// <remarks>
/// Most documents are loaded with an <c>http</c> base, because that is where a relative href resolves
/// the way the specification says. The one anchor case is loaded with the engine's own <c>res:</c>
/// scheme, which is where a fragment link has to be recognised without asking the address type to
/// resolve anything.
/// </remarks>
[TestFixture]
public sealed class LinkActivationTests
{
    [Test]
    public async Task ALinkResolvesAgainstTheDocumentItIsIn()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><a href='../other/page.htm'>next</a></body></html>",
            "http://example.com/book/index.htm");

        var link = view.LinkAt(1, 1);

        Assert.That(link, Is.Not.Null);
        Assert.That(
            link!.Value.Url,
            Is.EqualTo("http://example.com/other/page.htm"),
            "a relative href is resolved against the document it was written in");
        Assert.That(link.Value.IsFragment, Is.False);
    }

    [Test]
    public async Task ALinkCarriesTheContextAndTheDownloadItAsksFor()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<a href='next.htm' target='preview' download='saved.htm'>next</a>" +
            "</body></html>",
            "http://example.com/book/index.htm");

        var link = view.LinkAt(1, 1);

        Assert.That(link!.Value.Url, Is.EqualTo("http://example.com/book/next.htm"));
        Assert.That(link.Value.Target, Is.EqualTo("preview"));
        Assert.That(link.Value.IsDownload, Is.True);
    }

    [Test]
    public async Task AnAnchorInTheDocumentIsNotANavigation()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<style>a { color: #0000ff } :target { color: #ff0000 }</style>" +
            "<a href='#section'>jump</a>" +
            "<div id='section' style='height:50px'>here</div>" +
            "</body></html>",
            "index.htm");

        var requests = new List<LinkTarget>();
        view.NavigationRequested += requests.Add;

        var link = view.LinkAt(1, 1);

        Assert.That(link!.Value.IsFragment, Is.True, "the link points into the document it is written in");
        Assert.That(link.Value.Fragment, Is.EqualTo("section"));
        Assert.That(PaintedColour(view, "here"), Is.EqualTo(0xFF000000u), "nothing is the target yet");

        Assert.That(view.Activate(link.Value), Is.EqualTo("section"), "and the anchor is what to scroll to");
        Assert.That(requests, Is.Empty, "there is nothing to load");
        Assert.That(PaintedColour(view, "here"), Is.EqualTo(0xFFFF0000u), "the anchor answers to :target now");
    }

    [Test]
    public async Task ALinkElsewhereIsHandedToTheHost()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'><a href='next.htm'>next</a></body></html>",
            "http://example.com/book/index.htm");

        var requests = new List<LinkTarget>();
        view.NavigationRequested += requests.Add;

        var link = view.LinkAt(1, 1);

        Assert.That(view.Activate(link!.Value), Is.Null, "there is nothing in this document to scroll to");
        Assert.That(requests, Has.Count.EqualTo(1));
        Assert.That(requests[0].Url, Is.EqualTo("http://example.com/book/next.htm"));
        Assert.That(requests[0].IsFragment, Is.False);
        Assert.That(requests[0].Target, Is.Null, "a link that names no context asks for the one it is in");
        Assert.That(requests[0].IsDownload, Is.False);
    }

    [Test]
    public async Task AnAnchorThatGoesNowhereIsNotALink()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<a href=''>empty</a><a>none</a><a href='#'>hash</a>" +
            "</body></html>",
            "index.htm");

        var anchors = view.Document!.GetElementsByTagName("a").ToArray();

        Assert.That(anchors, Has.Length.EqualTo(3));
        Assert.That(anchors.Select(LinkResolver.Resolve), Is.All.Null, "none of them names anywhere to go");
        Assert.That(
            LinkResolver.Resolve(view.Document.DocumentElement!),
            Is.Null,
            "and an element that is not an anchor is not a link");
    }

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        return view;
    }

    /// <summary>Gets the colour the painter draws a run of text in.</summary>
    private static UInt32 PaintedColour(HutuViewInternal view, String text) =>
        view.Paint!.Commands.OfType<TextCommand>().First(command => command.Text == text).Color;
}

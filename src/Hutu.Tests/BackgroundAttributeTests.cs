using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The legacy <c>background</c> attribute is a presentational hint the rendering requirements still
/// honour on the body and the parts of a table, and on nothing else: a div carrying it draws no
/// picture. The commands the engine emits say which boxes took the hint up.
/// </summary>
[TestFixture]
public sealed class BackgroundAttributeTests
{
    [Test]
    public async Task ATableTakesItsBackgroundAttributeAsABackgroundImage()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table background='aqua.png'><tr><td>cell</td></tr></table>" +
            "</body></html>");

        Assert.That(
            view.Paint!.Commands.OfType<BackgroundImageCommand>(),
            Is.Not.Empty,
            "a table honours the background attribute");
    }

    [Test]
    public async Task TheBodyTakesItsBackgroundAttributeAsABackgroundImage()
    {
        using var view = CreateView();
        await view.SetHtml("<html><body background='aqua.png'>text</body></html>");

        Assert.That(
            view.Paint!.Commands.OfType<BackgroundImageCommand>(),
            Is.Not.Empty,
            "the body honours the background attribute");
    }

    [Test]
    public async Task ADivIgnoresItsBackgroundAttribute()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div background='aqua.png'>Ohai</div>" +
            "</body></html>");

        Assert.That(
            view.Paint!.Commands.OfType<BackgroundImageCommand>(),
            Is.Empty,
            "a general block is not one of the elements the attribute maps onto");
    }

    [Test]
    public async Task AnAuthorBackgroundImageOutranksTheBackgroundAttribute()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div background='aqua.png' style='background-image:url(square.png)'>Ohai</div>" +
            "</body></html>");

        // The author speaks for a div through the property, which the hint never overrides; the one
        // layer that reaches the picture is the author's, not the attribute's.
        var layers = view.Paint!.Commands.OfType<BackgroundImageCommand>().ToList();
        Assert.That(layers, Has.Count.EqualTo(1), "only the author's background-image is drawn");
    }

    [Test]
    public async Task ThePictureNamedOnlyInTheTableAttributeIsFetched()
    {
        // The resolved style draws the attribute, but a picture only comes on screen if it is fetched -
        // and it is fetched only if the collector noticed it. A table's picture, named nowhere but the
        // attribute, has to reach the loader all the same.
        var loader = new RecordingImageLoader();
        using var view = new HutuViewInternal(new FakeTextMeasurer(), loader);
        view.SetViewportSize(200, 200);
        await view.SetHtmlDocumentAsync(
            "<html><body style='margin:0'>" +
            "<table background='aqua.png'><tr><td>cell</td></tr></table>" +
            "</body></html>",
            "file:///corpus/box.html");
        await view.PrefetchLocalImagesAsync(default);

        Assert.That(
            loader.Requested.Any(url => url.EndsWith("aqua.png", StringComparison.OrdinalIgnoreCase)),
            Is.True,
            "the attribute's picture is fetched as surely as a stylesheet's would be");
    }

    [Test]
    public async Task ThePictureOnADivIsNeverFetched()
    {
        // A div is not one of the elements the attribute maps onto, so its picture is neither drawn nor
        // worth fetching - fetching it would pull a file the page has no business showing.
        var loader = new RecordingImageLoader();
        using var view = new HutuViewInternal(new FakeTextMeasurer(), loader);
        view.SetViewportSize(200, 200);
        await view.SetHtmlDocumentAsync(
            "<html><body style='margin:0'><div background='aqua.png'>Ohai</div></body></html>",
            "file:///corpus/box.html");
        await view.PrefetchLocalImagesAsync(default);

        Assert.That(loader.Requested, Is.Empty, "a div's background attribute names no picture to fetch");
    }

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(200, 200);
        return view;
    }

    /// <summary>An image loader that only remembers the URLs it was handed, so a test can read them back.</summary>
    private sealed class RecordingImageLoader : IImageLoader
    {
        public List<String> Requested { get; } = new();

        public Task LoadAsync(IReadOnlyList<String> urls, ResourceFetcher fetch, CancellationToken cancellationToken)
        {
            Requested.AddRange(urls);
            return Task.CompletedTask;
        }

        public ImageSize? GetSize(String url) => null;
    }
}

using Hutu.Rendering.Media;
using Hutu.Skia;
using NUnit.Framework;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the image pipeline: decoding both formats, caching, and - most importantly - the promise
/// that a missing or malformed resource degrades instead of failing.
/// </summary>
[TestFixture]
public sealed class SvgImageTests
{
    private const String LandscapeUrl = "res:/Images/landscape.svg";
    private const String ShapesUrl = "res:/Images/color-shapes.svg";

    [Test]
    public async Task SvgDecodesToItsViewBoxSize()
    {
        using var loader = new SkiaImageLoader();

        await loader.LoadAsync(new[] { LandscapeUrl, ShapesUrl }, FromSampleFiles, CancellationToken.None);

        Assert.That(loader.GetError(LandscapeUrl), Is.Null);
        Assert.That(loader.GetSize(LandscapeUrl)?.Width, Is.EqualTo(960).Within(0.5));
        Assert.That(loader.GetSize(LandscapeUrl)?.Height, Is.EqualTo(420).Within(0.5));

        Assert.That(loader.GetError(ShapesUrl), Is.Null);
        Assert.That(loader.GetSize(ShapesUrl)?.Width, Is.EqualTo(640).Within(0.5));
        Assert.That(loader.GetSize(ShapesUrl)?.Height, Is.EqualTo(360).Within(0.5));
    }

    [Test]
    public async Task RasterImageDecodesToItsPixelSize()
    {
        var png = CreatePng(12, 7);
        using var loader = new SkiaImageLoader();
        var url = "res:/Images/generated.png";

        await loader.LoadAsync(
            new[] { url },
            (_, _) => Task.FromResult<Stream?>(new MemoryStream(png)),
            CancellationToken.None);

        Assert.That(loader.GetError(url), Is.Null);
        Assert.That(loader.GetSize(url)?.Width, Is.EqualTo(12).Within(0.5));
        Assert.That(loader.GetSize(url)?.Height, Is.EqualTo(7).Within(0.5));
    }

    [Test]
    public async Task LoadingTheSameUrlTwiceFetchesOnce()
    {
        using var loader = new SkiaImageLoader();
        var fetches = 0;

        ResourceFetcher counting = async (url, token) =>
        {
            fetches++;
            return await FromSampleFiles(url, token);
        };

        await loader.LoadAsync(new[] { LandscapeUrl }, counting, CancellationToken.None);
        await loader.LoadAsync(new[] { LandscapeUrl }, counting, CancellationToken.None);

        Assert.That(fetches, Is.EqualTo(1));
    }

    [Test]
    public async Task MissingResourceIsRecordedRatherThanThrown()
    {
        using var loader = new SkiaImageLoader();
        var url = "res:/Images/does-not-exist.svg";

        await loader.LoadAsync(new[] { url }, (_, _) => Task.FromResult<Stream?>(null), CancellationToken.None);

        Assert.That(loader.GetSize(url), Is.Null);
        Assert.That(loader.GetError(url), Is.Not.Null);
    }

    [Test]
    public async Task UndecodableDataIsRecordedRatherThanThrown()
    {
        using var loader = new SkiaImageLoader();
        var url = "res:/Images/broken.svg";

        await loader.LoadAsync(
            new[] { url },
            (_, _) => Task.FromResult<Stream?>(new MemoryStream(new Byte[] { 1, 2, 3, 4 })),
            CancellationToken.None);

        Assert.That(loader.GetSize(url), Is.Null);
        Assert.That(loader.GetError(url), Is.Not.Null);
    }

    [Test]
    public async Task PrefetchCollectsImageSourcesAndBackgroundUrls()
    {
        var loader = new RecordingImageLoader();
        using var view = new HutuViewInternal(new FakeTextMeasurer(), loader);
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><head><style>.panel{background-image:url('../Images/landscape.svg')}</style></head>" +
            "<body style='margin:0'><img src='Images/photo.png'><div class='panel' style='height:10px'></div></body></html>",
            "res:/Pages/images.htm");

        Assert.That(
            loader.Urls.Any(url => url.EndsWith("Images/photo.png", StringComparison.Ordinal)),
            Is.True,
            "the img src should be collected and resolved against the document base");
        Assert.That(
            loader.Urls.Any(url => url.EndsWith("Images/landscape.svg", StringComparison.Ordinal)),
            Is.True,
            "the background url from the stylesheet should be collected too");
    }

    [Test]
    public async Task DataUrlPictureLaysOutAtTheSizeItStates()
    {
        // A page that bakes a square placeholder into its markup is asking for a square box. Nothing
        // is fetched, so the only thing that can answer is the URL itself; a box sized to the
        // specification's default object size instead would be 2:1 and the page would jump the moment
        // the picture it stood in for arrived.
        using var images = new SkiaImageLoader();
        using var view = new HutuViewInternal(new FakeTextMeasurer(), images);
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<img src='data:image/svg+xml;utf-8," +
            "%3Csvg width=%2240%22 height=%2240%22 xmlns=%22http://www.w3.org/2000/svg%22%3E%3C/svg%3E'>" +
            "</body></html>");

        var image = view.Layout!.FindBox(view.Document!.QuerySelector("img")!);

        Assert.That(image, Is.Not.Null, "the data URL should decode into a box");
        Assert.That(image!.BorderBox.Width, Is.EqualTo(40).Within(0.5));
        Assert.That(image.BorderBox.Height, Is.EqualTo(40).Within(0.5));
    }

    [Test]
    public async Task Base64DataUrlPictureLaysOutAtItsPixelSize()
    {
        // Base64 is the spelling a bitmap needs, and the only one that can carry bytes that are not
        // text at all.
        var png = CreatePng(12, 7);
        using var images = new SkiaImageLoader();
        using var view = new HutuViewInternal(new FakeTextMeasurer(), images);
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'><img src='data:image/png;base64," +
            Convert.ToBase64String(png) +
            "'></body></html>");

        var image = view.Layout!.FindBox(view.Document!.QuerySelector("img")!);

        Assert.That(image, Is.Not.Null, "the base64 data URL should decode into a box");
        Assert.That(image!.BorderBox.Width, Is.EqualTo(12).Within(0.5));
        Assert.That(image.BorderBox.Height, Is.EqualTo(7).Within(0.5));
    }

    private static Byte[] CreatePng(Int32 width, Int32 height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Serves the sample folder by file name, which is how the URLs are keyed.</summary>
    private static Task<Stream?> FromSampleFiles(String url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = Path.Combine(SampleImagesDirectory, Path.GetFileName(url));

        return Task.FromResult<Stream?>(
            File.Exists(path) ? new MemoryStream(File.ReadAllBytes(path)) : null);
    }

    private static String SampleImagesDirectory => Path.Combine(
        Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..")),
        "samples",
        "Sample.Common",
        "Samples",
        "Images");

    private sealed class RecordingImageLoader : IImageLoader
    {
        public List<String> Urls { get; } = new();

        public ImageSize? GetSize(String url) => null;

        public Task LoadAsync(IReadOnlyList<String> urls, ResourceFetcher fetch, CancellationToken cancellationToken)
        {
            Urls.AddRange(urls);
            return Task.CompletedTask;
        }
    }
}

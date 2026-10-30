using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Io;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Hutu.Tests;

[TestFixture]
public sealed class HutuResourceLoaderTests
{
    [Test]
    public async Task PicturesAreSkippedAndOtherResourcesKeepTheirLoader()
    {
        using var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(response => response
            .Address("https://example.test/page.html")
            .Content(
                "<html><head><link rel='stylesheet' href='style.css'></head>" +
                "<body><img src='picture.png'><input type='image' src='button.png'></body></html>"));

        var inner = new RecordingLoader();
        var loader = new HutuResourceLoader(inner);
        var image = document.QuerySelector("img")!;
        var input = document.QuerySelector("input")!;
        var style = document.QuerySelector("link")!;

        var imageDownload = loader.FetchAsync(
            new ResourceRequest(image, new Url("https://example.test/picture.png")));
        var inputDownload = loader.FetchAsync(
            new ResourceRequest(input, new Url("https://example.test/button.png")));
        var styleDownload = loader.FetchAsync(
            new ResourceRequest(style, new Url("https://example.test/style.css")));

        Assert.Multiple(() =>
        {
            Assert.That(inner.Requests, Is.EqualTo(1), "only the stylesheet reaches the inner loader");
            Assert.That(imageDownload.IsCompleted, Is.True);
            Assert.That(inputDownload.IsCompleted, Is.True);
            Assert.That(styleDownload, Is.SameAs(inner.Last));
        });

        Assert.That(await imageDownload.Task, Is.Null);
        Assert.That(await inputDownload.Task, Is.Null);
    }

    private sealed class RecordingLoader : IResourceLoader
    {
        public Int32 Requests { get; private set; }

        public IDownload? Last { get; private set; }

        public IDownload FetchAsync(ResourceRequest request)
        {
            Requests++;
            Last = new EmptyDownload(request.Target, request.Source);
            return Last;
        }

        public IEnumerable<IDownload> GetDownloads() => Array.Empty<IDownload>();
    }

    private sealed class EmptyDownload : IDownload
    {
        public EmptyDownload(Url target, Object source)
        {
            Target = target;
            Source = source;
        }

        public Task<IResponse> Task { get; } = System.Threading.Tasks.Task.FromResult(default(IResponse)!);

        public Url Target { get; }

        public Object? Source { get; }

        public Boolean IsCompleted => true;

        public Boolean IsRunning => false;

        public void Cancel()
        {
        }
    }
}

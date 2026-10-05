using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers replaced-element layout: sizing from the intrinsic size, the width/height constraints,
/// and participation in line breaking.
/// </summary>
[TestFixture]
public sealed class ImageLayoutTests
{
    private const String ImageUrl = "res:/a.svg";

    [Test]
    public async Task ReplacedElementUsesItsIntrinsicSize()
    {
        using var view = CreateView(800, ImageUrl, 400, 200);

        await view.SetHtml("<html><body style='margin:0'><img src='res:/a.svg'></body></html>");

        var image = FindKind(view, LayoutKind.Image);

        Assert.That(image, Is.Not.Null);
        Assert.That(image!.BorderBox.Width, Is.EqualTo(400).Within(0.5));
        Assert.That(image.BorderBox.Height, Is.EqualTo(200).Within(0.5));
    }

    [Test]
    public async Task SpecifiedWidthAndMaxHeightWinOverTheIntrinsicSize()
    {
        // figure img { display: block; width: 100%; max-height: 320px; object-fit: cover }
        using var view = CreateView(800, ImageUrl, 960, 420);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<img src='res:/a.svg' style='display:block;width:900px;max-height:320px;object-fit:cover'>" +
            "</body></html>");

        var image = FindKind(view, LayoutKind.Image);

        Assert.That(image!.BorderBox.Width, Is.EqualTo(900).Within(0.5), "the specified width is kept");
        Assert.That(image.BorderBox.Height, Is.EqualTo(320).Within(0.5), "max-height clamps the derived height");
        Assert.That(image.ImageFit, Is.EqualTo(ObjectFit.Cover));
    }

    [Test]
    public async Task MinFunctionPicksTheSmallerArgument()
    {
        // .shape-image { width: min(100%, 640px) }
        using var view = CreateView(900, ImageUrl, 640, 360);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<img src='res:/a.svg' style='width:min(100%, 640px)'>" +
            "</body></html>");

        var image = FindKind(view, LayoutKind.Image);

        Assert.That(image!.BorderBox.Width, Is.EqualTo(640).Within(0.5), "the viewport is 900, so 640 wins");
        Assert.That(image.BorderBox.Height, Is.EqualTo(360).Within(0.5), "the height follows the aspect ratio");
    }

    [Test]
    public async Task InlineImageWrapsToTheNextLineWhenItDoesNotFit()
    {
        using var view = CreateView(30, ImageUrl, 20, 10);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0'>aaaa<img src='res:/a.svg'></p></body></html>");

        var lines = Walk(view.Layout!.Root).Where(node => node.Kind == LayoutKind.Line).ToList();

        Assert.That(lines, Has.Count.EqualTo(2), "the image cannot share the line with the four characters");
        Assert.That(lines[1].Children.Any(child => child.Kind == LayoutKind.Image), Is.True);
    }

    [Test]
    public async Task MissingIntrinsicSizeFallsBackToTheDefaultObjectSize()
    {
        // No provider entry for the URL, and no width/height attributes either.
        using var view = CreateView(800);

        await view.SetHtml("<html><body style='margin:0'><img src='res:/unknown.svg'></body></html>");

        var image = FindKind(view, LayoutKind.Image);

        Assert.That(image!.BorderBox.Width, Is.EqualTo(300).Within(0.5));
        Assert.That(image.BorderBox.Height, Is.EqualTo(150).Within(0.5));
    }

    private static HutuViewInternal CreateView(
        Int32 viewportWidth,
        String? url = null,
        Double intrinsicWidth = 0,
        Double intrinsicHeight = 0)
    {
        var images = new FakeImageProvider();

        if (url is not null)
        {
            images.Add(url, intrinsicWidth, intrinsicHeight);
        }

        var view = new HutuViewInternal(new FakeTextMeasurer(), images);
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static LayoutBox? FindKind(HutuViewInternal view, LayoutKind kind) =>
        view.Layout is null ? null : Walk(view.Layout.Root).FirstOrDefault(node => node.Kind == kind);

    private static IEnumerable<LayoutBox> Walk(LayoutBox node)
    {
        yield return node;

        foreach (var descendant in node.Children.SelectMany(Walk))
        {
            yield return descendant;
        }
    }

    /// <summary>Reports the intrinsic sizes the layout asked for, without fetching anything.</summary>
    private sealed class FakeImageProvider : IImageLoader
    {
        private readonly Dictionary<String, ImageSize> _sizes = new(StringComparer.Ordinal);

        public FakeImageProvider Add(String url, Double width, Double height)
        {
            _sizes[url] = new ImageSize(width, height);
            return this;
        }

        public ImageSize? GetSize(String url) => _sizes.TryGetValue(url, out var size) ? size : null;

        public Task LoadAsync(IReadOnlyList<String> urls, ResourceFetcher fetch, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}

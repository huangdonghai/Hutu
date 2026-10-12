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
/// Covers replaced-element layout: sizing from the intrinsic size and from the width/height
/// attributes, the constraints that outrank each of them, and participation in line breaking.
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
    public async Task TheAttributesSizeTheBoxRatherThanThePictureThatArrived()
    {
        // <img width="197" height="99" src="…250px…"> - the thumbnail a wiki ships: the bitmap is one
        // width, the page asked for another, and the attributes are where the asking is written down.
        using var view = CreateView(800, ImageUrl, 400, 200);

        await view.SetHtml(
            "<html><body style='margin:0'><img src='res:/a.svg' width='197' height='99'></body></html>");

        var image = FindKind(view, LayoutKind.Image);

        Assert.That(image!.BorderBox.Width, Is.EqualTo(197).Within(0.5), "the declared width, not the bitmap's 400");
        Assert.That(image.BorderBox.Height, Is.EqualTo(99).Within(0.5));
        Assert.That(image.Source.IntrinsicSize?.Width, Is.EqualTo(400).Within(0.5),
            "the picture keeps its own size, which is what an object-fit has to fit");
    }

    [Test]
    public async Task AuthorCssOverridesTheAttributesAndTheOtherAxisFollowsThePicture()
    {
        // A browser measured on a real page: with `width: 50%` declared and both attributes present, the
        // width comes from the sheet and the height stays what the attribute said - squashed, because
        // nothing asked for a proportional height.
        using var view = CreateView(800, ImageUrl, 400, 200);

        await view.SetHtml(
            "<html><body style='margin:0'><img src='res:/a.svg' width='100' height='50' style='width:200px'></body></html>");

        var image = FindKind(view, LayoutKind.Image);

        Assert.That(image!.BorderBox.Width, Is.EqualTo(200).Within(0.5), "the sheet outranks the attribute");
        Assert.That(image.BorderBox.Height, Is.EqualTo(50).Within(0.5), "the attribute still answers that axis");
    }

    [Test]
    public async Task ADeclaredAutoHeightFollowsThePictureRatherThanTheAttribute()
    {
        // `img { max-width: 100%; height: auto }`, the responsive idiom: the attribute that would have
        // pinned the height is taken back, and the ratio of the picture that arrived decides it.
        using var view = CreateView(800, ImageUrl, 400, 200);

        await view.SetHtml(
            "<html><body style='margin:0'><img src='res:/a.svg' width='100' height='50' style='width:200px;height:auto'></body></html>");

        var image = FindKind(view, LayoutKind.Image);

        Assert.That(image!.BorderBox.Width, Is.EqualTo(200).Within(0.5));
        Assert.That(image.BorderBox.Height, Is.EqualTo(100).Within(0.5), "200 at the picture's 2:1");
    }

    [Test]
    public async Task AMaxWidthViolationLeavesTheHeightTheAttributesSaid()
    {
        // Measured in a browser the same way: a box clamped on one axis and declared on the other does
        // not keep the picture's shape - only an `auto` height would have followed the clamp.
        using var view = CreateView(800, ImageUrl, 400, 200);

        await view.SetHtml(
            "<html><body style='margin:0'><img src='res:/a.svg' width='400' height='200' style='max-width:100px'></body></html>");

        var image = FindKind(view, LayoutKind.Image);

        Assert.That(image!.BorderBox.Width, Is.EqualTo(100).Within(0.5));
        Assert.That(image.BorderBox.Height, Is.EqualTo(200).Within(0.5));
    }

    [Test]
    public async Task HalfADeclarationLeavesTheOtherAxisToThePicture()
    {
        using var view = CreateView(800, ImageUrl, 400, 200);

        await view.SetHtml(
            "<html><body style='margin:0'><img src='res:/a.svg' width='100'></body></html>");

        var image = FindKind(view, LayoutKind.Image);

        Assert.That(image!.BorderBox.Width, Is.EqualTo(100).Within(0.5));
        Assert.That(image.BorderBox.Height, Is.EqualTo(50).Within(0.5), "the picture's 2:1, not the default object's");
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

using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using SkiaSharp;
using Svg.Skia;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hutu.Skia;

/// <summary>
/// Fetches, decodes and caches the images a document refers to.
/// </summary>
/// <remarks>
/// Raster formats go through SkiaSharp; SVG goes through Svg.Skia and is kept as an
/// <see cref="SKPicture"/> rather than being rasterised up front, so it stays crisp when the box it
/// is drawn into is larger than its intrinsic size.
/// <para>
/// Every failure degrades to "this image is not available" and is recorded rather than thrown: a
/// missing or malformed resource must not be able to fail a layout pass.
/// </para>
/// </remarks>
public sealed class SkiaImageLoader : IImageLoader, IDisposable
{
    private readonly ConcurrentDictionary<String, Entry> _entries = new(StringComparer.Ordinal);

    public ImageSize? GetSize(String url) =>
        _entries.TryGetValue(url, out var entry) ? entry.Size : null;

    /// <summary>Gets why an image is unavailable, or null when it loaded or was never requested.</summary>
    public String? GetError(String url) =>
        _entries.TryGetValue(url, out var entry) ? entry.Error : null;

    public async Task LoadAsync(IReadOnlyList<String> urls, ResourceFetcher fetch, CancellationToken cancellationToken)
    {
        foreach (var url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Already attempted - including the failed attempts, which must not be retried on every
            // relayout.
            if (_entries.ContainsKey(url))
            {
                continue;
            }

            _entries[url] = await LoadOneAsync(url, fetch, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Draws a loaded image into the given rectangle, fitted and clipped as requested. Does nothing
    /// when the image is not available, so a broken reference simply leaves the page background.
    /// </summary>
    public void Draw(SKCanvas canvas, String url, SKRect destination, ObjectFit fit, CornerRadii radii)
    {
        if (!_entries.TryGetValue(url, out var entry) || entry.Size is not { IsValid: true } intrinsic)
        {
            return;
        }

        if (destination.Width <= 0 || destination.Height <= 0)
        {
            return;
        }

        var placed = Placement(intrinsic, destination, fit);

        // Clipping to the destination is what crops the overflow that cover/none/scale-down let the
        // content have; radii that are all zero make it a plain rectangular clip.
        canvas.Save();
        SkiaRounded.Clip(canvas, destination, radii);

        if (entry.Image is not null)
        {
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawImage(entry.Image, placed, paint);
        }
        else if (entry.Picture is not null)
        {
            DrawPicture(canvas, entry.Picture, placed);
        }

        canvas.Restore();
    }

    /// <summary>
    /// Draws a background layer. A background places and tiles its image by its own rules - size,
    /// position and repeat - which is a different model from <c>object-fit</c>, so this does not go
    /// through <see cref="Draw"/>.
    /// </summary>
    public void DrawBackground(SKCanvas canvas, BackgroundLayer layer, SKRect area, CornerRadii radii)
    {
        if (layer.Url is not { Length: > 0 } url)
        {
            return;
        }

        if (!_entries.TryGetValue(url, out var entry) || entry.Size is not { IsValid: true } intrinsic)
        {
            return;
        }

        if (area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        var tile = TileSize(layer, intrinsic, area);

        if (tile.Width <= 0 || tile.Height <= 0)
        {
            return;
        }

        var left = area.Left + (Single)layer.PositionX.Resolve(area.Width - tile.Width);
        var top = area.Top + (Single)layer.PositionY.Resolve(area.Height - tile.Height);

        canvas.Save();
        SkiaRounded.Clip(canvas, area, radii);

        if (layer.Repeat)
        {
            var startX = Start(left, area.Left, tile.Width);
            var startY = Start(top, area.Top, tile.Height);

            for (var y = startY; y < area.Bottom; y += tile.Height)
            {
                for (var x = startX; x < area.Right; x += tile.Width)
                {
                    DrawTile(canvas, entry, new SKRect(x, y, x + tile.Width, y + tile.Height));
                }
            }
        }
        else
        {
            DrawTile(canvas, entry, new SKRect(left, top, left + tile.Width, top + tile.Height));
        }

        canvas.Restore();
    }

    /// <summary>Gets the first tile at or before the start of the area, so the whole area is covered.</summary>
    private static Single Start(Single position, Single areaStart, Single size) =>
        areaStart + ((position - areaStart) % size + size) % size - size;

    private static SKSize TileSize(BackgroundLayer layer, ImageSize intrinsic, SKRect area)
    {
        var width = (Single)intrinsic.Width;
        var height = (Single)intrinsic.Height;

        switch (layer.SizeKind)
        {
            case BackgroundLayerSizeKind.Cover:
            {
                var scale = Math.Max(area.Width / width, area.Height / height);
                return new SKSize(width * scale, height * scale);
            }

            case BackgroundLayerSizeKind.Contain:
            {
                var scale = Math.Min(area.Width / width, area.Height / height);
                return new SKSize(width * scale, height * scale);
            }

            case BackgroundLayerSizeKind.Explicit:
            {
                var explicitWidth = (Single)layer.SizeWidth.Resolve(area.Width);
                var explicitHeight = (Single)layer.SizeHeight.Resolve(area.Height);

                if (explicitWidth <= 0 && explicitHeight <= 0)
                {
                    break;
                }

                if (explicitWidth <= 0)
                {
                    explicitWidth = explicitHeight * (Single)intrinsic.AspectRatio;
                }
                else if (explicitHeight <= 0)
                {
                    explicitHeight = explicitWidth / (Single)intrinsic.AspectRatio;
                }

                return new SKSize(explicitWidth, explicitHeight);
            }
        }

        return new SKSize(width, height);
    }

    private static void DrawTile(SKCanvas canvas, Entry entry, SKRect target)
    {
        if (entry.Image is not null)
        {
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawImage(entry.Image, target, paint);
        }
        else if (entry.Picture is not null)
        {
            DrawPicture(canvas, entry.Picture, target);
        }
    }

    /// <summary>Gets where the whole image content lands, before the destination clips it.</summary>
    private static SKRect Placement(ImageSize intrinsic, SKRect destination, ObjectFit fit)
    {
        if (fit == ObjectFit.Fill)
        {
            return destination;
        }

        var width = (Single)intrinsic.Width;
        var height = (Single)intrinsic.Height;

        var scaleX = destination.Width / width;
        var scaleY = destination.Height / height;

        var scale = fit switch
        {
            ObjectFit.Contain => Math.Min(scaleX, scaleY),
            ObjectFit.Cover => Math.Max(scaleX, scaleY),
            ObjectFit.None => 1f,
            _ => Math.Min(1f, Math.Min(scaleX, scaleY)),
        };

        var scaledWidth = width * scale;
        var scaledHeight = height * scale;
        var left = destination.Left + (destination.Width - scaledWidth) / 2;
        var top = destination.Top + (destination.Height - scaledHeight) / 2;

        return new SKRect(left, top, left + scaledWidth, top + scaledHeight);
    }

    /// <summary>
    /// Places a vector picture by mapping its own coordinate space onto the target rectangle, so the
    /// SVG is re-rasterised at the final size instead of being scaled as a bitmap.
    /// </summary>
    private static void DrawPicture(SKCanvas canvas, SKPicture picture, SKRect target)
    {
        var bounds = picture.CullRect;

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        canvas.Save();
        canvas.Translate(target.Left, target.Top);
        canvas.Scale(target.Width / bounds.Width, target.Height / bounds.Height);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);
        canvas.Restore();
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Dispose();
        }

        _entries.Clear();
    }

    private static async Task<Entry> LoadOneAsync(String url, ResourceFetcher fetch, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await fetch(url, cancellationToken).ConfigureAwait(false);

            if (stream is null)
            {
                return Entry.Failed("the resource could not be fetched");
            }

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

            return Decode(buffer.ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Entry.Failed(exception.Message);
        }
    }

    private static Entry Decode(Byte[] data)
    {
        if (data.Length == 0)
        {
            return Entry.Failed("the resource is empty");
        }

        return LooksLikeSvg(data) ? DecodeVector(data) : DecodeRaster(data);
    }

    private static Entry DecodeRaster(Byte[] data)
    {
        using var bitmap = SKBitmap.Decode(data);

        if (bitmap is null)
        {
            return Entry.Failed("the image data could not be decoded");
        }

        return Entry.FromRaster(SKImage.FromBitmap(bitmap), new ImageSize(bitmap.Width, bitmap.Height));
    }

    private static Entry DecodeVector(Byte[] data)
    {
        // The picture is drawn on demand, so the SKSvg that owns it has to outlive this method.
        var svg = new SKSvg();

        try
        {
            using var stream = new MemoryStream(data);

            if (svg.Load(stream) is null || svg.Picture is not { } picture)
            {
                svg.Dispose();
                return Entry.Failed("the SVG could not be parsed");
            }

            var bounds = picture.CullRect;
            return Entry.FromVector(svg, picture, new ImageSize(bounds.Width, bounds.Height));
        }
        catch (Exception exception)
        {
            svg.Dispose();
            return Entry.Failed(exception.Message);
        }
    }

    /// <summary>
    /// Gets whether the payload looks like SVG. The bytes are checked rather than the content type,
    /// because a document can be served from a scheme that carries no headers at all.
    /// </summary>
    private static Boolean LooksLikeSvg(Byte[] data)
    {
        var limit = Math.Min(data.Length, 512);
        var head = Encoding.UTF8.GetString(data, 0, limit);
        return head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Entry : IDisposable
    {
        private readonly SKSvg? _svg;

        private Entry(ImageSize? size, SKImage? image, SKPicture? picture, SKSvg? svg, String? error)
        {
            Size = size;
            Image = image;
            Picture = picture;
            _svg = svg;
            Error = error;
        }

        public ImageSize? Size { get; }

        public SKImage? Image { get; }

        public SKPicture? Picture { get; }

        public String? Error { get; }

        public static Entry Failed(String error) => new(null, null, null, null, error);

        public static Entry FromRaster(SKImage image, ImageSize size) => new(size, image, null, null, null);

        public static Entry FromVector(SKSvg svg, SKPicture picture, ImageSize size) =>
            new(size, null, picture, svg, null);

        public void Dispose()
        {
            Image?.Dispose();
            Picture?.Dispose();
            _svg?.Dispose();
        }
    }
}

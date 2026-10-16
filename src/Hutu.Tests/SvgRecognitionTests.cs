using Hutu.Skia;
using NUnit.Framework;
using System;
using System.Text;

namespace Hutu.Tests;

/// <summary>
/// What the image loader makes of the payloads it is handed: a document that opens with markup is
/// vector art however far in the element sits, and a raster is a raster.
/// </summary>
[TestFixture]
public sealed class SvgRecognitionTests
{
    [Test]
    public void APayloadThatOpensWithMarkupIsVector()
    {
        Assert.That(SkiaImageLoader.LooksLikeSvg(Bytes("<svg xmlns='...'/>")), Is.True, "a bare element");
        Assert.That(SkiaImageLoader.LooksLikeSvg(Bytes("\uFEFF  \n<svg/>")), Is.True, "after a byte order mark and space");
        Assert.That(SkiaImageLoader.LooksLikeSvg(Bytes("<?xml version='1.0'?><svg/>")), Is.True, "after a declaration");
    }

    [Test]
    public void AVectorElementPastTheOldWindowIsStillVector()
    {
        // Wikipedia's globe is 165KB and its doctype alone puts <svg> past 130 bytes - a payload that
        // was read as a bitmap and failed to decode with a null codec, so the picture never drew.
        var prolog = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\">" +
            new String(' ', 600) + "<svg xmlns='...'/>";

        Assert.That(SkiaImageLoader.LooksLikeSvg(Bytes(prolog)), Is.True);
    }

    [Test]
    public void ARasterIsNotVector()
    {
        Assert.That(SkiaImageLoader.LooksLikeSvg(new Byte[] { 0x89, 0x50, 0x4E, 0x47 }), Is.False, "PNG");
        Assert.That(SkiaImageLoader.LooksLikeSvg(new Byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }), Is.False, "JPEG");
        Assert.That(SkiaImageLoader.LooksLikeSvg(Bytes("GIF89a")), Is.False, "GIF");
        Assert.That(SkiaImageLoader.LooksLikeSvg(Array.Empty<Byte>()), Is.False, "nothing at all");
    }

    private static Byte[] Bytes(String text) => Encoding.UTF8.GetBytes(text);
}

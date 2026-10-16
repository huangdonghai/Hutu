using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using Hutu.Skia;
using NUnit.Framework;
using SkiaSharp;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The WOFF 2 decoder, against a real WOFF 2 file kept in the project.
/// </summary>
/// <remarks>
/// What is being tested is not that the bytes come out identical to some original - §5.1 says plainly
/// that a reconstruction need not match the input bit for bit, since the encoder may have packed
/// coordinates differently and a decoder is free to pack them again some other way. What has to come
/// out is a font that loads and answers for the same characters, which is what a user agent is
/// actually owed.
/// </remarks>
[TestFixture]
public sealed class Woff2Tests
{
    [Test]
    public void AWoff2FileComesBackAsAFontFile()
    {
        var packed = Fixture();

        var sfnt = Woff2Decoder.ToSfnt(packed);

        Assert.That(sfnt, Is.Not.Null, "the file decodes");

        var version = (UInt32)((sfnt![0] << 24) | (sfnt[1] << 16) | (sfnt[2] << 8) | sfnt[3]);

        Assert.Multiple(() =>
        {
            Assert.That(version, Is.EqualTo(0x00010000u).Or.EqualTo(0x4F54544Fu),
                "what comes out is an SFNT file: either TrueType outlines or CFF ones");
            Assert.That(sfnt.Length, Is.GreaterThan(packed.Length),
                "and it is larger than what went in - the point of the format is that it packs");
        });
    }

    [Test]
    public void TheFontItComesBackAsLoadsAndAnswersForCharacters()
    {
        var sfnt = Woff2Decoder.ToSfnt(Fixture());

        using var face = SKTypeface.FromStream(new MemoryStream(sfnt!));

        Assert.That(face, Is.Not.Null, "the reconstructed file is one the backend can open");

        var mapped = 0;

        for (var code = 0x20; code < 0x3000; code++)
        {
            if (face!.GetGlyph(code) != 0)
            {
                mapped++;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(face.GlyphCount, Is.GreaterThan(1), "the outlines came back");
            Assert.That(mapped, Is.GreaterThan(0), "and so did the character map that reaches them");
        });
    }

    [Test]
    public async Task AFontAPageLinksToIsFetchedAndAnswersToItsName()
    {
        // The font rides inside the page as a data URL, so this is a fetch that needs no network -
        // and the name it is given is one no machine has installed, which is the whole point.
        var packed = Convert.ToBase64String(Fixture());

        using var measurer = new SkiaTextMeasurer();
        var fonts = new SkiaFontLoader(measurer);
        using var view = new HutuViewInternal(measurer, null, fonts);
        view.SetViewportSize(600, 400);

        await view.SetHtml(
            "<html><head><style>" +
            "@font-face { font-family: Icon; src: url(data:font/woff2;base64," + packed + ") format(\"woff2\") }" +
            "p { font-family: Icon }" +
            "</style></head><body style='margin:0'><p>x</p></body></html>");

        Assert.That(
            measurer.Fonts.Holds("Icon"),
            Is.True,
            "the face was fetched, unpacked and registered under the name the page gave it");

        var resolved = measurer.Fonts.Resolve(new FontStyle("Icon", 16d));

        Assert.That(
            resolved.FamilyName,
            Is.EqualTo("Roboto"),
            "and the family answers with it afterwards - the answer the first layout cached has to go, " +
            "or the relayout would be handed the same wrong face again");
    }

    [Test]
    public async Task TheFamilyIsAskedForBeforeTheFontArrives()
    {
        // The order the first layout asks in, made to happen on purpose: the family is resolved while
        // the font is still on the wire, so the answer it caches is a system font's - and that cached
        // answer is what a registration has to displace.
        var packed = Convert.ToBase64String(Fixture());

        using var measurer = new SkiaTextMeasurer();
        var fonts = new SkiaFontLoader(measurer);

        var before = measurer.Fonts.Resolve(new FontStyle("Icon", 16d));

        Assert.That(before.FamilyName, Is.Not.EqualTo("Roboto"), "no machine has a font called after this page");

        using var view = new HutuViewInternal(measurer, null, fonts);
        view.SetViewportSize(600, 400);

        await view.SetHtml(
            "<html><head><style>" +
            "@font-face { font-family: Icon; src: url(data:font/woff2;base64," + packed + ") format(\"woff2\") }" +
            "</style></head><body style='margin:0'><p>x</p></body></html>");

        Assert.That(
            measurer.Fonts.Resolve(new FontStyle("Icon", 16d)).FamilyName,
            Is.EqualTo("Roboto"),
            "the face that arrived displaced what was cached for that family");
    }

    [Test]
    public void APrivateUseCharacterIsNotHandedToAnotherFamily()
    {
        using var measurer = new SkiaTextMeasurer();
        var style = new FontStyle("Icon", 16d);

        Assert.That(
            measurer.Fonts.ResolveFallback(style, 0xE701),
            Is.Null,
            "CSS Fonts 3 §5.4: what a private use area holds is the page's to decide, so the system " +
            "is not asked for a stand-in to draw in its place");
    }

    [Test]
    public void AFileThatIsNotWoff2IsRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Woff2Decoder.ToSfnt(Array.Empty<Byte>()), Is.Null, "too short to be a header");
            Assert.That(Woff2Decoder.ToSfnt(new Byte[64]), Is.Null, "no signature");
        });
    }

    private static Byte[] Fixture()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Hutu.Tests.Fonts.roboto.woff2");

        Assert.That(stream, Is.Not.Null, "the WOFF 2 fixture is embedded in the test assembly");

        using var sink = new MemoryStream();
        stream!.CopyTo(sink);
        return sink.ToArray();
    }
}

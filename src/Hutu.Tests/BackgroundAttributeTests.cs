using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Linq;
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

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(200, 200);
        return view;
    }
}

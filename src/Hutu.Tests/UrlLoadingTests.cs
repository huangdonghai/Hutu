using AngleSharp.Dom;
using Hutu.Skia;
using NUnit.Framework;
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers loading a document by URL - from disk and over plain HTTP - and the CSS constructs a real
/// page tends to carry, some of which used to stop the render tree from being built at all.
/// </summary>
[TestFixture]
public sealed class UrlLoadingTests
{
    [Test]
    public async Task ALocalDocumentLoadsThroughAFileUrl()
    {
        var directory = Directory.CreateTempSubdirectory("hutu-file-url-");

        try
        {
            var path = Path.Combine(directory.FullName, "index.htm");
            await File.WriteAllTextAsync(path, "<html><body style='margin:0'><p>from disk</p></body></html>");

            using var view = CreateView();
            await view.LoadUrlAsync(new Uri(path).AbsoluteUri);

            Assert.That(view.GetHtml(), Does.Contain("from disk"));
            Assert.That(view.RenderTree, Is.Not.Null);
            Assert.That(view.Layout, Is.Not.Null);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task ALocalDocumentPullsInItsStyleSheetOverAFileUrl()
    {
        var directory = Directory.CreateTempSubdirectory("hutu-file-url-");

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory.FullName, "site.css"),
                "#target { width: 120px; }");

            var path = Path.Combine(directory.FullName, "index.htm");
            await File.WriteAllTextAsync(
                path,
                "<html><head><link rel='stylesheet' href='site.css'></head>" +
                "<body style='margin:0'><div id='target'>x</div></body></html>");

            using var view = CreateView();
            await view.LoadUrlAsync(new Uri(path).AbsoluteUri);

            var element = view.Document!.GetElementById("target");

            Assert.That(element, Is.Not.Null);
            Assert.That(
                view.GetComputedStyle(element!).GetPropertyValue("width").Trim(),
                Is.EqualTo("120px"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task AnHttpDocumentIsFetchedAndLaidOut()
    {
        const int port = 38472;

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var bytes = Encoding.UTF8.GetBytes("<html><body style='margin:0'><p>from http</p></body></html>");
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });

        try
        {
            using var view = CreateView();
            await view.LoadUrlAsync($"http://127.0.0.1:{port}/index.htm");

            Assert.That(view.GetHtml(), Does.Contain("from http"));
            Assert.That(view.Layout, Is.Not.Null);
        }
        finally
        {
            listener.Stop();

            try
            {
                await server;
            }
            catch
            {
                // The client end of the exchange is what this test is about.
            }
        }
    }

    /// <summary>
    /// The line height of the font shorthand is a bare number, and a bare number is a factor of the
    /// font size rather than a length. Treating it as a length used to throw while computing styles,
    /// which took the whole render tree down with it.
    /// </summary>
    [Test]
    public async Task AUnitlessLineHeightInTheFontShorthandSurvivesAsAFactor()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0;font:16px/1.6 sans-serif'><p>x</p></body></html>");

        var body = view.Document!.Body!;

        Assert.That(
            view.GetComputedStyle(body).GetPropertyValue("line-height").Trim(),
            Is.EqualTo("1.6"));
        Assert.That(view.RenderTree, Is.Not.Null);
    }

    /// <summary>
    /// The ex and ch units can be parsed but had no pixel equivalent, so a page using either of them
    /// failed the same way.
    /// </summary>
    [Test]
    public async Task ExAndChLengthsAreMeasuredInsteadOfRejected()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='ex' style='width:10ex'>x</div>" +
            "<div id='ch' style='width:10ch'>x</div>" +
            "</body></html>");

        var ex = view.Document!.GetElementById("ex");
        var ch = view.Document.GetElementById("ch");

        Assert.That(view.GetComputedStyle(ex!).GetPropertyValue("width").Trim(), Does.EndWith("px"));
        Assert.That(view.GetComputedStyle(ch!).GetPropertyValue("width").Trim(), Does.EndWith("px"));
        Assert.That(view.RenderTree, Is.Not.Null);
    }

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);
        return view;
    }
}

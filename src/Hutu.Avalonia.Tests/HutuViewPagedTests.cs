using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media.Imaging;
using Hutu.Rendering;
using NUnit.Framework;
using System.IO;
using System.Threading.Tasks;

namespace Hutu.Avalonia.Tests;

/// <summary>
/// The paged mode: the document is shown as the pages it prints on, one sheet at a time, and the
/// reader moves between them.
/// </summary>
[TestFixture]
public sealed class HutuViewPagedTests
{
    private const string ThreeBoxes =
        "<style>@page { size: 200px 150px; margin: 10px } body { margin: 0 }" +
        " #a { height: 120px; background: red }" +
        " #b { height: 120px; background: lime }" +
        " #c { height: 120px; background: blue }</style>" +
        "<div id=\"a\"></div><div id=\"b\"></div><div id=\"c\"></div>";

    [AvaloniaTest]
    public async Task ThePagedViewShowsOnePageAtATime()
    {
        var view = new HutuView();
        var window = new Window { Content = view, Width = 800, Height = 600 };

        window.Show();

        await view.SetHtml(ThreeBoxes);

        Assert.That(view.IsPaged, Is.False, "a document is one scrolling page until paging is asked for");

        var changes = 0;
        view.PagesChanged += () => changes++;

        view.SetPaged(true);

        Assert.Multiple(() =>
        {
            Assert.That(view.IsPaged, Is.True);
            Assert.That(view.PageCount, Is.EqualTo(3), "one box to a page");
            Assert.That(view.CurrentPage, Is.EqualTo(1), "the first page is the one shown");
            Assert.That(changes, Is.EqualTo(1), "the controls are told the pages changed");
        });

        var first = Frame(window);

        view.NextPage();
        Assert.That(view.CurrentPage, Is.EqualTo(2), "the next sheet stands after the first");

        var second = Frame(window);
        Assert.That(second, Is.Not.EqualTo(first), "a different sheet is drawn");

        view.PreviousPage();
        Assert.That(view.CurrentPage, Is.EqualTo(1));

        view.ShowPage(99);
        Assert.That(view.CurrentPage, Is.EqualTo(view.PageCount), "a page past the end is kept at the last one");

        view.SetPaged(false);

        Assert.Multiple(() =>
        {
            Assert.That(view.IsPaged, Is.False);
            Assert.That(view.CurrentPage, Is.EqualTo(0), "the whole document is shown again");
            Assert.That(view.PageCount, Is.EqualTo(0));
        });
    }

    /// <summary>Gets the window as a picture, so two pages can be told apart.</summary>
    private static Byte[] Frame(Window window)
    {
        using var frame = window.CaptureRenderedFrame();

        Assert.That(frame, Is.Not.Null, "no frame was produced");

        using var stream = new MemoryStream();

        frame!.Save(stream);

        return stream.ToArray();
    }
}

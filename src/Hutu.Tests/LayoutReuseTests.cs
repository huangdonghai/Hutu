using AngleSharp.Dom;
using Hutu.Rendering;
using Hutu.Rendering.Interaction;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using Hutu.Skia;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Hutu.Tests;

/// <summary>
/// A rebuild that only changes how a document is painted keeps the layout it already has, so a pointer
/// moving over it does not lay the document out again. Anything that could move a box must still force
/// a fresh layout.
/// </summary>
[TestFixture]
public sealed class LayoutReuseTests
{
    [Test]
    public void HoverThatOnlyRepaintsKeepsTheLayout()
    {
        using var session = Session.Create(
            "<style>a{color:#111111}a:hover{color:#ff0000;background:#ffeeee;border-color:#ff0000}</style>" +
            "<div style='font-size:32px'><a href='x.htm'>hover me please</a></div>");

        var anchor = session.Document.QuerySelector("a")!;
        var color = session.Color(anchor);
        var layout = session.View.Layout;
        var paint = session.View.Paint;

        Assert.That(Backgrounds(session), Is.Empty, "the link has no background until it is hovered");

        var moved = session.Hover(anchor);

        Assert.That(moved.Changed, Is.True, "the pointer should have entered the link");
        Assert.That(session.View.Layout, Is.SameAs(layout), "nothing moved, so the layout should be reused");
        Assert.That(session.View.Paint, Is.Not.SameAs(paint), "the new colours still need a repaint");
        Assert.That(session.Color(anchor), Is.Not.EqualTo(color), "the hover style should have applied");

        // The layout was reused, so the box the background lands on is one the reused layout already
        // held: an inline element has to be given a box whether or not it was drawing anything at the
        // time, or a background declared by a later style would have nowhere to go.
        Assert.That(
            Backgrounds(session),
            Is.EqualTo(new[] { 0xFFFFEEEEu }),
            "the background the hover added has to reach the paint list");
    }

    /// <summary>Gets the colours of the background fills the paint list was built with.</summary>
    private static IEnumerable<UInt32> Backgrounds(Session session) =>
        session.View.Paint!.Commands.OfType<FillCommand>().Select(fill => fill.Color);

    [Test]
    public void HoverThatChangesGeometryLaysOutAgain()
    {
        using var session = Session.Create(
            "<style>a{font-size:16px}a:hover{font-size:40px}</style>" +
            "<div><a href='x.htm'>hover me please</a></div>");

        var anchor = session.Document.QuerySelector("a")!;
        var layout = session.View.Layout;
        var width = session.Bounds(anchor).Width;

        var moved = session.Hover(anchor);

        Assert.That(moved.Changed, Is.True);
        Assert.That(session.View.Layout, Is.Not.SameAs(layout), "the text grew, so it has to be laid out again");
        Assert.That(session.Bounds(anchor).Width, Is.GreaterThan(width));
    }

    [Test]
    public void HoverThatChangesAnInheritedGeometryPropertyLaysOutAgain()
    {
        using var session = Session.Create(
            "<style>.box{font-size:16px}.box:hover{font-size:40px}</style>" +
            "<div class='box'><a href='x.htm'>hover me please</a></div>");

        var anchor = session.Document.QuerySelector("a")!;
        var layout = session.View.Layout;

        var moved = session.Hover(anchor);

        Assert.That(moved.Changed, Is.True);
        Assert.That(session.View.Layout, Is.Not.SameAs(layout), "the inherited font size grew, so geometry changed");
    }

    [Test]
    public void HoverThatChangesACollapsedBorderColourLaysOutAgain()
    {
        using var session = Session.Create(
            "<style>table{border-collapse:collapse;border:2px solid #000000;width:240px}" +
            "td{padding:16px}td:hover{border-color:#00ff00}</style>" +
            "<table><tr><td>hover me please</td></tr></table>");

        var cell = session.Document.QuerySelector("td")!;
        var layout = session.View.Layout;

        var moved = session.Hover(cell);

        Assert.That(moved.Changed, Is.True);
        Assert.That(
            session.View.Layout,
            Is.Not.SameAs(layout),
            "a collapsed border colour is cached on the box, so it cannot be picked up by repainting alone");
    }

    [Test]
    public void ResizingTheViewportLaysOutAgain()
    {
        using var session = Session.Create(
            "<style>div{width:50%}</style><div><a href='x.htm'>hover me please</a></div>");

        var anchor = session.Document.QuerySelector("a")!;
        var layout = session.View.Layout;

        // The styles are unchanged, but a percentage width resolves against the new viewport.
        Assert.That(session.View.SetViewportSize(400, 300), Is.True);

        Assert.That(session.View.Layout, Is.Not.SameAs(layout), "the initial containing block changed");
        Assert.That(session.HasBox(anchor), Is.True);
    }

    private sealed class Session : IDisposable
    {
        private readonly SkiaTextMeasurer _measurer;

        private Session(SkiaTextMeasurer measurer, HutuViewInternal view, IDocument document)
        {
            _measurer = measurer;
            View = view;
            Document = document;
        }

        public HutuViewInternal View { get; }

        public IDocument Document { get; }

        public static Session Create(String html)
        {
            var measurer = new SkiaTextMeasurer();
            var view = new HutuViewInternal(measurer);
            view.SetViewportSize(600, 400);

            var document = view.SetHtml(html, "https://example.test/index.htm").GetAwaiter().GetResult();
            return new Session(measurer, view, document);
        }

        public PointerInfo Hover(IElement element)
        {
            var bounds = Bounds(element);
            return View.PointerMoved(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
        }

        public Rect Bounds(IElement element)
        {
            var box = Find(element);
            Assert.That(box, Is.Not.Null, $"<{element.NodeName}> should have been laid out");
            return box!.BorderBox;
        }

        public Boolean HasBox(IElement element) => Find(element) is not null;

        public String Color(IElement element) => View.GetComputedStyle(element).GetPropertyValue("color");

        private LayoutBox? Find(IElement element)
        {
            var boxes = new List<LayoutBox>();
            Walk(View.Layout!.Root, boxes);

            foreach (var box in boxes)
            {
                var owner = ElementOf(box);

                if (owner is not null && (ReferenceEquals(owner, element) || owner.IsDescendantOf(element)))
                {
                    return box;
                }
            }

            return null;
        }

        private static IElement? ElementOf(LayoutBox box) =>
            box.Source.Element ?? box.Source.Source?.ParentElement;

        private static void Walk(LayoutBox box, List<LayoutBox> into)
        {
            into.Add(box);

            foreach (var child in box.Children)
            {
                Walk(child, into);
            }
        }

        public void Dispose()
        {
            View.Dispose();
            _measurer.Dispose();
        }
    }
}

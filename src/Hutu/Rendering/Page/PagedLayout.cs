using Hutu.Rendering.Layout;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering;

/// <summary>
/// One page of a paged document: where its content stands in the document's own coordinates, and the
/// page box that content is shown in.
/// </summary>
/// <remarks>
/// The content is a slice of the single tall layout rather than a tree laid out again. A cut is only
/// ever made where the flow allows one, so a slice never ends inside a line; what a page shows is the
/// stretch of the document from <see cref="Content"/>.<see cref="Rect.Y"/> down to its bottom, moved
/// to the page area's top edge.
/// </remarks>
/// <param name="Number">The page's number, counting from one.</param>
/// <param name="Geometry">The page box the slice is shown in.</param>
/// <param name="Content">The slice's rectangle in document coordinates, whose height is the room the page uses.</param>
/// <param name="PageName">The name the content at the head of the page states, or null for the unnamed page.</param>
/// <param name="Blank">Whether the page holds no content: one inserted so a left or right page begins on
/// the side it asks for (CSS Page 3 §3.2).</param>
/// <param name="Lead">
/// The distance the slice's content is pushed from the page area's start because something else
/// stands there - a table's repeated header, which a fragment below the first one carries at its top
/// (CSS Break 3 §5.2). The room it takes is left out of the page's content extent, so the slice and
/// the lead together fill the page area.
/// </param>
/// <param name="Trail">
/// The distance at the end of the page area another piece keeps from the slice - a table's repeated
/// footer, which stands at the bottom of every fragment that continues the table.
/// </param>
/// <param name="Repeats">
/// The pieces of the document standing on this page besides its own slice: a table's header drawn
/// again at the top of every fragment that continues the table, and its footer at the bottom.
/// </param>
public readonly record struct PageSlice(
    Int32 Number,
    PageGeometry Geometry,
    Rect Content,
    String? PageName = null,
    Boolean Blank = false,
    IReadOnlyList<PageMarginBand>? MarginBoxes = null,
    Double Lead = 0,
    Double Trail = 0,
    IReadOnlyList<PageRepeat>? Repeats = null)
{
    /// <summary>Gets where the document's own origin lands on the page, along the inline axis.</summary>
    public Double OriginX => Geometry.Margins.Left - Content.X;

    /// <summary>Gets where the slice's top lands on the page, below the top margin.</summary>
    public Double OriginY => Geometry.Margins.Top - Content.Y;
}

/// <summary>
/// A piece of the document drawn on a page besides its own slice: the rectangle it takes in the
/// document (<paramref name="Source"/>) and the rectangle it is drawn in on the page
/// (<paramref name="Place"/>).
/// </summary>
public readonly record struct PageRepeat(Rect Source, Rect Place);

/// <summary>
/// The pages a document comes to when it is printed: the page box every page shares (for now) and the
/// slices of the one tall layout each page shows.
/// </summary>
public sealed class PagedLayout
{
    internal PagedLayout(PageGeometry page, IReadOnlyList<PageSlice> pages, Double documentHeight)
    {
        Page = page;
        Pages = pages;
        DocumentHeight = documentHeight;
    }

    /// <summary>Gets the page box the slices are shown in.</summary>
    public PageGeometry Page { get; }

    /// <summary>Gets the pages, in order.</summary>
    public IReadOnlyList<PageSlice> Pages { get; }

    /// <summary>Gets the height of the tall document the slices were cut from.</summary>
    public Double DocumentHeight { get; }

    /// <summary>Gets how many pages the document comes to.</summary>
    public Int32 Count => Pages.Count;
}

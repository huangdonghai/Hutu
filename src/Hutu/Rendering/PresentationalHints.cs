using AngleSharp.Css.Values;
using AngleSharp.Dom;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Hutu.Rendering;

/// <summary>
/// Reads the declarations the obsolete attributes of HTML hint at.
/// </summary>
/// <remarks>
/// <para>
/// HTML 2.3.6 treats these attributes as presentational hints: each is read as a declaration <em>on the
/// element the attribute stands on</em>, in the author origin's zero-specificity slot, and nothing of it
/// is inherited. Two things follow from that placement, and Chrome answers both - a rule the page writes
/// takes the element back, even a <c>* { ... }</c> one, and a coloured row does not colour the cell whose
/// own <c>bgcolor</c> says otherwise.
/// </para>
/// <para>
/// The value each attribute holds is read the way HTML reads one, which is nowhere like CSS: a number of
/// pixels taken from the front of the string, so that <c>height="50px"</c> is 50 pixels and
/// <c>cellpadding="12.9"</c> is 12; a trailing percent sign that makes a share of the box the element is
/// sized against, except where the attribute rejects it; and a string that names no number at all, which
/// one attribute takes for nothing and another for its smallest value. <see cref="LegacyColor"/> holds
/// the colours, <see cref="LeadingNumber"/> the numbers.
/// </para>
/// <para>
/// A hint is a declaration the page is taken to have written, so it loses to every rule the page did
/// write - and the engine's own sheet is one of those rules. That is why the defaults the sheet used to
/// give a table - the room a cell keeps, the distance between its cells - are supplied here as fallbacks
/// instead: a declaration in the sheet would be one the hint could never take back. Chrome keeps the two
/// apart the same way, by giving the sheet's cell padding only to a table that writes no attribute.
/// </para>
/// </remarks>
internal static class PresentationalHints
{
    /// <summary>The room HTML leaves between a cell's content and its border when nothing says otherwise.</summary>
    private const Double DefaultCellPadding = 1;

    /// <summary>The distance HTML puts between the cells of a table that says nothing about it.</summary>
    private const Double DefaultSpacing = 2;

    /// <summary>The border every cell of a bordered table is drawn with, however thick the table's is.</summary>
    private const Double CellBorderWidth = 1;

    /// <summary>The bevel a table with a <c>border</c> is drawn in.</summary>
    private const String OutsetBorder = "outset";

    /// <summary>The bevel the cells of such a table are drawn in.</summary>
    private const String InsetBorder = "inset";

    /// <summary>The elements HTML gives a <c>bgcolor</c> hint to.</summary>
    private static readonly HashSet<String> BackgroundColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "body", "table", "thead", "tbody", "tfoot", "tr", "td", "th",
    };

    /// <summary>The elements whose <c>width</c> and <c>height</c> attributes the table sizes by.</summary>
    private static readonly HashSet<String> Cells = new(StringComparer.OrdinalIgnoreCase)
    {
        "td", "th",
    };

    /// <summary>
    /// Gets the colour a <c>bgcolor</c> attribute hints at for this element, or null where the element is
    /// none of the ones the attribute means anything on, writes no such attribute, or writes a value that
    /// names no colour.
    /// </summary>
    public static CssColorValue? BackgroundColor(IElement? element) =>
        element is not null && BackgroundColors.Contains(element.LocalName)
            ? LegacyColor.Parse(element.GetAttribute("bgcolor"))
            : null;

    /// <summary>Gets the colour the <c>body</c> element's <c>text</c> attribute hints at.</summary>
    public static CssColorValue? TextColor(IElement? element) =>
        element is not null && element.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase)
            ? LegacyColor.Parse(element.GetAttribute("text"))
            : null;

    /// <summary>
    /// Gets the colour one of the <c>body</c> element's link attributes hints at: <c>link</c> for a link
    /// not yet read, <c>vlink</c> for one that has, and <c>alink</c> for the one being followed.
    /// </summary>
    public static CssColorValue? LinkColor(IElement? element, String name) =>
        element?.Owner?.Body?.GetAttribute(name) is { } value ? LegacyColor.Parse(value) : null;

    /// <summary>
    /// Gets the size a <c>width</c> or <c>height</c> attribute hints at for a table - the box the table
    /// itself takes, which is what makes <c>width="300"</c> a table 300 pixels across whatever its cells
    /// ask for. Nothing is said for an element the attribute does not size, for a value that names no
    /// number, and for a zero, which HTML reads as no size at all.
    /// </summary>
    public static StyleLength? Size(IElement? element, String name) =>
        element is not null && element.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase)
            ? Dimension(element.GetAttribute(name))
            : null;

    /// <summary>
    /// Gets the size a <c>width</c> or <c>height</c> attribute hints at for a cell or a row, which the
    /// table algorithm holds as a least rather than as the size the box comes to: a cell asked for 30
    /// pixels is wider where its content wants more, and a row asked for 15 is taller where a table of
    /// 300 has room to spare.
    /// </summary>
    /// <remarks>
    /// Two forms are read as nothing, both measured in Chrome: a <c>width</c> on a row, which the row
    /// takes from its columns rather than the other way about, and a percentage <c>height</c> on a cell or
    /// a row, which is a share of a table that is still being sized - where the table did declare a height
    /// the rows are stretched to it anyway, and where it did not there is no whole for the share to be of.
    /// </remarks>
    public static StyleLength? CellSize(IElement? element, String name)
    {
        if (element is null)
        {
            return null;
        }

        var row = element.LocalName.Equals("tr", StringComparison.OrdinalIgnoreCase);

        if (!row && !Cells.Contains(element.LocalName))
        {
            return null;
        }

        var height = name.Equals("height", StringComparison.OrdinalIgnoreCase);

        // The row's width says nothing; the cell's or row's percentage height has no whole to be taken of.
        if (!height && row)
        {
            return null;
        }

        var length = Dimension(element.GetAttribute(name));

        return !height || length?.Kind != StyleLengthKind.Percent ? length : null;
    }

    /// <summary>
    /// Gets the padding a cell keeps around its content. A table that says nothing about the room leaves
    /// the cell the one pixel every browser's sheet gives it; a table that writes the attribute but names
    /// no number in it still asks for the room, and <c>cellpadding="abc"</c> is therefore no room at all,
    /// as Chrome measures it, while a table that writes the attribute with nothing in it says no more than
    /// one that writes none.
    /// </summary>
    public static StyleLength? CellPadding(IElement? element)
    {
        if (element is null || !Cells.Contains(element.LocalName))
        {
            return null;
        }

        var table = TableOf(element);

        if (table?.GetAttribute("cellpadding") is not { } value || value.Length == 0)
        {
            return StyleLength.FromPixels(DefaultCellPadding);
        }

        // The number is a count of pixels, so a part of one is no part of the room it describes.
        return StyleLength.FromPixels(Math.Max(0, Math.Floor(LeadingNumber(value) ?? 0)));
    }

    /// <summary>
    /// Gets the distance a table keeps between its cells and between them and its own edge: its
    /// <c>cellspacing</c> where the attribute names a distance, and the two pixels HTML gives a table that
    /// says nothing. The attribute is read as a number of pixels and nothing else - a value that ends in a
    /// percent sign is no distance, as Chrome measures it, and so is a negative one or a value that does
    /// not begin with a digit, both of which leave the table at the distance it would have had anyway.
    /// Nothing is said for a box that is not an HTML table, which keeps the distance the property begins
    /// with however its display is spelled: the attribute means nothing on a box the page laid out as a
    /// table itself.
    /// </summary>
    public static Double? Spacing(IElement? element)
    {
        if (element is null || !element.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = element.GetAttribute("cellspacing");

        if (value is null || value.Length == 0 || value.Contains('%') ||
            LeadingNumber(value) is not { } number || number < 0)
        {
            return DefaultSpacing;
        }

        return Snap(number);
    }

    /// <summary>
    /// Gets the width of the border a <c>border</c> attribute hints at: for a table, the number the
    /// attribute holds, and for one of its cells, the one pixel Chrome rings every cell of a bordered
    /// table with however thick the table's own border is asked to be. Nothing is said where no
    /// <c>border</c> attribute stands on the table the cell belongs to.
    /// </summary>
    /// <remarks>
    /// A table's attribute that names no number at all - nothing in it, or letters, or a negative - is
    /// still a border asked for, and comes to one pixel; only a number that is zero takes the border back.
    /// </remarks>
    public static Double? BorderWidth(IElement? element)
    {
        if (element is null)
        {
            return null;
        }

        if (element.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase))
        {
            return TableBorderWidth(element);
        }

        if (!Cells.Contains(element.LocalName))
        {
            return null;
        }

        return TableBorderWidth(TableOf(element)) is { } width ? (width > 0 ? CellBorderWidth : 0) : null;
    }

    /// <summary>
    /// Gets the border style a <c>border</c> attribute hints at: the table raised on the page in its own
    /// bevel, and its cells sunk into it. Nothing is said where the table writes no such attribute, or
    /// writes it as no border, which is what leaves both to the flat edge the property begins with.
    /// </summary>
    public static String? BorderStyle(IElement? element) =>
        BorderWidth(element) is { } width && width > 0
            ? element!.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase) ? OutsetBorder : InsetBorder
            : null;

    /// <summary>Gets the border width a table's own <c>border</c> attribute hints at, or null for none.</summary>
    private static Double? TableBorderWidth(IElement? table)
    {
        if (table is null || table.GetAttribute("border") is not { } value)
        {
            return null;
        }

        if (LeadingNumber(value) is not { } number)
        {
            return 1;
        }

        var width = Math.Floor(number);

        // A negative value is no width at all, and Chrome answers it with the one pixel it gives a border
        // that names nothing; a zero - however it is spelled - is the width that takes the border back.
        return width < 0 ? 1 : width;
    }

    /// <summary>
    /// Reads a dimension the way HTML reads one: the number at the front of the value, which may be
    /// followed by anything at all - <c>40px</c> and <c>40pt</c> are both 40 pixels - or by a percent sign,
    /// which makes it a share of the box the element is sized against. A value with no number at its front
    /// says nothing, and so does a size that is no size.
    /// </summary>
    private static StyleLength? Dimension(String? value)
    {
        if (value is null)
        {
            return null;
        }

        var text = value.Trim();
        var percent = text.EndsWith("%", StringComparison.Ordinal);

        if (percent)
        {
            text = text[..^1];
        }

        if (LeadingNumber(text) is not { } number || number <= 0)
        {
            return null;
        }

        return percent ? StyleLength.FromPercent(number / 100) : StyleLength.FromPixels(number);
    }

    /// <summary>
    /// Gets the number a value begins with, sign and fraction included, or null where it begins with
    /// nothing that is a number. What follows the number is left where it stands, which is how
    /// <c>1em</c> reads as one and <c>1e3</c> as one rather than a thousand.
    /// </summary>
    private static Double? LeadingNumber(String value)
    {
        var text = value.Trim();
        var at = 0;

        if (at < text.Length && (text[at] == '+' || text[at] == '-'))
        {
            at++;
        }

        var digits = at;

        while (at < text.Length && Char.IsAsciiDigit(text[at]))
        {
            at++;
        }

        if (at == digits)
        {
            return null;
        }

        if (at < text.Length && text[at] == '.')
        {
            at++;

            var fraction = at;

            while (at < text.Length && Char.IsAsciiDigit(text[at]))
            {
                at++;
            }

            // A point with nothing after it is no part of a number, and the number ends before it.
            if (at == fraction)
            {
                at--;
            }
        }

        return Double.TryParse(text.AsSpan(0, at), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    /// <summary>
    /// Brings a spacing to the half pixel the table's borders are laid out on, which is where Chrome
    /// settles a fractional one: <c>cellspacing="12.9"</c> is 12.5 pixels of room, not 12.9.
    /// </summary>
    private static Double Snap(Double number) => Math.Floor(number * 2) / 2;

    /// <summary>
    /// Gets what the hints this element takes from its own attributes and from the table it belongs to
    /// come to, as one string, or null where it takes none. Two elements that match the same rules are one
    /// kind of element to the style cache, and an attribute read as a declaration is the difference the
    /// cache cannot otherwise see: the cells of a table that set its own padding are not the cells of one
    /// that did not, whatever the page's sheet says about both.
    /// </summary>
    public static String? Fingerprint(IElement? element)
    {
        if (element is null || !TableFamily.Contains(element.LocalName))
        {
            return null;
        }

        var table = TableOf(element) ?? element;
        var keyed = new StringBuilder();

        foreach (var name in OwnHints)
        {
            if (element.GetAttribute(name) is { } own)
            {
                keyed.Append(name).Append('=').Append(own).Append(';');
            }
        }

        if (!ReferenceEquals(table, element))
        {
            foreach (var name in InheritedHints)
            {
                if (table.GetAttribute(name) is { } shared)
                {
                    keyed.Append(name).Append('=').Append(shared).Append(';');
                }
            }
        }

        return keyed.Length == 0 ? null : keyed.ToString();
    }

    /// <summary>The elements HTML hints a size or a room at.</summary>
    private static readonly HashSet<String> TableFamily = new(StringComparer.OrdinalIgnoreCase)
    {
        "table", "thead", "tbody", "tfoot", "tr", "td", "th", "caption",
    };

    /// <summary>The attributes read as a declaration on the element that writes them.</summary>
    private static readonly String[] OwnHints = { "width", "height", "cellspacing", "border", "cellpadding" };

    /// <summary>The attributes a cell reads from the table it belongs to.</summary>
    private static readonly String[] InheritedHints = { "cellpadding", "border" };

    /// <summary>Gets the table a cell or a row belongs to, which is the nearest one above it.</summary>
    private static IElement? TableOf(IElement element)
    {
        for (var at = element.ParentElement; at is not null; at = at.ParentElement)
        {
            if (at.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase))
            {
                return at;
            }
        }

        return null;
    }
}

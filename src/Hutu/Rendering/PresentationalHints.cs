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

    /// <summary>The flat line a frame or a rule draws.</summary>
    private const String SolidBorder = "solid";

    /// <summary>The side a frame leaves out of its ring, which draws nothing and would hide a line drawn there.</summary>
    private const String HiddenBorder = "hidden";

    /// <summary>The side a rule leaves out of its ring, which leaves the side to whatever else asks for it.</summary>
    private const String NoBorder = "none";

    /// <summary>The width a border begins with, which is what a frame draws where no border names one.</summary>
    private const Double MediumBorderWidth = 3;

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

    /// <summary>One ring a legacy attribute asks for: a width and a style for each of the four sides.</summary>
    /// <remarks>
    /// A side whose style is <c>none</c> or <c>hidden</c> draws nothing. The two are not the same in the
    /// collapsing model - hidden suppresses the line it meets and none loses to it - so the difference is
    /// kept, and the rendering section of the HTML standard is what says which side gets which.
    /// </remarks>
    internal readonly record struct Ring(
        Double Top,
        Double Right,
        Double Bottom,
        Double Left,
        String TopStyle,
        String RightStyle,
        String BottomStyle,
        String LeftStyle);

    /// <summary>
    /// Gets the ring the legacy border attributes ask of this element, read as the rendering section of
    /// the HTML standard reads them. The table's own ring comes from <c>rules</c>, <c>border</c> and
    /// <c>frame</c> in that order - a rules table hides its own edges and collapses, a border raises
    /// them, and a frame names the sides - while cells, rows, row groups and column groups take the
    /// borders the rules value puts on them.
    /// </summary>
    public static Ring? Border(IElement? element)
    {
        if (element is null)
        {
            return null;
        }

        var name = element.LocalName;

        if (name.Equals("table", StringComparison.OrdinalIgnoreCase))
        {
            return TableRing(element);
        }

        if (Cells.Contains(name))
        {
            return CellRing(TableOf(element));
        }

        if (name.Equals("tr", StringComparison.OrdinalIgnoreCase))
        {
            return RowRing(TableOf(element));
        }

        if (name.Equals("thead", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("tbody", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("tfoot", StringComparison.OrdinalIgnoreCase))
        {
            return RowGroupRing(TableOf(element));
        }

        return name.Equals("colgroup", StringComparison.OrdinalIgnoreCase) ? ColumnGroupRing(TableOf(element)) : null;
    }

    /// <summary>
    /// Gets whether the table's <c>rules</c> attribute puts it into the collapsing model, which the
    /// standard's rendering section does for every value the attribute takes. A declaration the page
    /// writes for <c>border-collapse</c> is read instead of this.
    /// </summary>
    public static Boolean Collapses(IElement? element) =>
        element is not null &&
        element.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase) &&
        RulesOf(element.GetAttribute("rules")) is not null;

    private static Ring? TableRing(IElement? table)
    {
        if (table is null)
        {
            return null;
        }

        var rules = RulesOf(table.GetAttribute("rules"));
        var frame = FrameOf(table.GetAttribute("frame"));
        var border = TableBorderWidth(table);
        var sized = border is { } size && size > 0;

        if (rules is null && !sized && frame is null)
        {
            return null;
        }

        // The standard's sheet sets the rules first, the border over it, and the frame over that, so a
        // table with both a border and rules draws its border, and a frame names the sides after both.
        var style = rules is not null ? HiddenBorder : NoBorder;
        var width = sized ? border!.Value : MediumBorderWidth;

        if (sized)
        {
            style = OutsetBorder;
        }

        if (frame is { } sides)
        {
            return new Ring(
                sides.Top ? width : 0,
                sides.Right ? width : 0,
                sides.Bottom ? width : 0,
                sides.Left ? width : 0,
                sides.Top ? OutsetBorder : HiddenBorder,
                sides.Right ? OutsetBorder : HiddenBorder,
                sides.Bottom ? OutsetBorder : HiddenBorder,
                sides.Left ? OutsetBorder : HiddenBorder);
        }

        return new Ring(width, width, width, width, style, style, style, style);
    }

    private static Ring? CellRing(IElement? table)
    {
        if (table is null)
        {
            return null;
        }

        var rules = RulesOf(table.GetAttribute("rules"));

        if (rules is { } value)
        {
            // The standard gives every cell a one-pixel border and takes back the sides the rules do
            // not draw, so the width is there for the collapsing model while the style says what shows.
            return value.Kind switch
            {
                RulesKind.All => new Ring(1, 1, 1, 1, SolidBorder, SolidBorder, SolidBorder, SolidBorder),
                RulesKind.Cols => new Ring(1, 1, 1, 1, NoBorder, SolidBorder, NoBorder, SolidBorder),
                _ => new Ring(1, 1, 1, 1, NoBorder, NoBorder, NoBorder, NoBorder),
            };
        }

        return TableBorderWidth(table) is { } width && width > 0
            ? new Ring(1, 1, 1, 1, InsetBorder, InsetBorder, InsetBorder, InsetBorder)
            : null;
    }

    private static Ring? RowRing(IElement? table) =>
        RulesOf(table?.GetAttribute("rules")) is { Kind: RulesKind.Rows }
            ? new Ring(1, 1, 1, 1, SolidBorder, NoBorder, SolidBorder, NoBorder)
            : null;

    private static Ring? RowGroupRing(IElement? table) =>
        RulesOf(table?.GetAttribute("rules")) is { Kind: RulesKind.Groups }
            ? new Ring(1, 1, 1, 1, SolidBorder, NoBorder, SolidBorder, NoBorder)
            : null;

    private static Ring? ColumnGroupRing(IElement? table) =>
        RulesOf(table?.GetAttribute("rules")) is { Kind: RulesKind.Groups }
            ? new Ring(1, 1, 1, 1, NoBorder, SolidBorder, NoBorder, SolidBorder)
            : null;

    /// <summary>
    /// Gets the colour a <c>bordercolor</c> attribute hints at: the table's, for the table itself and for
    /// every cell of it, and a cell's own where it writes one. Nothing is said where neither does.
    /// </summary>
    public static CssColorValue? BorderColor(IElement? element)
    {
        if (element is null)
        {
            return null;
        }

        var table = element.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase);

        if (!table && !Cells.Contains(element.LocalName))
        {
            return null;
        }

        if (LegacyColor.Parse(element.GetAttribute("bordercolor")) is { } own)
        {
            return own;
        }

        var owner = table ? element : TableOf(element);

        return owner is null ? null : LegacyColor.Parse(owner.GetAttribute("bordercolor"));
    }

    /// <summary>
    /// Gets the sides a <c>frame</c> names, or null where it names none of the values HTML gives it -
    /// which leaves the table's borders to the <c>border</c> attribute.
    /// </summary>
    private static Sides? FrameOf(String? value) => value?.Trim().ToLowerInvariant() switch
    {
        "void" => new Sides(false, false, false, false),
        "above" => new Sides(true, false, false, false),
        "below" => new Sides(false, false, true, false),
        "hsides" => new Sides(true, false, true, false),
        "vsides" => new Sides(false, true, false, true),
        "lhs" => new Sides(false, false, false, true),
        "rhs" => new Sides(false, true, false, false),
        "box" or "border" => new Sides(true, true, true, true),
        _ => null,
    };

    /// <summary>
    /// Gets what a <c>rules</c> value asks for, or null where it names none of the values the standard
    /// gives it. Which boxes draw the lines differs by value: <c>rows</c> gives them to the rows,
    /// <c>groups</c> to the row and column groups, and <c>all</c> and <c>cols</c> to the cells.
    /// </summary>
    private static Rules? RulesOf(String? value) => value?.Trim().ToLowerInvariant() switch
    {
        "none" => new Rules(RulesKind.None),
        "groups" => new Rules(RulesKind.Groups),
        "rows" => new Rules(RulesKind.Rows),
        "cols" => new Rules(RulesKind.Cols),
        "all" => new Rules(RulesKind.All),
        _ => null,
    };

    private enum RulesKind : Byte
    {
        None,
        Groups,
        Rows,
        Cols,
        All,
    }

    private readonly record struct Rules(RulesKind Kind);

    private readonly record struct Sides(Boolean Top, Boolean Right, Boolean Bottom, Boolean Left);

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

    /// <summary>The table parts whose legacy alignment attributes HTML gives a meaning.</summary>
    private static readonly HashSet<String> Aligned = new(StringComparer.OrdinalIgnoreCase)
    {
        "thead", "tbody", "tfoot", "tr", "td", "th", "col", "colgroup",
    };

    /// <summary>
    /// Gets the <c>text-align</c> an <c>align</c> attribute hints at for a table part. Chrome reads the
    /// physical spellings and <c>absmiddle</c>, which is the older spelling of a centre: measured,
    /// <c>align="left"</c> on a row computes <c>left</c> on its cells and <c>absmiddle</c> computes
    /// <c>center</c>. Any other value says nothing.
    /// </summary>
    public static String? Align(IElement? element) =>
        element is not null && Aligned.Contains(element.LocalName)
            ? element.GetAttribute("align")?.Trim().ToLowerInvariant() switch
            {
                "left" => "left",
                "right" => "right",
                "center" or "absmiddle" => "center",
                "justify" => "justify",
                _ => null,
            }
            : null;

    /// <summary>
    /// Gets the alignment a table header is given when nothing else says: the standard's sheet centres
    /// it, and a declaration the page writes - the <c>align</c> attribute among them - stands above that.
    /// </summary>
    public static String? DefaultTextAlign(IElement? element) =>
        element is not null && element.LocalName.Equals("th", StringComparison.OrdinalIgnoreCase)
            ? "center"
            : null;

    /// <summary>
    /// Gets the <c>vertical-align</c> a <c>valign</c> attribute hints at for a table part, or null where
    /// it names none of the four values HTML gives it.
    /// </summary>
    public static String? Valign(IElement? element) =>
        element is not null && Aligned.Contains(element.LocalName)
            ? element.GetAttribute("valign")?.Trim().ToLowerInvariant() switch
            {
                "top" => "top",
                "middle" => "middle",
                "bottom" => "bottom",
                "baseline" => "baseline",
                _ => null,
            }
            : null;

    /// <summary>
    /// Gets the side a table's <c>align</c> attribute floats it to. The standard's sheet gives a table
    /// <c>float: left</c> or <c>float: right</c>, and <c>align="center"</c> auto inline margins instead -
    /// see <see cref="CentersTable"/>.
    /// </summary>
    public static Layout.FloatSide? Float(IElement? element) =>
        element is not null && element.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase)
            ? element.GetAttribute("align")?.Trim().ToLowerInvariant() switch
            {
                "left" => Layout.FloatSide.Left,
                "right" => Layout.FloatSide.Right,
                _ => null,
            }
            : null;

    /// <summary>Gets whether a table's <c>align="center"</c> puts an auto margin on each inline side.</summary>
    public static Boolean CentersTable(IElement? element) =>
        element is not null &&
        element.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase) &&
        String.Equals(element.GetAttribute("align")?.Trim(), "center", StringComparison.OrdinalIgnoreCase);

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
        "table", "thead", "tbody", "tfoot", "tr", "td", "th", "caption", "col", "colgroup",
    };

    /// <summary>The attributes read as a declaration on the element that writes them.</summary>
    private static readonly String[] OwnHints = { "width", "height", "cellspacing", "border", "cellpadding", "frame", "rules", "bordercolor", "align", "valign" };

    /// <summary>The attributes a cell reads from the table it belongs to.</summary>
    private static readonly String[] InheritedHints = { "cellpadding", "border", "rules", "bordercolor", "align", "valign" };

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

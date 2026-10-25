using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using System;
using System.Globalization;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The alignment keywords flex shares between justification and alignment.
/// </summary>
/// <remarks>
/// This mirrors <see cref="FlexContentMode"/> but keeps Hutu's own alignment vocabulary, so the layout
/// code never depends on the parser's enum. <c>space-evenly</c>, <c>start</c> and <c>end</c> are now
/// defined as keywords in AngleSharp.Css, but the local switch still lists them explicitly.
/// </remarks>
internal enum FlexAlignment : byte
{
    /// <summary>Not set: a flex item defers to its container, a container defers to its initial value.</summary>
    Auto,
    Start,
    End,
    Center,
    SpaceBetween,
    SpaceAround,
    SpaceEvenly,
    Stretch,
    Baseline,
    /// <summary>
    /// The <c>normal</c> keyword, kept apart from <see cref="Auto"/> because the two answer differently:
    /// <c>auto</c> asks the container what to do, and <c>normal</c> is the layout's own answer - stretch
    /// for an item in flow, the start edge for an out-of-flow box (CSS Align 3 §5.1).
    /// </summary>
    Normal,

    /// <summary>
    /// The <c>baseline</c> position taken from the box's last line rather than its first. An
    /// out-of-flow box has no line to stand on, so its last baseline is synthesized at the end edge.
    /// </summary>
    LastBaseline,

    /// <summary>
    /// The physical left edge, which is the logical end of a right-to-left row. Kept apart from
    /// <see cref="Start"/> so that a page asking for the left edge gets the left edge in either
    /// direction (CSS Align 3 §5.2).
    /// </summary>
    Left,

    /// <summary>The physical right edge, kept apart from <see cref="End"/> for the same reason.</summary>
    Right,
}

/// <summary>
/// The flex longhands of a box, read without the shorthands and without trusting the computed
/// declaration's keyword types.
/// </summary>
/// <remarks>
/// Several upstream defects are contained here rather than worked around at every use:
/// <list type="bullet">
/// <item><c>position</c>'s initial value is keyed <c>inline</c>, so the string is never read for a
/// keyword that has a typed value.</item>
/// <item><c>flex-grow</c>, <c>flex-shrink</c> and <c>order</c> carry their initial values as a
/// <see cref="CssLengthValue"/> rather than a number, so both shapes are accepted.</item>
/// <item><c>align-items</c>' and <c>align-content</c>'s initial values serialise as <c>normal</c>
/// while meaning <c>stretch</c>, so the typed value wins when it is present.</item>
/// </list>
/// A shorthand such as <c>flex</c> is deliberately not read: AngleSharp.Css rebuilds a shorthand
/// from its longhands lossily, so the longhands are the only faithful source.
/// </remarks>
internal readonly struct FlexStyle
{
    private FlexStyle(
        FlexDirection direction,
        FlexWrapMode wrap,
        FlexAlignment justifyContent,
        FlexAlignment alignItems,
        FlexAlignment alignContent,
        Double rowGap,
        Double columnGap,
        Double grow,
        Double shrink,
        StyleLength basis,
        Boolean basisIsContent,
        Int32 order,
        FlexAlignment alignSelf)
    {
        Direction = direction;
        Wrap = wrap;
        JustifyContent = justifyContent;
        AlignItems = alignItems;
        AlignContent = alignContent;
        RowGap = rowGap;
        ColumnGap = columnGap;
        Grow = grow;
        Shrink = shrink;
        Basis = basis;
        BasisIsContent = basisIsContent;
        Order = order;
        AlignSelf = alignSelf;
    }

    public FlexDirection Direction { get; }

    public FlexWrapMode Wrap { get; }

    public FlexAlignment JustifyContent { get; }

    public FlexAlignment AlignItems { get; }

    public FlexAlignment AlignContent { get; }

    /// <summary>
    /// Gets the flex container grown by the zoom its box carries: the two gaps are the room the page
    /// asked to leave between boxes and the basis is a length like any other, so a scale reaches them
    /// all. The style holds the declaration's own numbers, so one flex style serves a container drawn at
    /// two and the same container drawn at one.
    /// </summary>
    internal FlexStyle Scaled(Double factor) =>
        factor == 1
            ? this
            : new FlexStyle(
                Direction,
                Wrap,
                JustifyContent,
                AlignItems,
                AlignContent,
                RowGap * factor,
                ColumnGap * factor,
                Grow,
                Shrink,
                Basis.Scaled(factor),
                BasisIsContent,
                Order,
                AlignSelf);

    public Double RowGap { get; }

    public Double ColumnGap { get; }

    public Double Grow { get; }

    public Double Shrink { get; }

    /// <summary>Gets the flex base size. <c>auto</c> and <c>content</c> both end up without a value.</summary>
    public StyleLength Basis { get; }

    /// <summary>Gets whether the base size is <c>content</c>, which is measured rather than resolved and ignores the main-size property.</summary>
    public Boolean BasisIsContent { get; }

    public Int32 Order { get; }

    public FlexAlignment AlignSelf { get; }

    /// <summary>Gets whether the main axis runs horizontally.</summary>
    public Boolean IsRow => Direction is FlexDirection.Row or FlexDirection.RowReverse;

    /// <summary>Gets whether the main axis runs backwards.</summary>
    public Boolean IsReversed => Direction is FlexDirection.RowReverse or FlexDirection.ColumnReverse;

    /// <summary>Gets whether the items break onto more than one line when they do not fit.</summary>
    public Boolean Wraps => Wrap != FlexWrapMode.NoWrap;

    /// <summary>Gets whether the lines are stacked in the reverse order along the cross axis.</summary>
    public Boolean IsWrapReversed => Wrap == FlexWrapMode.WrapReverse;

    internal static FlexStyle Read(ComputedStyle style)
    {
        var (rowGap, columnGap) = ReadGaps(style);
        var justify = ReadAlignment(style, "justify-content", CssInitialValues.JustifyContent);
        var (grow, shrink, basis, basisIsContent) = ReadFlexLonghands(style);

        return new FlexStyle(
            ReadDirection(style),
            ReadWrap(style),
            Justify(justify, Text(style, "justify-content")),
            ReadAlignment(style, "align-items", CssInitialValues.AlignItems),
            ReadAlignment(style, "align-content", CssInitialValues.AlignContent),
            rowGap,
            columnGap,
            grow,
            shrink,
            basis,
            basisIsContent,
            (Int32)ReadNumber(style, "order", 0),
            ReadAlignment(style, "align-self", CssInitialValues.AlignSelf));
    }

    /// <summary>
    /// Reads the three flex longhands, containing the shorthand's keyword forms: <c>flex: none</c>
    /// and its kin are stored as a whole rather than split into their longhands, so the longhands
    /// would all answer at their initials - a shrink of one where <c>none</c> meant zero.
    /// </summary>
    private static (Double Grow, Double Shrink, StyleLength Basis, Boolean BasisIsContent) ReadFlexLonghands(
        ComputedStyle style)
    {
        var grow = ReadNumber(style, "flex-grow", 0);
        var shrink = ReadNumber(style, "flex-shrink", 1);
        var basis = ReadBasis(style);
        var basisIsContent = Text(style, "flex-basis") == "content";

        if (KeywordFlex(Text(style, "flex")) is { } keyword)
        {
            (grow, shrink, basis, basisIsContent) = keyword;
        }
        else if (OmittedBasis(style.Specified?.GetProperty("flex-basis")))
        {
            basis = StyleLength.FromPercent(0);
        }

        return (grow, shrink, basis, basisIsContent);
    }

    /// <summary>
    /// The longhands' initial values: what a style that was never handed a flex context answers
    /// with. A fused style carries no declaration, so a box that is neither a flex container nor a
    /// flex item cannot be read at all - and the struct's own default is not this, because
    /// <see cref="FlexAlignment.Auto"/> is the first member of that enum and an alignment that is
    /// unset is not the same thing as one that is <c>stretch</c> or <c>flex-start</c>.
    /// </summary>
    internal static FlexStyle Initial { get; } = new(
        CssInitialValues.FlexDirection,
        CssInitialValues.FlexWrap,
        CssInitialValues.JustifyContent,
        CssInitialValues.AlignItems,
        CssInitialValues.AlignContent,
        0,
        0,
        0,
        1,
        StyleLength.Auto,
        false,
        0,
        CssInitialValues.AlignSelf);

    /// <summary>
    /// Reads the flex longhands from the element's own cascaded declarations, for the fused path.
    /// A container reads its direction, wrapping, alignment and gaps; an item reads its grow,
    /// shrink, basis, order and <c>align-self</c>. Both are read together because asking which one
    /// a box is would cost as much as the reads, and the ones that do not apply are never asked for.
    /// </summary>
    internal static FlexStyle ReadFused(
        Func<String, ICssProperty?> find, LengthReader.FontSource font, IRenderDimensions dimensions,
        StyleLength? inheritedBasis = null)
    {
        var (rowGap, columnGap) = ReadGapsFused(find, font, dimensions);
        var justify = ReadAlignmentFused(find("justify-content"), CssInitialValues.JustifyContent);

        var grow = ReadNumberFused(find("flex-grow"), 0);
        var shrink = ReadNumberFused(find("flex-shrink"), 1);
        var basis = ReadBasisFused(find("flex-basis"), font, dimensions, inheritedBasis);
        var basisIsContent = Text(find("flex-basis")) == "content";

        // The keyword forms of the shorthand are stored as a whole rather than split into their
        // longhands, so the longhands read as their initials unless the shorthand is read here.
        if (KeywordFlex(Text(find("flex"))) is { } keyword)
        {
            (grow, shrink, basis, basisIsContent) = keyword;
        }
        else if (OmittedBasis(find("flex-basis")))
        {
            basis = StyleLength.FromPercent(0);
        }

        return new FlexStyle(
            ReadDirectionFused(find("flex-direction")),
            ReadWrapFused(find("flex-wrap")),
            Justify(justify, Text(find("justify-content"))),
            ReadAlignmentFused(find("align-items"), CssInitialValues.AlignItems),
            ReadAlignmentFused(find("align-content"), CssInitialValues.AlignContent),
            rowGap,
            columnGap,
            grow,
            shrink,
            basis,
            basisIsContent,
            (Int32)ReadNumberFused(find("order"), 0),
            ReadAlignmentFused(find("align-self"), CssInitialValues.AlignSelf));
    }

    /// <summary>
    /// Reads the two gaps from the cascaded declarations, one axis at a time.
    /// </summary>
    /// <remarks>
    /// Each axis stands on its own: <c>row-gap</c> and <c>column-gap</c> are separate longhands whose
    /// initial value <c>normal</c> is a used 0px in everything but a multi-column container (CSS Gaps 1
    /// §2.1), and only the <c>gap</c> shorthand sets both - by giving the column the row's value when
    /// the second one is omitted (§2.2). It used to be read the other way round here: the two axes
    /// were answered with whichever of them carried a number, because a single-value <c>gap</c> left
    /// its column longhand holding nothing at all and that was indistinguishable from a lone
    /// <c>row-gap</c>. The split no longer leaves it empty, so a page that sets only <c>column-gap</c>
    /// keeps its zero row gap - and Wikipedia's <c>column-gap: 24px</c> no longer opens a 24px band
    /// between every pair of rows.
    /// </remarks>
    private static (Double Row, Double Column) ReadGapsFused(
        Func<String, ICssProperty?> find, LengthReader.FontSource font, IRenderDimensions dimensions)
    {
        return Gaps(find, font, dimensions);
    }

    /// <summary>
    /// The same two gaps for a caller that is not a flex container: a grid sets them with the same
    /// longhands, and the two contexts have to read a page the same way.
    /// </summary>
    internal static (Double Row, Double Column) GapsFused(
        Func<String, ICssProperty?> find, LengthReader.FontSource font, IRenderDimensions dimensions) =>
        Gaps(find, font, dimensions);

    private static (Double Row, Double Column) Gaps(
        Func<String, ICssProperty?> find, LengthReader.FontSource font, IRenderDimensions dimensions)
    {
        // The grid-prefixed names are the legacy aliases of the two longhands (CSS Gaps 1 §2.4:
        // "grid-row-gap as a legacy name alias of the row-gap property"), and a page that writes only
        // those is otherwise read as having no gap at all. They are tried when the standard name
        // answers nothing; a page that declares both lets the standard one win, whichever order the
        // two were written in, because only the declaration list could weigh the alias against the
        // name and the fused path reads one declaration at a time.
        var row = ReadGapFused(find("row-gap"), font, dimensions) ??
            ReadGapFused(find("grid-row-gap"), font, dimensions);
        var column = ReadGapFused(find("column-gap"), font, dimensions) ??
            ReadGapFused(find("grid-column-gap"), font, dimensions);

        return (row ?? 0, column ?? 0);
    }

    private static Double? ReadGapFused(ICssProperty? declared, LengthReader.FontSource font, IRenderDimensions dimensions) =>
        declared is not null &&
        LengthReader.TryReadProperty(declared, RenderMode.Horizontal, dimensions, font, out var gap) &&
        gap.HasValue
            ? Math.Max(0, gap.Resolve(0))
            : null;

    private static Double? GapOfFused(ICssValue item, LengthReader.FontSource font, IRenderDimensions dimensions) =>
        LengthReader.TryReadValue(item, RenderMode.Horizontal, dimensions, font, out var gap) && gap.HasValue
            ? Math.Max(0, gap.Resolve(0))
            : null;

    private static FlexDirection ReadDirectionFused(ICssProperty? declared)
    {
        if (declared?.RawValue is CssConstantValue<FlexDirection> constant)
        {
            return constant.Value;
        }

        return Text(declared) switch
        {
            "row-reverse" => FlexDirection.RowReverse,
            "column" => FlexDirection.Column,
            "column-reverse" => FlexDirection.ColumnReverse,
            _ => CssInitialValues.FlexDirection,
        };
    }

    private static FlexWrapMode ReadWrapFused(ICssProperty? declared)
    {
        if (declared?.RawValue is CssConstantValue<FlexWrapMode> constant)
        {
            return constant.Value;
        }

        return Text(declared) switch
        {
            "wrap" => FlexWrapMode.Wrap,
            "wrap-reverse" => FlexWrapMode.WrapReverse,
            _ => CssInitialValues.FlexWrap,
        };
    }

    /// <summary>Reads one of the alignment longhands from the element's own cascaded declaration.</summary>
    internal static FlexAlignment ReadAlignmentFused(ICssProperty? declared, FlexAlignment fallback)
    {
        if (declared?.RawValue is CssConstantValue<FlexContentMode> constant && Map(constant.Value) is var mapped &&
            mapped != FlexAlignment.Auto)
        {
            return mapped;
        }

        var text = Text(declared);
        return text.Length == 0 ? fallback : Parse(text, fallback);
    }

    private static StyleLength ReadBasisFused(
        ICssProperty? declared, LengthReader.FontSource font, IRenderDimensions dimensions, StyleLength? inherited = null) =>
        inherited is { } parent && ComputedStyle.DeclaresInherit(declared)
            ? parent
            :
        declared is not null &&
        LengthReader.TryReadProperty(declared, RenderMode.Horizontal, dimensions, font, out var basis) &&
        basis.HasValue
            ? basis
            : StyleLength.Auto;

    private static Double ReadNumberFused(ICssProperty? declared, Double fallback)
    {
        switch (declared?.RawValue)
        {
            case CssNumberValue number:
                return number.Value;

            case CssIntegerValue integer:
                return integer.Value;

            case CssLengthValue length when !Double.IsNaN(length.Value) && !Double.IsInfinity(length.Value):
                return length.Value;
        }

        var text = Text(declared);
        return Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    /// <summary>Gets a keyword as the cascade wrote it, lowered and trimmed.</summary>
    private static String Text(ICssProperty? declared) =>
        declared?.Value?.Trim().ToLowerInvariant() ?? String.Empty;

    /// <summary>
    /// Reads the two gaps from the element's declaration, one axis at a time - the render-tree
    /// counterpart of <see cref="Gaps"/>, and answerable the same way ever since the <c>gap</c>
    /// shorthand stopped leaving its column longhand empty when only one value was written. A
    /// two-value <c>gap</c> is <c>row</c> then <c>column</c>, in that order.
    /// </summary>
    private static (Double Row, Double Column) ReadGaps(ComputedStyle style)
    {
        var row = ReadGap(style, "row-gap") ?? ReadGap(style, "grid-row-gap");
        var column = ReadGap(style, "column-gap") ?? ReadGap(style, "grid-column-gap");

        return (row ?? 0, column ?? 0);
    }

    private static Double? ReadGap(ComputedStyle style, String name) =>
        LengthReader.TryRead(style.Specified, style.Declaration, name, RenderMode.Horizontal, style.Dimensions, style.FontSize, out var gap) &&
        gap.HasValue
            ? Math.Max(0, gap.Resolve(0))
            : null;

    private static Double? GapOf(ComputedStyle style, ICssValue item) =>
        LengthReader.TryReadValue(item, RenderMode.Horizontal, style.Dimensions, style.FontSize, out var gap) && gap.HasValue
            ? Math.Max(0, gap.Resolve(0))
            : null;

    private static FlexDirection ReadDirection(ComputedStyle style)
    {
        if (style.RawValue("flex-direction") is CssConstantValue<FlexDirection> constant)
        {
            return constant.Value;
        }

        return Text(style, "flex-direction") switch
        {
            "row-reverse" => FlexDirection.RowReverse,
            "column" => FlexDirection.Column,
            "column-reverse" => FlexDirection.ColumnReverse,
            _ => CssInitialValues.FlexDirection,
        };
    }

    private static FlexWrapMode ReadWrap(ComputedStyle style)
    {
        if (style.RawValue("flex-wrap") is CssConstantValue<FlexWrapMode> constant)
        {
            return constant.Value;
        }

        return Text(style, "flex-wrap") switch
        {
            "wrap" => FlexWrapMode.Wrap,
            "wrap-reverse" => FlexWrapMode.WrapReverse,
            _ => CssInitialValues.FlexWrap,
        };
    }

    /// <summary>
    /// Reads one of the alignment longhands. It is shared with the grid context, because
    /// <c>justify-content</c>, <c>align-content</c> and <c>align-items</c> mean the same thing in both
    /// formatting contexts; only <c>justify-items</c> and the <c>*-self</c> pair are read elsewhere.
    /// A fallback of <see cref="FlexAlignment.Auto"/> is how a caller asks whether the property was
    /// set at all.
    /// </summary>
    internal static FlexAlignment ReadAlignment(ComputedStyle style, String name, FlexAlignment fallback)
    {
        if (style.RawValue(name) is CssConstantValue<FlexContentMode> constant)
        {
            var mapped = Map(constant.Value);

            if (mapped != FlexAlignment.Auto)
            {
                return mapped;
            }
        }

        var text = Text(style, name);
        return text.Length == 0 ? fallback : Parse(text, fallback);
    }

    private static FlexAlignment Map(FlexContentMode mode) => mode switch
    {
        FlexContentMode.Start => FlexAlignment.Start,
        FlexContentMode.End => FlexAlignment.End,
        FlexContentMode.Center => FlexAlignment.Center,
        FlexContentMode.SpaceBetween => FlexAlignment.SpaceBetween,
        FlexContentMode.SpaceAround => FlexAlignment.SpaceAround,
        FlexContentMode.SpaceEvenly => FlexAlignment.SpaceEvenly,
        FlexContentMode.SelfStart => FlexAlignment.Start,
        FlexContentMode.SelfEnd => FlexAlignment.End,
        FlexContentMode.Stretch => FlexAlignment.Stretch,
        FlexContentMode.Baseline => FlexAlignment.Baseline,
        _ => CssInitialValues.UnmappableAlignment,
    };

    /// <summary>
    /// Parses an alignment keyword. The mapping table is the reader's own rather than AngleSharp's,
    /// because the latter is missing <c>space-evenly</c> and does not know <c>start</c>/<c>end</c>.
    /// </summary>
    internal static FlexAlignment ParseAlignment(String text, FlexAlignment fallback) => Parse(text, fallback);

    private static FlexAlignment Parse(String text, FlexAlignment fallback)
    {
        // `safe` and `unsafe` are the overflow alignment modifier (CSS Box Alignment 3 §5.2). What
        // they decide is what happens when the subject overflows the box, which is a question the
        // reader leaves to the layout that carries the alignment out; the keyword they wrap still
        // names the alignment that was asked for. The HTML rendering rules spell a form control's
        // centring as `unsafe center`.
        if (text.StartsWith("unsafe ", StringComparison.Ordinal))
        {
            text = text["unsafe ".Length..];
        }
        else if (text.StartsWith("safe ", StringComparison.Ordinal))
        {
            text = text["safe ".Length..];
        }

        return ParseKeyword(text, fallback);
    }

    private static FlexAlignment ParseKeyword(String text, FlexAlignment fallback) => text switch
    {
        "flex-start" or "start" or "self-start" => FlexAlignment.Start,
        "flex-end" or "end" or "self-end" => FlexAlignment.End,
        // The two physical spellings name the left and the right edge of the area rather than its start
        // and end along an axis. Writing modes and right-to-left tracks are not carried apart from one
        // another here, so the edge each names is the edge the box is placed against.
        "left" => FlexAlignment.Left,
        "right" => FlexAlignment.Right,
        "center" => FlexAlignment.Center,
        "space-between" => FlexAlignment.SpaceBetween,
        "space-around" => FlexAlignment.SpaceAround,
        "space-evenly" => FlexAlignment.SpaceEvenly,
        "stretch" => FlexAlignment.Stretch,
        "baseline" => FlexAlignment.Baseline,
        "last baseline" => FlexAlignment.LastBaseline,
        "normal" => FlexAlignment.Normal,
        _ => fallback,
    };

    private static StyleLength ReadBasis(ComputedStyle style) =>
        LengthReader.TryRead(style.Specified, style.Declaration, "flex-basis", RenderMode.Horizontal, style.Dimensions, style.FontSize, out var basis) &&
        basis.HasValue
            ? basis
            : StyleLength.Auto;

    /// <summary>
    /// The keyword forms of the <c>flex</c> shorthand, which the aggregator stores as a whole rather
    /// than splitting into its longhands: read through the longhands alone, <c>flex: none</c> would
    /// answer a shrink of one where it meant zero.
    /// </summary>
    private static (Double Grow, Double Shrink, StyleLength Basis, Boolean BasisIsContent)? KeywordFlex(
        String text) => text switch
    {
        "none" => (0.0, 0.0, StyleLength.Auto, false),
        "auto" => (1.0, 1.0, StyleLength.Auto, false),
        "initial" => (0.0, 1.0, StyleLength.Auto, false),
        _ => null,
    };

    /// <summary>
    /// Gets whether a <c>flex-basis</c> declaration is the empty one the <c>flex</c> shorthand leaves
    /// behind when it is written without its third value. That value is <c>0%</c>, not <c>auto</c> (CSS
    /// Flexbox 1 §7.1.1) - which is what makes <c>flex: 1 0</c> share a container equally between items
    /// whose content differs, while still falling back to the content when the container's main size is
    /// indefinite, since a percentage has nothing to be a percentage of there (§9.2.3).
    /// </summary>
    private static Boolean OmittedBasis(ICssProperty? declared) =>
        declared is not null && String.IsNullOrWhiteSpace(declared.Value);

    /// <summary>
    /// The physical keywords <c>justify-content</c> takes beside the alignment ones: <c>right</c>
    /// justifies at the right edge and <c>left</c> at the left one, which in a page that reads
    /// left-to-right is the end and the start.
    /// </summary>
    private static FlexAlignment Justify(FlexAlignment read, String text) => text switch
    {
        "right" => FlexAlignment.End,
        "left" => FlexAlignment.Start,
        _ => read,
    };

    /// <summary>
    /// Reads a plain number. The initial values of <c>flex-grow</c>, <c>flex-shrink</c> and
    /// <c>order</c> are stored as lengths, so both a number and a length are accepted.
    /// </summary>
    private static Double ReadNumber(ComputedStyle style, String name, Double fallback)
    {
        switch (style.RawValue(name))
        {
            case CssNumberValue number:
                return number.Value;

            case CssIntegerValue integer:
                return integer.Value;

            case CssLengthValue length when !Double.IsNaN(length.Value) && !Double.IsInfinity(length.Value):
                return length.Value;
        }

        var text = Text(style, name);
        return Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    /// <summary>Gets a keyword, preferring the cascaded declaration, lowered and trimmed.</summary>
    internal static String Text(ComputedStyle style, String name)
    {
        var text = style.Specified?.GetPropertyValue(name);

        if (String.IsNullOrWhiteSpace(text))
        {
            text = style.Declaration.GetPropertyValue(name);
        }

        return text is null ? String.Empty : text.Trim().ToLowerInvariant();
    }
}

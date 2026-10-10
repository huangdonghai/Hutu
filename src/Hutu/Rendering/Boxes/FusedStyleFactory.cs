using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using AngleSharp.Dom;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// What a fused style inherits from its parent: the resolved values the resolvers take on a
/// miss, plus the parent's own cascaded <c>line-height</c> for the size-dependent inherit.
/// </summary>
public sealed class FusedParent
{
    public static readonly FusedParent Root = new();

    public Double FontSize { get; init; }
    public CssColorValue? Color { get; init; }
    public String FontFamily { get; init; } = "sans-serif";
    public Double LetterSpacing { get; init; }
    public Int32 Weight { get; init; } = 400;
    public Boolean Italic { get; init; }
    public Double? LineHeight { get; init; }
    public ICssProperty? LineHeightHit { get; init; }
    public String WhiteSpace { get; init; } = "normal";
    public String Align { get; init; } = "start";

    /// <summary>
    /// The parent's own resolved vertical alignment. The property is not inherited, so this
    /// travels only to answer an <c>inherit</c> - which the user-agent sheet declares on every
    /// row and cell of a table.
    /// </summary>
    public String VerticalAlign { get; init; } = CssInitialValues.VerticalAlign;

    /// <summary>
    /// Whether the parent is a flex container, which is what makes this element a flex item - the
    /// only thing in the cascade that says so, and the only reason <c>flex-grow</c>, <c>flex-shrink</c>,
    /// <c>flex-basis</c>, <c>order</c> and <c>align-self</c> mean anything on it.
    /// </summary>
    public Boolean InFlex { get; init; }

    /// <summary>
    /// Whether the parent is a grid container, which is what makes this element a grid item - the
    /// only thing that says so, and the only reason <c>grid-column-start</c> and friends mean
    /// anything on it.
    /// </summary>
    public Boolean InGrid { get; init; }

    public StyleLength Indent { get; init; } = StyleLength.Zero;
    public TextDirection Direction { get; init; } = TextDirection.LeftToRight;
    public WritingMode WritingMode { get; init; } = WritingMode.HorizontalTb;

    /// <summary>
    /// Where an annotation goes, which <c>ruby-position</c> states on an ancestor as often as on
    /// the pair itself and carries down like any other inherited property.
    /// </summary>
    public RubyPosition Ruby { get; init; } = RubyPosition.Over;

    public TextOrientation? Orientation { get; init; }
    public Boolean? CombinesUpright { get; init; }
    public CursorKind Cursor { get; init; }

    /// <summary>The values a parent hands down to its children.</summary>
    public FusedParent Next(ComputedStyle style, MatchedCascade cascade) => new()
    {
        FontSize = style.FontSize,
        Color = style.Color,
        FontFamily = style.FontFamily,
        LetterSpacing = style.LetterSpacing,
        Weight = style.FontWeight,
        Italic = style.IsItalic,
        LineHeight = style.LineHeight,

        // The nearest declaration at or above this element, not merely its own. A number or a
        // share is re-resolved against every element's own size, so it has to keep travelling
        // down: taken from the element alone it survived one level and no more, and a grandchild
        // fell back to the pixels its parent had already resolved - a body's 1.3333 reached a
        // 12.8px note as 21.3px instead of 17.1px. A length is unaffected: it does not parse
        // as a number in the resolver, which then takes the resolved value as it always did.
        LineHeightHit = cascade.Find("line-height") ?? LineHeightHit,
        WhiteSpace = style.WhiteSpace,
        Align = style.TextAlign,
        VerticalAlign = style.VerticalAlign,
        InFlex = style.Display.Contains("flex", StringComparison.OrdinalIgnoreCase),
        InGrid = style.Display.Contains("grid", StringComparison.OrdinalIgnoreCase),
        Indent = style.TextIndent,
        Direction = style.Direction,
        WritingMode = style.WritingMode,
        Ruby = style.RubyPosition,
        Orientation = style.DeclaredTextOrientation,
        CombinesUpright = style.DeclaredTextCombineUpright,
        Cursor = style.Cursor,
    };
}

/// <summary>
/// Assembles a <see cref="ComputedStyle"/> from the element's own matched declarations and the
/// values it inherits, with no render tree and no intermediate declaration.
/// </summary>
/// <remarks>
/// Every resolver reads the element's own cascade, where a hit is a declared value and a miss
/// is an inherited one - the distinction the render-tree path settles by a second cascade.
/// </remarks>
public static class FusedStyleFactory
{
    public static ComputedStyle Build(
        IElement element,
        MatchedCascade cascade,
        FusedParent parent,
        IRenderDimensions dimensions,
        FontFaceTable? faces,
        StyleIntern? pool)
    {
        Func<String, ICssProperty?> find = cascade.Find;

        var size = ComputedStyle.ResolveFontSizeFused(find("font-size"), parent.FontSize, dimensions);
        var font = new FontArea
        {
            Size = size,
            Family = ComputedStyle.ResolveFontFamilyFused(find("font-family"), parent.FontFamily, faces),
            Weight = ComputedStyle.ResolveFontWeightFused(find("font-weight"), parent.Weight),
            Italic = ComputedStyle.ResolveFontStyleFused(find("font-style"), parent.Italic),
            LetterSpacing = ComputedStyle.ResolveLetterSpacingFused(find("letter-spacing"), parent.LetterSpacing, size, dimensions),
            LineHeight = ComputedStyle.ResolveLineHeightFused(
                find("line-height"), parent.LineHeightHit, size, parent.LineHeight, dimensions),
        };

        var text = new TextArea
        {
            WhiteSpace = ComputedStyle.ResolveWhiteSpaceFused(find("white-space"), parent.WhiteSpace),
            Align = ComputedStyle.ResolveTextAlignFused(find("text-align"), parent.Align),
            VerticalAlign = ComputedStyle.ResolveVerticalAlignFused(find("vertical-align"), parent.VerticalAlign),
            Indent = ComputedStyle.ResolveTextIndentFused(find("text-indent"), parent.Indent, size, dimensions),
        };

        var borderStyles = ComputedStyle.ResolveBorderStylesFused(find);
        var borderWidths = ComputedStyle.ResolveBorderWidthsFused(find, borderStyles, size, dimensions);
        var border = new BorderArea
        {
            TopStyle = borderStyles.Top,
            RightStyle = borderStyles.Right,
            BottomStyle = borderStyles.Bottom,
            LeftStyle = borderStyles.Left,
            TopWidth = borderWidths.Top,
            RightWidth = borderWidths.Right,
            BottomWidth = borderWidths.Bottom,
            LeftWidth = borderWidths.Left,
            Lines = ComputedStyle.ResolveBorderLinesFused(borderStyles),
            Radius = ComputedStyle.ResolveBorderRadiusFused(find, size, dimensions),
        };

        var boxModel = new BoxModelArea
        {
            BoxSizing = ComputedStyle.ResolveBoxSizingFused(find("box-sizing")),
            Width = ComputedStyle.ResolveBoxLengthFused(find("width"), RenderMode.Horizontal, size, dimensions, StyleLength.Auto),
            Height = ComputedStyle.ResolveBoxLengthFused(find("height"), RenderMode.Vertical, size, dimensions, StyleLength.Auto),
            MinWidth = ComputedStyle.ResolveBoxLengthFused(find("min-width"), RenderMode.Horizontal, size, dimensions, StyleLength.None),
            MaxWidth = ComputedStyle.ResolveBoxLengthFused(find("max-width"), RenderMode.Horizontal, size, dimensions, StyleLength.None),
            MinHeight = ComputedStyle.ResolveBoxLengthFused(find("min-height"), RenderMode.Vertical, size, dimensions, StyleLength.None),
            MaxHeight = ComputedStyle.ResolveBoxLengthFused(find("max-height"), RenderMode.Vertical, size, dimensions, StyleLength.None),
            Margin = ComputedStyle.ResolveThicknessFused(find, element, size, dimensions, DetermineFlow(element, cascade, parent), "margin", StyleLength.Zero),
            Padding = ComputedStyle.ResolveThicknessFused(find, element, size, dimensions, DetermineFlow(element, cascade, parent), "padding", StyleLength.Zero),
        };

        var display = new DisplayPositioningArea
        {
            Display = ComputedStyle.ResolveDisplayFused(find("display")),
            ObjectFit = ComputedStyle.ResolveObjectFitFused(find("object-fit")),
            ClipsOverflow = ComputedStyle.ResolveClipsOverflowFused(find("overflow-x"), find("overflow-y")),
        };

        var outlineStyle = ComputedStyle.ResolveOutlineStyleFused(find("outline-style"));
        var visual = new VisualEffectsArea
        {
            OutlineStyle = outlineStyle,
            OutlineWidth = ComputedStyle.ResolveOutlineWidthFused(outlineStyle, find("outline-width"), size, dimensions),
            OutlineOffset = ComputedStyle.ResolveOutlineOffsetFused(find("outline-offset"), size, dimensions),
        };

        var decoration = ComputedStyle.ResolveDecorationFused(find);
        var textDecoration = new TextDecorationArea
        {
            Underline = decoration.Underline,
            LineThrough = decoration.LineThrough,
        };

        var color = ComputedStyle.ResolveColorFused(
            element, find("color"), parent.Color, ComputedStyle.ResolveLinkColorFused(element));

        var direction = ComputedStyle.ResolveDirectionFused(element, find("direction"), parent.Direction);
        var writingMode = ComputedStyle.ResolveWritingModeFused(find("writing-mode"), parent.WritingMode);
        var orientation = ComputedStyle.ResolveTextOrientationFused(find("text-orientation"), parent.Orientation);
        var combine = ComputedStyle.ResolveTextCombineFused(find("text-combine-upright"), parent.CombinesUpright);

        var keywords = new ComputedStyle.FusedKeywords
        {
            WritingMode = writingMode,
            Direction = direction,
            Orientation = orientation,
            CombinesUpright = combine,
            Color = color,
            BackgroundColor = ComputedStyle.ResolveBackgroundColorFused(find("background-color")),
            Backgrounds = ComputedStyle.ResolveBackgroundsFused(find, element, dimensions, size),
            Cursor = ComputedStyle.ResolveCursorFused(element, find("cursor"), parent.Cursor),
            Ruby = ComputedStyle.ResolveRubyPositionFused(find("ruby-position"), parent.Ruby),
            OutlineColor = ComputedStyle.ResolveOutlineColorFused(find("outline-color"), color),
            BackgroundClip = ComputedStyle.ResolveBackgroundClipFused(find("background-clip")),
            BorderTopColor = ComputedStyle.ResolveBorderColorFused(find("border-top-color"), color),
            BorderRightColor = ComputedStyle.ResolveBorderColorFused(find("border-right-color"), color),
            BorderBottomColor = ComputedStyle.ResolveBorderColorFused(find("border-bottom-color"), color),
            BorderLeftColor = ComputedStyle.ResolveBorderColorFused(find("border-left-color"), color),
        };

        var values = new ComputedStyle.StyleValues();
        values.Font = font;
        values.Text = text;
        values.Border = border;
        values.BoxModel = boxModel;
        values.DisplayPositioning = display;
        values.VisualEffects = visual;
        values.Decoration = textDecoration;
        values.Position = PositionStyle.ReadFused(find, size, dimensions);

        // The table longhands are five property lookups no element that is not part of a table has a
        // use for, so they are only read where the display says the box is one. Every spelling of it
        // - table, inline-table, table-row, table-cell and the rest - carries the word.
        values.Table = display.Display.Contains("table", StringComparison.OrdinalIgnoreCase)
            ? TableStyle.ReadFused(find, text.VerticalAlign, size, dimensions)
            : null;

        // The flex longhands are read for a flex container - every spelling carries the word - and
        // for a flex item, which only the parent's display can tell apart. Nothing else reads them,
        // and a box that reads one without being either gets the initial values.
        var flex = display.Display.Contains("flex", StringComparison.OrdinalIgnoreCase);

        values.Flex = flex || parent.InFlex
            ? FlexStyle.ReadFused(find, size, dimensions)
            : null;

        // The grid longhands are read for a container - every spelling carries the word - while an
        // item is recognised by its parent's display and asked only for its own placement.
        values.Grid = display.Display.Contains("grid", StringComparison.OrdinalIgnoreCase)
            ? GridStyle.ReadFused(find, size, dimensions)
            : null;

        values.GridItem = parent.InGrid ? GridItemStyle.ReadFused(find) : null;

        values.Fused = keywords;

        return new ComputedStyle(dimensions, element, keywords, values, faces);
    }

    /// <summary>
    /// Resolves the writing mode the element settles on, for the thickness flow mapping.
    /// </summary>
    private static WritingModeInfo DetermineFlow(IElement element, MatchedCascade cascade, FusedParent parent)
    {
        var mode = ComputedStyle.ResolveWritingModeFused(cascade.Find("writing-mode"), parent.WritingMode);
        var direction = ComputedStyle.ResolveDirectionFused(element, cascade.Find("direction"), parent.Direction);

        if (mode == WritingMode.HorizontalTb)
        {
            return direction == TextDirection.LeftToRight
                ? WritingModeInfo.Horizontal
                : new WritingModeInfo(WritingMode.HorizontalTb, TextOrientation.Mixed, false, direction);
        }

        var orientation = ComputedStyle.ResolveTextOrientationFused(cascade.Find("text-orientation"), parent.Orientation);
        var combine = ComputedStyle.ResolveTextCombineFused(cascade.Find("text-combine-upright"), parent.CombinesUpright);

        return new WritingModeInfo(mode, orientation ?? TextOrientation.Mixed, combine ?? false, direction);
    }
}

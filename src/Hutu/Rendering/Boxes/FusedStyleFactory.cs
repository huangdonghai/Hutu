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
    public StyleLength Indent { get; init; } = StyleLength.Zero;
    public TextDirection Direction { get; init; } = TextDirection.LeftToRight;
    public WritingMode WritingMode { get; init; } = WritingMode.HorizontalTb;
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
        LineHeightHit = cascade.Find("line-height"),
        WhiteSpace = style.WhiteSpace,
        Align = style.TextAlign,
        Indent = style.TextIndent,
        Direction = style.Direction,
        WritingMode = style.WritingMode,
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
            VerticalAlign = ComputedStyle.ResolveVerticalAlignFused(find("vertical-align")),
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
            Ruby = ComputedStyle.ResolveRubyPositionFused(find("ruby-position")),
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

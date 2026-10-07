using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.RenderTree;
using AngleSharp.Css.Values;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using System;
using System.Collections.Generic;
using System.Text;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// Turns the styled render tree into a box tree.
/// </summary>
/// <remarks>
/// The render tree is a faithful projection of the DOM with computed styles; it is not a
/// formatting structure. This builder applies the two rules that make it one: subtrees whose
/// <c>display</c> is <c>none</c> are dropped, and - inside a block that mixes block-level content -
/// runs of inline-level content are wrapped in anonymous blocks so the layout engine sees a single
/// formatting context per container.
/// </remarks>
public static class BoxBuilder
{
    /// <summary>
    /// Builds the box tree for the given render tree, or null when there is nothing to render.
    /// </summary>
    /// <param name="images">
    /// Supplies the intrinsic size of images. Optional: without it a replaced box falls back to the
    /// declared attributes and then to the specification's default object size.
    /// </param>
    public static Box? Build(IRenderNode? renderTree, IRenderDimensions dimensions, IImageProvider? images = null)
    {
        if (renderTree is not ElementRenderNode root)
        {
            return null;
        }

        var box = BuildElement(root, dimensions, images, WritingModeInfo.Horizontal);

        if (box is not null)
        {
            MarkBasisUse(box);
        }

        return box;
    }

    /// <summary>
    /// Marks every box whose intrinsic size is the same whatever containing block it is measured
    /// against.
    /// </summary>
    /// <remarks>
    /// A percentage is the only length that reads the containing block; every other one has already
    /// been folded into pixels by the time the style is read. So a box is free of the basis when
    /// neither it nor any descendant resolves one - a fact about the whole subtree, and therefore
    /// only knowable on the way back up it. The layout engine keys its intrinsic cache on the answer,
    /// which is why being wrong in the safe direction - calling a box dependent when it is not -
    /// costs nothing but time, while being wrong the other way serves a size the box never wanted.
    /// </remarks>
    private static void MarkBasisUse(Box box)
    {
        var free = !ReadsContainingBlock(box.Style);

        foreach (var child in box.Children)
        {
            MarkBasisUse(child);
            free &= child.IntrinsicIgnoresBasis;
        }

        box.IntrinsicIgnoresBasis = free;
    }

    /// <summary>Gets whether any length of the style is a fraction of the containing block.</summary>
    private static Boolean ReadsContainingBlock(ComputedStyle style) =>
        ReadsContainingBlock(style.Width) ||
        ReadsContainingBlock(style.Height) ||
        ReadsContainingBlock(style.MinWidth) ||
        ReadsContainingBlock(style.MaxWidth) ||
        ReadsContainingBlock(style.MinHeight) ||
        ReadsContainingBlock(style.MaxHeight) ||
        ReadsContainingBlock(style.Margin) ||
        ReadsContainingBlock(style.Padding);

    private static Boolean ReadsContainingBlock(StyleThickness thickness) =>
        ReadsContainingBlock(thickness.Top) ||
        ReadsContainingBlock(thickness.Right) ||
        ReadsContainingBlock(thickness.Bottom) ||
        ReadsContainingBlock(thickness.Left);

    private static Boolean ReadsContainingBlock(StyleLength length) => length.Kind switch
    {
        StyleLengthKind.Percent => true,

        // A math function is only as free of the basis as its arguments are: `min(100%, 640px)` reads
        // the containing block through one of them, `min(20em, 640px)` through none.
        StyleLengthKind.Math => ReadsContainingBlock(length.Operands),

        _ => false,
    };

    /// <summary>
    /// Gets whether any argument of a <c>min()</c>, <c>max()</c> or <c>clamp()</c> reads the
    /// containing block. Arguments that cannot be read at all count as dependent, which errs towards
    /// measuring the box again rather than towards reusing a size it may never have asked for.
    /// </summary>
    private static Boolean ReadsContainingBlock(IReadOnlyList<StyleLength>? operands)
    {
        if (operands is null)
        {
            return true;
        }

        foreach (var operand in operands)
        {
            if (ReadsContainingBlock(operand))
            {
                return true;
            }
        }

        return false;
    }

    private static Box? BuildElement(
        ElementRenderNode node,
        IRenderDimensions dimensions,
        IImageProvider? images,
        WritingModeInfo inherited,
        Double parentFontSize = 0,
        CssColorValue? parentColor = null)
    {
        var style = new ComputedStyle(
            node.ComputedStyle,
            dimensions,
            node.SpecifiedStyle,
            node.Ref,
            parentFontSize,
            parentColor,
            DeclaresRelativeFontSize(node.Ref, node.SpecifiedStyle, dimensions));

        if (style.IsDisplayNone)
        {
            return null;
        }

        var element = node.Ref;
        var kind = ResolveKind(element, style);
        var flow = ResolveFlow(style, inherited);

        if (kind == BoxKind.HorizontalRule)
        {
            return new Box(kind, style) { Element = element, Flow = flow };
        }

        if (element is IHtmlImageElement image)
        {
            return BuildReplaced(image, kind, style, flow, images);
        }

        var children = new List<Box>(node.Children is ICollection<IRenderNode> collection ? collection.Count : 0);

        // Resolved once: working out the colour of a link costs a second selector match, and every one
        // of the link's children asks its parent box for the same answer.
        var color = style.Color;

        foreach (var child in node.Children)
        {
            switch (child)
            {
                case ElementRenderNode elementChild:
                    var childBox = BuildElement(elementChild, dimensions, images, flow, style.FontSize, color);

                    if (childBox is not null)
                    {
                        children.Add(childBox);
                    }

                    break;

                case TextRenderNode textChild:
                    var textBox = BuildText(textChild, style, flow);

                    if (textBox is not null)
                    {
                        children.Add(textBox);
                    }

                    break;
            }
        }

        if (kind == BoxKind.Inline && HasBlockLevel(children))
        {
            // An inline box holding block-level content cannot be laid out as a line box. Promoting
            // it to a block container approximates the block-in-inline fixup browsers perform and,
            // unlike ignoring the children, never loses content.
            kind = BoxKind.Block;
        }

        var box = new Box(kind, style) { Element = element, Flow = flow };

        switch (kind)
        {
            // A cell is an ordinary block container; only the table context above it is different.
            // So is an inline-block, which is a block container that the surrounding line places as
            // one unit - it is the outer role, not the inner one, that the line box sees.
            case BoxKind.Block:
            case BoxKind.InlineBlock:
            case BoxKind.TableCell:
                GroupChildren(box, children);
                break;

            // A flex or grid container's children are all items, so a run of inline content is one
            // item even when there is nothing else around it - it is wrapped rather than left to
            // form a line box, which is what makes a bare word inside either container behave.
            case BoxKind.Flex:
            case BoxKind.Grid:
                GroupChildren(box, children, true);
                break;

            case BoxKind.Table:
                box.Children.AddRange(children);
                TableFixup.NormalizeTable(box);
                break;

            case BoxKind.TableRowGroup:
                box.Children.AddRange(children);
                TableFixup.NormalizeRowGroup(box);
                break;

            case BoxKind.TableRow:
                box.Children.AddRange(children);
                TableFixup.NormalizeRow(box);
                break;

            default:
                box.Children.AddRange(children);
                break;
        }

        return box;
    }

    /// <summary>
    /// Gets whether the element declares a font-relative <c>font-size</c> of its own rather than taking
    /// one over from a parent.
    /// </summary>
    /// <remarks>
    /// A relative font size the element declares is measured against its parent, while an inherited one
    /// is already resolved. Telling the two apart costs a second cascade, so it is only paid where the
    /// answer changes anything: a size in pixels or an absolute unit means the same thing either way.
    /// </remarks>
    private static Boolean DeclaresRelativeFontSize(
        IElement element,
        ICssStyleDeclaration specified,
        IRenderDimensions dimensions) =>
        specified.GetProperty("font-size")?.RawValue is CssLengthValue size &&
        ComputedStyle.IsFontRelative(size.Type) &&
        ComputedStyle.Declares(element, dimensions, "font-size");

    private static Boolean HasBlockLevel(List<Box> children)
    {
        foreach (var child in children)
        {
            if (child.IsBlockLevel)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the box for an image. The intrinsic size is preferred, then the declared width and
    /// height attributes, and finally the specification's default object size - so a box of usable
    /// geometry always exists, whatever happened to the resource.
    /// </summary>
    private static Box BuildReplaced(
        IHtmlImageElement image,
        BoxKind kind,
        ComputedStyle style,
        WritingModeInfo flow,
        IImageProvider? images)
    {
        var url = ImageUrlResolver.Resolve(image, image.Source);
        var intrinsic = url is null ? null : images?.GetSize(url);

        if (intrinsic is not { IsValid: true })
        {
            var attributeWidth = image.DisplayWidth;
            var attributeHeight = image.DisplayHeight;

            intrinsic = attributeWidth > 0 && attributeHeight > 0
                ? new ImageSize(attributeWidth, attributeHeight)
                : new ImageSize(300, 150);
        }

        return new Box(kind, style)
        {
            Element = image,
            IsReplaced = true,
            SourceUrl = url,
            IntrinsicSize = intrinsic,
            Flow = flow,
        };
    }

    /// <summary>
    /// Resolves the writing mode a box lays its content out in: the mode the cascade already carried
    /// down, and the orientation and combining its element declares or, failing that, the ones its
    /// ancestors run in.
    /// </summary>
    private static WritingModeInfo ResolveFlow(ComputedStyle style, WritingModeInfo inherited)
    {
        var mode = style.WritingMode;
        var direction = style.Direction;

        if (mode == Layout.WritingMode.HorizontalTb)
        {
            return direction == TextDirection.LeftToRight
                ? WritingModeInfo.Horizontal
                : new WritingModeInfo(Layout.WritingMode.HorizontalTb, TextOrientation.Mixed, false, direction);
        }

        return new WritingModeInfo(
            mode,
            style.DeclaredTextOrientation ?? inherited.Orientation,
            style.DeclaredTextCombineUpright ?? inherited.CombinesUpright,
            direction);
    }

    private static BoxKind ResolveKind(IElement element, ComputedStyle style)
    {
        if (IsTag(element, "br"))
        {
            return BoxKind.LineBreak;
        }

        if (IsTag(element, "hr"))
        {
            return BoxKind.HorizontalRule;
        }

        // Ruby is matched by tag rather than by display, because the annotation only means anything
        // as the partner of its base: flattened into the line it would simply read as text after it.
        if (IsTag(element, "ruby"))
        {
            return BoxKind.Ruby;
        }

        if (IsTag(element, "rt") || IsTag(element, "rtc"))
        {
            return BoxKind.RubyText;
        }

        if (IsTag(element, "rb"))
        {
            return BoxKind.RubyBase;
        }

        var display = style.Display;

        // The table spellings are matched before the inline test, because `inline-table` starts with
        // "inline" but is a table. The engine does not model the outer role of `inline-table`, so it
        // is treated as a block-level table.
        switch (display.ToLowerInvariant())
        {
            case "table":
            case "inline-table":
                return BoxKind.Table;

            case "table-row-group":
            case "table-header-group":
            case "table-footer-group":
                return BoxKind.TableRowGroup;

            case "table-row":
                return BoxKind.TableRow;

            case "table-cell":
                return BoxKind.TableCell;

            case "table-caption":
                return BoxKind.TableCaption;

            case "table-column":
                return BoxKind.TableColumn;

            case "table-column-group":
                return BoxKind.TableColumnGroup;
        }

        // A flex or grid container is a container whichever form it takes; the engine does not yet
        // model the outer role of `inline-flex` or `inline-grid`, so both spellings of each produce
        // the same kind.
        if (display.StartsWith("flex", StringComparison.OrdinalIgnoreCase) ||
            display.StartsWith("inline-flex", StringComparison.OrdinalIgnoreCase))
        {
            return BoxKind.Flex;
        }

        if (display.StartsWith("grid", StringComparison.OrdinalIgnoreCase) ||
            display.StartsWith("inline-grid", StringComparison.OrdinalIgnoreCase))
        {
            return BoxKind.Grid;
        }

        // An inline-block is the one inline-level box that is a block container in its own right, so
        // it has to be picked out before the catch-all below, which would otherwise let it fall in
        // with the elements that are laid out as runs on the line.
        if (display.Equals("inline-block", StringComparison.OrdinalIgnoreCase))
        {
            return BoxKind.InlineBlock;
        }

        // Everything else that starts with "inline" (inline, inline-flex, inline-grid, inline-table)
        // takes part in the surrounding line box as a run; of those, the container forms are still
        // laid out as the block-level containers they are named after, which is a known gap. The rest
        // is treated as block-level for now.
        return display.StartsWith("inline", StringComparison.OrdinalIgnoreCase)
            ? BoxKind.Inline
            : BoxKind.Block;
    }

    private static Box? BuildText(TextRenderNode node, ComputedStyle parentStyle, WritingModeInfo flow)
    {
        var raw = node.Ref.Data;

        if (String.IsNullOrEmpty(raw))
        {
            return null;
        }

        var text = parentStyle.IsPreformatted ? raw : CollapseWhitespace(raw);

        if (text.Length == 0)
        {
            return null;
        }

        return new Box(BoxKind.Text, parentStyle) { Source = node.Ref, Text = text, Flow = flow };
    }

    /// <summary>
    /// Wraps runs of inline-level boxes in anonymous blocks when the container also has block-level
    /// children, and drops white-space-only text that only separates blocks.
    /// </summary>
    /// <param name="wrapInline">
    /// Whether inline content is wrapped even when the container has no block-level children. A grid
    /// container needs it, because every child of a grid is an item and a run of inline content is
    /// therefore one item rather than a line box.
    /// </param>
    internal static void GroupChildren(Box parent, List<Box> children, Boolean wrapInline = false)
    {
        var hasBlock = false;

        foreach (var child in children)
        {
            if (child.IsBlockLevel)
            {
                hasBlock = true;
                break;
            }
        }

        // A block whose content is all inline-level is itself the inline formatting context.
        if (!hasBlock)
        {
            if (wrapInline && children.Count > 0)
            {
                var item = new Box(BoxKind.Block, parent.Style) { IsAnonymous = true, Flow = parent.Flow };
                item.Children.AddRange(children);
                parent.Children.Add(item);
            }
            else
            {
                parent.Children.AddRange(children);
            }

            return;
        }

        var index = 0;

        while (index < children.Count)
        {
            var child = children[index];

            if (child.IsBlockLevel)
            {
                parent.Children.Add(child);
                index++;
                continue;
            }

            var run = new List<Box>();
            var hasVisible = false;

            while (index < children.Count && children[index].IsInlineLevel)
            {
                var item = children[index];
                run.Add(item);

                if (item.Kind != BoxKind.Text || !String.IsNullOrWhiteSpace(item.Text))
                {
                    hasVisible = true;
                }

                index++;
            }

            // White space between two blocks collapses away and generates no box.
            if (!hasVisible)
            {
                continue;
            }

            var anonymous = new Box(BoxKind.Block, parent.Style) { IsAnonymous = true, Flow = parent.Flow };
            anonymous.Children.AddRange(run);
            parent.Children.Add(anonymous);
        }
    }

    private static Boolean IsTag(IElement element, String name) =>
        String.Equals(element.LocalName, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Collapses every run of ASCII white space to a single space, leaving the rest untouched. The
    /// result may still be white space only; line layout decides whether it survives.
    /// </summary>
    private static String CollapseWhitespace(String text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var character in text)
        {
            if (character is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            builder.Append(character);
        }

        if (pendingSpace && builder.Length > 0)
        {
            builder.Append(' ');
        }

        return builder.ToString();
    }
}

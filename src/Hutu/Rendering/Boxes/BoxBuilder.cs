using AngleSharp.Css;
using AngleSharp.Css.RenderTree;
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

        return BuildElement(root, dimensions, images);
    }

    private static Box? BuildElement(ElementRenderNode node, IRenderDimensions dimensions, IImageProvider? images)
    {
        var style = new ComputedStyle(node.ComputedStyle, dimensions, node.SpecifiedStyle, node.Ref);

        if (style.IsDisplayNone)
        {
            return null;
        }

        var element = node.Ref;
        var kind = ResolveKind(element, style);

        if (kind == BoxKind.HorizontalRule)
        {
            return new Box(kind, style) { Element = element };
        }

        if (element is IHtmlImageElement image)
        {
            return BuildReplaced(image, kind, style, images);
        }

        var children = new List<Box>(node.Children is ICollection<IRenderNode> collection ? collection.Count : 0);

        foreach (var child in node.Children)
        {
            switch (child)
            {
                case ElementRenderNode elementChild:
                    var childBox = BuildElement(elementChild, dimensions, images);

                    if (childBox is not null)
                    {
                        children.Add(childBox);
                    }

                    break;

                case TextRenderNode textChild:
                    var textBox = BuildText(textChild, style);

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

        var box = new Box(kind, style) { Element = element };

        switch (kind)
        {
            // A cell is an ordinary block container; only the table context above it is different.
            case BoxKind.Block:
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
        };
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

        // Everything that starts with "inline" (inline, inline-block, inline-flex, ...) takes part
        // in the surrounding line box; the rest is treated as block-level for now.
        return display.StartsWith("inline", StringComparison.OrdinalIgnoreCase)
            ? BoxKind.Inline
            : BoxKind.Block;
    }

    private static Box? BuildText(TextRenderNode node, ComputedStyle parentStyle)
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

        return new Box(BoxKind.Text, parentStyle) { Source = node.Ref, Text = text };
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
                var item = new Box(BoxKind.Block, parent.Style) { IsAnonymous = true };
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

            var anonymous = new Box(BoxKind.Block, parent.Style) { IsAnonymous = true };
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

using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using System;
using System.Collections.Generic;
using System.Text;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// Turns the styled DOM into a box tree.
/// </summary>
/// <remarks>
/// The engine renders through <see cref="TryBuildFused"/>, which builds the
/// box tree straight from the DOM.
/// </remarks>
public static partial class BoxBuilder
{
    /// <summary>
    /// Gives the page the writing mode the <c>body</c> runs in, when the root declares none of its own.
    /// </summary>
    /// <remarks>
    /// A vertical book states its direction on the <c>body</c> - that is where the format puts it, and
    /// where the readers that show vertical Chinese look for it - which leaves the root horizontal and
    /// the body in an orthogonal flow. An orthogonal box with an automatic inline size has no definite
    /// extent to fill, so it takes the extent its content asks for instead: every paragraph becomes one
    /// column as tall as its whole text, and the chapter becomes a band of them tens of thousands of
    /// pixels wide, of which the viewport shows the last one.
    /// <para>
    /// The page box is the one the book asked for. The root runs the way the body does, so a column is
    /// as long as the viewport is tall and the chapter is as wide as its columns make it.
    /// </para>
    /// <para>
    /// Only the root is touched: every box under the body carries the mode the body asked for already,
    /// and one that inherits the root's is horizontal in the cascade whatever this settles on.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The same, with the root's own declaration already settled by the caller - the fused
    /// path reads it from the cascade it already has.
    /// </summary>
    private static void AdoptBodyFlow(Box root, Boolean rootDeclaresWritingMode)
    {
        if (root.Element is null || rootDeclaresWritingMode)
        {
            return;
        }

        foreach (var child in root.Children)
        {
            if (child.Element is { } body && IsTag(body, "body") && child.Flow.IsVertical)
            {
                root.Flow = child.Flow;
                return;
            }
        }
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
    /// Builds the box for an image. The picture that arrived answers the intrinsic size, and the
    /// <c>width</c>/<c>height</c> attributes are kept beside it as the presentational hint they are - so
    /// a box of usable geometry always exists whatever happened to the resource, and a box whose
    /// picture did arrive is still the size the page asked for rather than the size the bitmap happens
    /// to be.
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
        var declared = DeclaredSize(image);

        if (intrinsic is not { IsValid: true })
        {
            // Nothing decoded to size from, so the document's own numbers stand in for the picture's -
            // both axes of them, because half a declaration gives no ratio to lay out against - and the
            // specification's default object size answers for a page that declared nothing.
            intrinsic = declared is { IsValid: true } stated ? stated : new ImageSize(300, 150);
        }

        return new Box(kind, style)
        {
            Element = image,
            IsReplaced = true,
            SourceUrl = url,
            IntrinsicSize = intrinsic,
            AttributeSize = declared,
            Flow = flow,
        };
    }

    /// <summary>
    /// The size the element's <c>width</c>/<c>height</c> attributes declare. A missing or unreadable
    /// attribute leaves that axis zero, which the sizing reads as <c>auto</c> and answers from the
    /// picture's ratio - the way a page that states only the width still gets a proportional height.
    /// </summary>
    private static ImageSize? DeclaredSize(IHtmlImageElement image)
    {
        var width = image.DisplayWidth;
        var height = image.DisplayHeight;

        return width > 0 || height > 0
            ? new ImageSize(width, height)
            : null;
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

        // A float joins its container's block flow rather than the line it would have sat on: the
        // specification blockifies the inline-level outer roles before any box is generated, so
        // `display: inline` with `float: left` is a block that happens to be pulled to one edge, and
        // `inline-block` under a float is that same block container minus the line. The two are
        // indistinguishable from here on: the engine arranges an inline-block exactly as it arranges
        // a block, and only the line that would have held it knows the difference.
        if (style.Float != FloatSide.None &&
            display.StartsWith("inline", StringComparison.OrdinalIgnoreCase))
        {
            return BoxKind.Block;
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

    /// <summary>
    /// The heart of the text-building logic, shared with the fused walk that
    /// reaches text through the DOM rather than the render tree.
    /// </summary>
    private static String? BuildTextValue(String raw, ComputedStyle parentStyle, WritingModeInfo flow)
    {
        if (String.IsNullOrEmpty(raw))
        {
            return null;
        }

        var text = parentStyle.IsPreformatted ? raw : CollapseWhitespace(raw);

        return text.Length == 0 ? null : text;
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
    /// result may still be white space only; line layout decides whether it survives. A space at
    /// either end of a node survives the collapse as well: whether it is a line's start or end, or
    /// the one space an element's edge sits between, is a question for the line, not the node.
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

            if (pendingSpace)
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            builder.Append(character);
        }

        if (pendingSpace)
        {
            builder.Append(' ');
        }

        return builder.ToString();
    }
}

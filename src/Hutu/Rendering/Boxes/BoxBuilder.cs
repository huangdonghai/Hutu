using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// Turns the styled DOM into a box tree.
/// </summary>
/// <remarks>
/// The engine renders through <see cref="TryBuildFused"/>, which builds the
/// box tree straight from the DOM.
/// </remarks>
internal static partial class BoxBuilder
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
        // CSS Contain 1 §2: any containment on the root or the body - not only the layout kind - turns
        // the body's special handling off, so its writing mode stops at its own box.
        if (root.Element is null || rootDeclaresWritingMode || root.Style.Contain.Stated)
        {
            return;
        }

        foreach (var child in root.Children)
        {
            if (child.Element is { } body && IsTag(body, "body") && child.Flow.IsVertical &&
                !child.Style.Contain.Stated)
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

    /// <summary>
    /// Gets whether the children hold content that cannot sit in a line, which is what makes an inline
    /// box a block container rather than one. A box that left the flow is not such content: it is laid out
    /// against a containing block of its own and takes no part in the lines of the box it stands in, so a
    /// span that holds nothing but an absolutely positioned box stays the inline box the page wrote - with
    /// its own padding painting outward, and with the place in the line the out-of-flow box would have had
    /// standing as that box's static position (CSS 2.1 §9.2.1.1, §9.4.1, §10.3.7).
    /// </summary>
    private static Boolean HasInFlowBlockLevel(List<Box> children)
    {
        foreach (var child in children)
        {
            if (child.IsBlockLevel && !PositionedLayout.IsOutOfFlow(child))
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

    /// <summary>Gets whether the element is an inline <c>&lt;svg&gt;</c> root (not a child shape).</summary>
    private static Boolean IsInlineSvgRoot(IElement element) =>
        element.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase) &&
        element.NamespaceUri.Equals("http://www.w3.org/2000/svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the box for an inline <c>&lt;svg&gt;</c>: a replaced box sized by its own geometry whose
    /// paintable content is the element serialized to standalone markup. The <c>width</c>/<c>height</c>
    /// attributes give the intrinsic size, a <c>viewBox</c> supplies the ratio when only one axis is
    /// stated, and the specification's default object size answers for a bare <c>&lt;svg&gt;</c>.
    /// </summary>
    private static Box BuildInlineSvg(IElement svg, BoxKind kind, ComputedStyle style, WritingModeInfo flow)
    {
        var width = AttributeLength(svg, "width");
        var height = AttributeLength(svg, "height");
        var stated = ViewBoxSize(svg);

        ImageSize intrinsic = (width, height) switch
        {
            (not null, not null) => new ImageSize(width.Value, height.Value),
            (not null, null) when stated is { } both => new ImageSize(width.Value, width.Value * both.Height / both.Width),
            (null, not null) when stated is { } both => new ImageSize(height.Value * both.Width / both.Height, height.Value),
            _ => new ImageSize(300, 150),
        };

        ImageSize? declared = width is not null || height is not null
            ? new ImageSize(width ?? 0, height ?? 0)
            : null;

        return new Box(kind, style)
        {
            Element = svg,
            IsReplaced = true,
            IsInlineSvg = true,
            IntrinsicSize = intrinsic,
            AttributeSize = declared,
            SvgMarkup = SvgSerializer.Serialize(svg),
            Flow = flow,
        };
    }

    /// <summary>A plain-number <c>width</c>/<c>height</c> attribute in user units; null for a percentage or a missing one.</summary>
    private static Double? AttributeLength(IElement element, String name)
    {
        var text = element.GetAttribute(name);

        if (String.IsNullOrWhiteSpace(text) || text.Contains('%'))
        {
            return null;
        }

        var end = 0;

        while (end < text.Length && (Char.IsDigit(text[end]) || text[end] is '.' or '-' or '+'))
        {
            end++;
        }

        return end > 0 &&
               Double.TryParse(text[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
               value > 0
            ? value
            : null;
    }

    /// <summary>The width and height a <c>viewBox</c> declares, or null when there is none to read.</summary>
    private static (Double Width, Double Height)? ViewBoxSize(IElement element)
    {
        var text = element.GetAttribute("viewBox");

        if (String.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split(new[] { ' ', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 4 &&
            Double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width) &&
            Double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height) &&
            width > 0 && height > 0)
        {
            return (width, height);
        }

        return null;
    }

    /// <summary>
    /// Resolves the writing mode a box lays its content out in: the mode the cascade already carried
    /// down, and the orientation and combining its element declares or, failing that, the ones its
    /// ancestors run in.
    /// </summary>
    private static WritingModeInfo ResolveFlow(ComputedStyle style, WritingModeInfo inherited, IElement? element)
    {
        var mode = style.WritingMode;
        var direction = style.Direction;

        // A plaintext paragraph's direction is a fact about the text on it rather than about what the
        // page declared, so the first character of strong direction settles which way its line reads
        // and the `direction` the cascade carried is dropped (UAX#9 §3.4).
        if (element is not null &&
            style.PlaintextParagraph &&
            TextChunker.StrongDirection(RenderedText(element)) is { } read)
        {
            direction = read;
        }

        if (mode == Layout.WritingMode.HorizontalTb)
        {
            return direction == TextDirection.LeftToRight
                ? WritingModeInfo.Horizontal
                : new WritingModeInfo(Layout.WritingMode.HorizontalTb, TextOrientation.Mixed, false, direction);
        }

        // Each sideways mode turns every glyph by itself, so there is no upright run left for
        // `text-combine-upright` to make and the property has no effect there (CSS Writing Modes 3 §5.1).
        var sideways = mode is Layout.WritingMode.SidewaysRl or Layout.WritingMode.SidewaysLr;

        return new WritingModeInfo(
            mode,
            style.DeclaredTextOrientation ?? inherited.Orientation,
            !sideways && (style.DeclaredTextCombineUpright ?? inherited.CombinesUpright),
            direction);
    }

    /// <summary>
    /// Gets the text an element holds on its lines, which is what a plaintext paragraph is read out of.
    /// The parts of a document that reach no line - a script's source, a style sheet, a title, the
    /// unused markup of a template - are left out, as they are of the rendering.
    /// </summary>
    private static String RenderedText(IElement element)
    {
        var text = new StringBuilder();

        AppendRenderedText(element, text);

        return text.ToString();
    }

    private static void AppendRenderedText(INode node, StringBuilder text)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText)
            {
                text.Append(child.TextContent);
            }
            else if (child is IElement { LocalName: not ("script" or "style" or "template" or "title") } element)
            {
                AppendRenderedText(element, text);
            }
        }
    }

    private static BoxKind ResolveKind(IElement element, ComputedStyle style) =>
        ResolveKind(element, style, inTable: true);

    /// <summary>
    /// Gets the kind of box an element comes to.
    /// </summary>
    /// <param name="inTable">
    /// Whether the element stands in a table, which is what decides whether a table part keeps the role
    /// its element gives it (CSS Display 3 §2.7).
    /// </param>
    private static BoxKind ResolveKind(IElement element, ComputedStyle style, Boolean inTable)
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

        // A table part has two roles, and the page may change only one of them (CSS Display 3 §2.7).
        // The inner role is what the element is - a row group, a row, a cell - and it does not change:
        // the user agent sheet's `tbody { display: table-row-group }` says what the children of a
        // tbody are, whether the page went on to write `display: flex` for it. The outer role is what
        // box the part is, and that is the one the page writes. So a part written `display: flex` is a
        // flex container and not a row group at all, and the rows inside it are rows of nothing.
        if (TableRole(element) is { } role)
        {
            // A table part has two roles, and the page may change only the outer one (CSS Display 3
            // §2.7). The inner role is what the element is - a row group, a row, a cell - and it does
            // not change; the outer role is what box the part is, and that is the one the page writes.
            //
            // A part that is no longer in a table is not the part its role says it is: the rows of a
            // row group that is a flex container are rows of nothing, and a row that is not in a table
            // is a block - the user agent's `tr { display: table-row }` is conditional on the table, and
            // without it the row has nothing to be. A cell is the exception: it keeps the role the
            // sheet gives it, and the cells that stand together make an anonymous table (CSS 2.1
            // §17.2.1).
            if (IsTableSpelling(display))
            {
                if (!inTable)
                {
                    return role == BoxKind.TableCell ? role : BoxKind.Block;
                }

                // In a table, a part takes the role the spelling names: a cell written `table-row` is
                // a row, and the table lays it out as one.
                return display.Equals(RoleDisplay(element), StringComparison.OrdinalIgnoreCase)
                    ? role
                    : OuterRole(display);
            }

            // The page wrote something else, and that is the role the box takes: a row group written
            // `display: flex` is a flex container, and the rows inside it are rows of nothing. A cell
            // written `display: flex` is one too - measured in Chrome on 2026-10-23, where the two spans
            // it holds stand side by side as items while the table still gives the cell its column - so
            // the spelling the page wrote wins for a cell exactly as it does for the parts above it.
            return OuterRole(display);
        }

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

        // A grid-lanes (masonry) container is its own formatting context, not a grid: items pack into the
        // shortest lane along the main axis. Matched on the inner role - `grid-lanes` or `masonry` anywhere
        // in the display - before the grid test below, whose `StartsWith("grid")` would otherwise claim
        // `grid-lanes` for the grid and miss the `inline grid-lanes` spelling entirely.
        if (display.Contains("grid-lanes", StringComparison.OrdinalIgnoreCase) ||
            display.Contains("masonry", StringComparison.OrdinalIgnoreCase))
        {
            return BoxKind.Masonry;
        }

        // A flex or grid container is a container whichever form it takes: the two spellings of each
        // share a kind, because the kind is the inner role - the outer one, block-level or
        // inline-level, is what the caller reads back off the display and records separately.
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

        // A box that leaves the flow is blockified exactly as a float is: `display: inline` with
        // `position: absolute` is a box placed against its containing block, not a run sitting on the
        // line it was written in. Left inline it is carried by that line, so the offsets never apply and
        // the line is as tall as the box - which is how a logo pinned a hundred and fifty-eight pixels
        // below the head of its block came to be drawn at the head of it, and to make that block twice
        // the height the page asked for.
        if (style.Position.Position is PositionMode.Absolute or PositionMode.Fixed &&
            display.StartsWith("inline", StringComparison.OrdinalIgnoreCase))
        {
            return BoxKind.Block;
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

        // A `contents` element generates no box and its children take part in the parent's formatting
        // context (CSS Display 3 §2.5). The engine is not there yet - the element still comes to a box -
        // and what that box must not do is stand in the block flow: as a run its words stay on the line the
        // parent is laying out, and the width and height the element declared for itself are ignored the
        // way a non-replaced inline's are, which is closer to the answer than a block that pushes the
        // lines apart would be.
        if (display.Equals("contents", StringComparison.OrdinalIgnoreCase))
        {
            return BoxKind.Inline;
        }

        // Everything else that starts with "inline" takes part in the surrounding line box as a run.
        // The container forms - inline-flex among them - were already claimed above, and reach the line
        // as the atomic boxes their kinds name, so what is left here is plain inline content.
        return display.StartsWith("inline", StringComparison.OrdinalIgnoreCase)
            ? BoxKind.Inline
            : BoxKind.Block;
    }

    /// <summary>
    /// Gets whether the container kind a box resolved to carries an inline outer role - the
    /// <c>inline-flex</c>, <c>inline-grid</c>, <c>inline-table</c> and inline masonry spellings.
    /// </summary>
    /// <remarks>
    /// The inner role is what the kind records, so the outer one is read back off the <c>display</c>
    /// the style computed to. Only a box still in normal flow can be inline-level: the specification
    /// blockifies the inline-level outer roles before any box is generated, so a box that left the
    /// flow - absolutely or fixed positioned, or floated - is block-level however its display was
    /// written, and an item of a flex or grid container has its inline role taken away by the caller
    /// that knows it is one.
    /// </remarks>
    internal static Boolean HasInlineOuter(BoxKind kind, ComputedStyle style) =>
        kind is BoxKind.Flex or BoxKind.Grid or BoxKind.Masonry or BoxKind.Table &&
        style.Display.StartsWith("inline", StringComparison.OrdinalIgnoreCase) &&
        style.Position.Position is not (PositionMode.Absolute or PositionMode.Fixed) &&
        style.Float == FloatSide.None;

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

        // `hyphens: none` takes every soft hyphen for nothing at all: it is not an opportunity and it is
        // not drawn (CSS Text 3 §6.1, measured by hyphens-none-011).
        raw = parentStyle.WithoutSoftHyphens(raw);

        // `break-spaces` keeps its runs as written just as the `pre` spellings do; only the modes that
        // collapse their spaces fold the text down here, and `pre-line` keeps the breaks it was written
        // with while folding everything else.
        var text = parentStyle.CollapsesSpaces
            ? CollapseWhitespace(raw, keepBreaks: parentStyle.KeepsSegmentBreaks)
            : raw;

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
        // A child that left the flow is not one of them: it is laid out against a containing block of its
        // own and takes no part in the lines or the stack this box forms. Counting it would wrap the
        // inline content beside it in an anonymous block for no reason - and the box would then be placed
        // against the block cursor, a fresh line, instead of the place in the flow it would have had,
        // which is where an offset left unset falls back to.
        var hasBlock = false;

        foreach (var child in children)
        {
            if (child.IsBlockLevel && !PositionedLayout.IsOutOfFlow(child))
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
                // Anonymous: it has no element, so it has no margin, no padding, no border and no
                // position of its own. Taking the parent's style wholesale would give it the parent's
                // margins, and a body's eight pixels would be counted twice.
                var item = new Box(BoxKind.Block, parent.Style.AsAnonymous()) { IsAnonymous = true, Flow = parent.Flow };

                foreach (var child in children)
                {
                    // A box that left the flow is not part of the run: burying it inside the anonymous
                    // block would hide it from everything that goes looking for one, and it would never
                    // be placed at all.
                    if (PositionedLayout.IsOutOfFlow(child))
                    {
                        parent.Children.Add(child);
                    }
                    else
                    {
                        item.Children.Add(child);
                    }
                }

                if (item.Children.Count > 0)
                {
                    parent.Children.Add(item);
                }
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

                // White space the line rules collapse away leaves no trace, but a no-break space is not
                // among them: CSS Text 3 §4 keeps U+00A0 and the other space separators as content that is
                // measured, is never folded into a neighbour and is never trimmed from either end of a line.
                // The document's own notion of white space counts those as blanks, and a run of five of them
                // beside a floated child was dropped with the blanks instead of laid out as a line.
                if (item.Kind != BoxKind.Text ||
                    (item.Text.Length > 0 && !TextChunker.IsCollapsible(item.Text)))
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

            var anonymous = new Box(BoxKind.Block, parent.Style.AsAnonymous()) { IsAnonymous = true, Flow = parent.Flow };
            anonymous.Children.AddRange(run);
            parent.Children.Add(anonymous);
        }
    }

    private static Boolean IsTag(IElement element, String name) =>
        String.Equals(element.LocalName, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the role a table part has inside a table, or null where the element is not one.
    /// </summary>
    /// <remarks>
    /// The role is what the element is inside a table, and it is the user agent sheet that gives it:
    /// a <c>tbody</c> is a row group, a <c>tr</c> a row, a <c>td</c> a cell. The page may write another
    /// display for the element, which changes what box it is and not what it is.
    /// </remarks>
    private static BoxKind? TableRole(IElement element)
    {
        if (IsTag(element, "tbody"))
        {
            return BoxKind.TableRowGroup;
        }

        if (IsTag(element, "thead"))
        {
            return BoxKind.TableRowGroup;
        }

        if (IsTag(element, "tfoot"))
        {
            return BoxKind.TableRowGroup;
        }

        if (IsTag(element, "tr"))
        {
            return BoxKind.TableRow;
        }

        if (IsTag(element, "td") || IsTag(element, "th"))
        {
            return BoxKind.TableCell;
        }

        if (IsTag(element, "caption"))
        {
            return BoxKind.TableCaption;
        }

        if (IsTag(element, "col"))
        {
            return BoxKind.TableColumn;
        }

        if (IsTag(element, "colgroup"))
        {
            return BoxKind.TableColumnGroup;
        }

        return null;
    }

    /// <summary>
    /// Gets whether a display names a role of the table, which is what keeps a part in it.
    /// </summary>
    private static Boolean IsTableSpelling(String display) => display.ToLowerInvariant() switch
    {
        "table" or "inline-table" or "table-row-group" or "table-header-group" or "table-footer-group" or
        "table-row" or "table-cell" or "table-caption" or "table-column" or "table-column-group" => true,
        _ => false,
    };

    /// <summary>
    /// Gets the display the user agent sheet gives a table part, which is the one that names its role.
    /// </summary>
    /// <remarks>
    /// A row group is a <c>table-row-group</c>, a header group a <c>table-header-group</c> and a footer
    /// group a <c>table-footer-group</c>: the engine has one kind for the three of them, and the
    /// display is what tells them apart.
    /// </remarks>
    private static String RoleDisplay(IElement element)
    {
        if (IsTag(element, "thead"))
        {
            return "table-header-group";
        }

        if (IsTag(element, "tfoot"))
        {
            return "table-footer-group";
        }

        if (IsTag(element, "tbody"))
        {
            return "table-row-group";
        }

        if (IsTag(element, "tr"))
        {
            return "table-row";
        }

        if (IsTag(element, "td") || IsTag(element, "th"))
        {
            return "table-cell";
        }

        if (IsTag(element, "caption"))
        {
            return "table-caption";
        }

        if (IsTag(element, "col"))
        {
            return "table-column";
        }

        if (IsTag(element, "colgroup"))
        {
            return "table-column-group";
        }

        return String.Empty;
    }

    /// <summary>
    /// Gets the kind of box a display names, for an element that is not a table part.
    /// </summary>
    private static BoxKind OuterRole(String display)
    {
        // A table spelling names a table part whatever the element is: a cell written `table-row` is
        // a row (CSS Display 3 §2.7 - the outer role is what the page wrote, and a table spelling is
        // a role of the table). It is still in the table, so the table's algorithms lay it out.
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

        if (display.Contains("grid-lanes", StringComparison.OrdinalIgnoreCase) ||
            display.Contains("masonry", StringComparison.OrdinalIgnoreCase))
        {
            return BoxKind.Masonry;
        }

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

        return display.StartsWith("inline", StringComparison.OrdinalIgnoreCase)
            ? BoxKind.Inline
            : BoxKind.Block;
    }

    /// <summary>
    /// Collapses every run of the white space the line rules may collapse into a single space, leaving
    /// the rest untouched. The result may still be white space only; line layout decides whether it
    /// survives. A space at either end of a node survives the collapse as well: whether it is a line's
    /// start or end, or the one space an element's edge sits between, is a question for the line, not
    /// the node.
    /// </summary>
    /// <remarks>
    /// The predicate is the line's own, which CSS Text 3 §4.2 keeps to the space, the tab and the
    /// segment breaks. A form feed is not among them: it is a control character, and a control
    /// character is drawn as a glyph of its own and stands as content between two words rather than
    /// folding into one of the spaces around it. Reading the document language's notion of an ASCII
    /// space here instead is what made the node and the line disagree about what the text was made of.
    /// </remarks>
    private static String CollapseWhitespace(String text, Boolean keepBreaks)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var character in text)
        {
            if (keepBreaks && character == '\n')
            {
                // The break is kept as it was written; the space before it sits at the end of the line
                // it closes and collapses away with the run it belongs to.
                pendingSpace = false;
                builder.Append(character);
                continue;
            }

            if (TextChunker.IsCollapsible(character))
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

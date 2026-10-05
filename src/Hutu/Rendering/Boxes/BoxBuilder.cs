using AngleSharp.Css;
using AngleSharp.Css.RenderTree;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
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

        if (kind == BoxKind.Block)
        {
            GroupChildren(box, children);
        }
        else
        {
            box.Children.AddRange(children);
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

        // Everything that starts with "inline" (inline, inline-block, inline-flex, ...) takes part
        // in the surrounding line box; the rest is treated as block-level for now.
        return style.Display.StartsWith("inline", StringComparison.OrdinalIgnoreCase)
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
    private static void GroupChildren(Box parent, List<Box> children)
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
            parent.Children.AddRange(children);
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

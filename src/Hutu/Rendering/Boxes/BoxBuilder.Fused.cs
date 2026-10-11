using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// The fused box builder: matches, cascades and computes each element from the DOM directly,
/// with no render tree and no per-element declaration.
/// </summary>
/// <remarks>
/// It shares the render-tree builder's box assembly - the kind, the inline grouping, the text
/// runs - and differs only in where the styles come from. A document the fused resolvers
/// cannot yet reproduce whole (layout contexts that read the declaration, substitution
/// functions, ruby) makes the whole build abstain, so the caller falls back to the render
/// tree for that document: one decision, no mixed tree.
/// </remarks>
public static partial class BoxBuilder
{
    /// <summary>
    /// Builds the box tree from the document's own DOM, or null when the document uses
    /// something the fused path does not reproduce and the caller should fall back.
    /// </summary>
    public static Box? TryBuildFused(
        IDocument document,
        IRenderDevice device,
        IImageProvider? images = null,
        FontFaceTable? faces = null)
    {
        if (document.DocumentElement is not { } root)
        {
            return null;
        }

        var sheets = SheetsOf(document);
        var index = RuleBucketIndex.Build(sheets, device);
        var pool = new StyleIntern();
        var cache = new FusedAreaCache();
        var supported = true;

        var box = BuildFusedElement(
            root, index, FusedParent.Root, device, images, faces, pool, cache, WritingModeInfo.Horizontal, ref supported);

        if (!supported || box is null)
        {
            return null;
        }

        var rootCascade = MatchedCascade.ForElement(index, root);
        AdoptBodyFlow(box, rootCascade.Find("writing-mode") is not null);
        MarkBasisUse(box);
        return box;
    }

    private static Box? BuildFusedElement(
        IElement element,
        RuleBucketIndex index,
        FusedParent parent,
        IRenderDevice device,
        IImageProvider? images,
        FontFaceTable? faces,
        StyleIntern pool,
        FusedAreaCache cache,
        WritingModeInfo inheritedFlow,
        ref Boolean supported)
    {
        var cascade = MatchedCascade.ForElement(index, element);

        // The fused resolvers read cascaded values as written, so a var() or env() - which only the
        // cascade's own substitution can resolve - abandons the whole document to the render-tree
        // path.
        if (cascade.UsesSubstitution())
        {
            supported = false;
            return null;
        }

        var style = FusedStyleFactory.Build(element, cascade, parent, device, faces, pool, cache);

        if (style.IsDisplayNone)
        {
            return null;
        }

        var kind = ResolveKind(element, style);

        // The substitution above is now the one thing that abandons a document. Every formatting
        // context has the readers it needs - `PositionStyle`, `TableStyle`, `FlexStyle`, `GridStyle`
        // - each taking the cascaded declarations instead of a computed one, and ruby needs none of
        // that: a pair is built here, and only its annotation's side has to be carried down.

        var flow = ResolveFlow(style, inheritedFlow);

        if (kind == BoxKind.HorizontalRule)
        {
            return new Box(kind, style) { Element = element, Flow = flow };
        }

        if (element is IHtmlImageElement image)
        {
            return BuildReplaced(image, kind, style, flow, images);
        }

        var children = new List<Box>();

        foreach (var child in element.ChildNodes)
        {
            switch (child)
            {
                case IElement elementChild:
                    // The parent's own tag name travels down with it: a box model needs it, because the
                    // sample stylesheet leaves a list inside a list to its outer one's margins.
                    var childBox = BuildFusedElement(
                        elementChild, index, parent.Next(style, cascade, cache.Tag(element.LocalName)), device, images, faces, pool, cache, flow, ref supported);

                    if (childBox is not null)
                    {
                        children.Add(childBox);
                    }

                    break;

                case IText textChild:
                    if (BuildTextValue(textChild.Data, style, flow) is { } text)
                    {
                        children.Add(new Box(BoxKind.Text, style) { Source = textChild, Text = text, Flow = flow });
                    }

                    break;
            }
        }

        if (kind == BoxKind.Inline && HasBlockLevel(children))
        {
            kind = BoxKind.Block;
        }

        var box = new Box(kind, style) { Element = element, Flow = flow };

        switch (kind)
        {
            // A table is not a block container: what it holds is rows, and a cell that arrives
            // without one is given an anonymous row to sit in. That is the fixup, and it is what
            // makes a table of `<div>`s lay out like the table it says it is. It is not part of
            // assembling boxes, so it is asked for here exactly as the render-tree path asks.
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

            // A flex or grid container's children are all items, so a run of inline content is one
            // item even when there is nothing else around it - it is wrapped rather than left to
            // form a line box, which is what makes a bare word inside either container behave.
            case BoxKind.Flex:
            case BoxKind.Grid:
                GroupChildren(box, children, true);
                break;

            case BoxKind.Block:
            case BoxKind.InlineBlock:
            case BoxKind.TableCell:
                GroupChildren(box, children);
                break;

            default:
                box.Children.AddRange(children);
                break;
        }

        // Every child is here now, so this is the first point at which an item can be told which item it
        // is. The same call runs in the render-tree builder, which is what keeps the two trees marking
        // the same items the same way.
        ListMarkers.Mark(box);

        return box;
    }

    private static List<ICssStyleSheet> SheetsOf(IDocument document)
    {
        var sheets = new List<ICssStyleSheet>();

        foreach (var provider in document.Context.GetServices<ICssDefaultStyleSheetProvider>())
        {
            sheets.Add(provider.Default);
        }

        sheets.AddRange(document.GetStyleSheets().OfType<ICssStyleSheet>());
        return sheets;
    }
}

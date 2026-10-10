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
        var supported = true;

        var box = BuildFusedElement(
            root, index, FusedParent.Root, device, images, faces, pool, WritingModeInfo.Horizontal, ref supported);

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
        WritingModeInfo inheritedFlow,
        ref Boolean supported)
    {
        var cascade = MatchedCascade.ForElement(index, element);

        // The fused resolvers read cascaded values as written; var()/env() need the cascade's
        // substitution, and the layout contexts that read the declaration through lazy readers
        // (flex, grid, table) or the ruby pairing are not yet fused. Any of these abandons the
        // whole document to the render-tree path.
        if (cascade.UsesSubstitution())
        {
            supported = false;
            return null;
        }

        var style = FusedStyleFactory.Build(element, cascade, parent, device, faces, pool);

        if (style.IsDisplayNone)
        {
            return null;
        }

        var kind = ResolveKind(element, style);

        if (kind is BoxKind.Flex or BoxKind.Grid or BoxKind.Table or BoxKind.TableRowGroup
            or BoxKind.TableRow or BoxKind.TableCell or BoxKind.TableCaption
            or BoxKind.TableColumn or BoxKind.TableColumnGroup
            or BoxKind.Ruby or BoxKind.RubyText or BoxKind.RubyBase)
        {
            supported = false;
            return null;
        }

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
                    var childBox = BuildFusedElement(
                        elementChild, index, parent.Next(style, cascade), device, images, faces, pool, flow, ref supported);

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
            case BoxKind.Block:
            case BoxKind.InlineBlock:
            case BoxKind.TableCell:
                GroupChildren(box, children);
                break;

            default:
                box.Children.AddRange(children);
                break;
        }

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

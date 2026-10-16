using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
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
internal static partial class BoxBuilder
{
    /// <summary>
    /// Builds the box tree from the document's own DOM, or null when the document uses
    /// something the fused path does not reproduce and the caller should fall back.
    /// </summary>
    /// <param name="carried">
    /// The style resolution state of an earlier build, reused when it still describes this document
    /// and this device. Null builds everything from scratch, which is what a document or a viewport
    /// that changed asks for.
    /// </param>
    public static Box? TryBuildFused(
        IDocument document,
        HutuRenderDevice device,
        IImageProvider? images = null,
        FontFaceTable? faces = null,
        StyleResolution? carried = null)
    {
        if (document.DocumentElement is not { } root)
        {
            return null;
        }

        // Every build starts from the size a document that says nothing has, because the root's own
        // relative size is a share of it - and a second document, or a resize of the first, must not be
        // measured against whatever the last one decided. The walk hands the root's answer back to the
        // device as soon as it has it; see <see cref="FusedStyleFactory.Build"/>.
        device.RootFontSize = HutuRenderDevice.InitialFontSize;

        // The sheets are only read to build an index, so a carried one spares the walk over them
        // entirely - and with it the cascade matches and the interned areas behind them. A build
        // with nothing carried starts from a fresh state, which is what a document that has just
        // been loaded pays.
        var state = carried ?? new StyleResolution();
        var index = state.Index;
        var supported = true;

        if (index is null)
        {
            index = RuleBucketIndex.Build(SheetsOf(document), device);
        }

        // A custom property is read by parsing its substituted text back into a declaration, which is
        // the parser's job. A document that declares none never asks, and one that does without a parser
        // to answer with is left to the render tree - the same decision the engine made when it could
        // not read variables at all, and one no context built by WithCss() ever reaches.
        var parser = document.Context.GetService<ICssParser>();

        var box = BuildFusedElement(
            root, index, FusedParent.Root, device, images, faces, state, parser, WritingModeInfo.Horizontal, false, ref supported);

        if (!supported || box is null)
        {
            return null;
        }

        var rootCascade = MatchedCascade.ForElement(index, root, StyleRecord.MarksOf(root));
        AdoptBodyFlow(box, rootCascade.Find("writing-mode") is not null);
        MarkBasisUse(box);

        // Remembered only now that the walk has agreed to describe the document: a build that
        // abstained has proved nothing about the sheets, so the next one starts again.
        state.Keep(index);

        return box;
    }

    private static Box? BuildFusedElement(
        IElement element,
        RuleBucketIndex index,
        FusedParent parent,
        HutuRenderDevice device,
        IImageProvider? images,
        FontFaceTable? faces,
        StyleResolution state,
        ICssParser? parser,
        WritingModeInfo inheritedFlow,
        Boolean inItemContainer,
        ref Boolean supported)
    {
        // The marks come first because the index is asked for the cascade with them: they are the one
        // input to a match that a reading gesture can move, and the index holds last build's answer
        // for an element no rule reaches outside of - which is almost all of them - so the walk over
        // every candidate selector is spared for every element whose own marks stand still.
        var marks = StyleRecord.MarksOf(element);
        var cascade = MatchedCascade.ForElement(index, element, marks);

        // An environment variable is the one substitution nothing here can answer - it is a value the
        // host has and the stylesheet does not - so the document is handed over rather than guessed at.
        // A var() is no longer such a case: the scope opened below reads it.
        if (cascade.Scan().UsesEnvironment)
        {
            supported = false;
            return null;
        }

        var variables = VariableScope.Open(cascade, parent.Variables, parser);

        if (variables is null && parser is null && VariableScope.NeedsParser(cascade))
        {
            supported = false;
            return null;
        }

        // A style is a function of the element's own match, the values it inherits, the scope its
        // var() references are read through and the marks the engine answers to off the element
        // itself. All four are held - three by reference and the marks as a small number - so when
        // each is what the last build used the answer is the one already in hand and the whole
        // resolution stands down. That is what keeps a pointer moving across a link from resolving
        // several thousand styles to change one of them.
        var record = state.RecordFor(element);
        var stands = false;
        ComputedStyle style;

        if (record is { } previous &&
            ReferenceEquals(previous.Cascade, cascade) &&
            ReferenceEquals(previous.Variables, variables) &&
            previous.Marks == marks &&
            parent.Stands)
        {
            style = previous.Style;
            stands = true;

            // The root's size is the number every rem below it is a share of, and the build that
            // would have handed it to the device is the one being stood down.
            if (ReferenceEquals(parent, FusedParent.Root))
            {
                device.RootFontSize = style.FontSize;
            }
        }
        else
        {
            style = FusedStyleFactory.Build(element, cascade, parent, device, faces, state.Pool, state.Cache, variables);
            state.Remember(element, new StyleRecord(cascade, variables, style, marks));

            // Resolved again, and it came to the same answer: the mark that moved on this element -
            // or the one that moved on its ancestor and left this one's inherited values alone -
            // changed nothing a descendant reads. Its subtree is therefore handed what it was handed
            // last time and stands down, even though this element itself was computed over.
            //
            // What a descendant reads is what the builder hands down, and all but one of it comes off
            // the style: the areas, the colour, the flow, the cursor, the annotation. The exception is
            // the line-height declaration, which a child re-resolves against its own size and so has
            // to keep travelling down as the declaration rather than as the value - it is compared
            // here, and it is the only reason the earlier cascade is still asked about at all.
            stands = record is { } earlier &&
                style.SameInheritance(earlier.Style) &&
                ReferenceEquals(earlier.Cascade.Find("line-height"), cascade.Find("line-height"));
        }

        if (style.IsDisplayNone)
        {
            return null;
        }

        var kind = ResolveKind(element, style);

        // A flex or grid container blockifies its children: an inline-level child is an item of its
        // own, laid out as a block and carrying the style the page gave it. Left inline, a run of
        // `span` and `a` children would be wrapped into one anonymous block that carries the
        // container's style instead - and every flex property those children declared would be
        // dropped, which is what collapses a card title or a nav row to nothing.
        if (inItemContainer && kind is BoxKind.Inline or BoxKind.InlineBlock)
        {
            kind = BoxKind.Block;
        }

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

        if (IsInlineSvgRoot(element))
        {
            return BuildInlineSvg(element, kind, style, flow);
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
                        elementChild, index, parent.Next(style, cascade, variables, state.Tag(element.LocalName), stands), device, images, faces, state, parser, flow,
                        kind is BoxKind.Flex or BoxKind.Grid or BoxKind.Masonry, ref supported);

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
            case BoxKind.Masonry:
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

    internal static List<ICssStyleSheet> SheetsOf(IDocument document)
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

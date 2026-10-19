using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <param name="profile">
    /// Filled with where the walk spent its time and how much of the document it had to look at
    /// again, or null - which is what a renderer passes and what costs nothing.
    /// </param>
    public static Box? TryBuildFused(
        IDocument document,
        HutuRenderDevice device,
        IImageProvider? images = null,
        FontFaceTable? faces = null,
        StyleResolution? carried = null,
        FusedProfile? profile = null)
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

        if (profile is not null)
        {
            index.ResetMatchCounters();
            VariableScope.ResetCounters();
        }

        var box = BuildFusedElement(
            root, index, FusedParent.Root, device, images, faces, state, parser, WritingModeInfo.Horizontal, false, profile, ref supported);

        if (profile is not null)
        {
            profile.HeldMatches = index.HeldMatches;
            profile.FreshMatches = index.FreshMatches;
            profile.OpenedScopes = VariableScope.Opened;
            profile.ReusedScopes = VariableScope.Reused;
        }

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
        FusedProfile? profile,
        ref Boolean supported)
    {
        // The marks come first because the index is asked for the cascade with them: they are the one
        // input to a match that a reading gesture can move, and the index holds last build's answer
        // for an element no rule reaches outside of - which is almost all of them - so the walk over
        // every candidate selector is spared for every element whose own marks stand still.
        var marks = StyleRecord.MarksOf(element);

        var matchStart = FusedProfile.Stamp(profile);
        var cascade = MatchedCascade.ForElement(index, element, marks);

        if (profile is not null)
        {
            profile.Matching += FusedProfile.Since(matchStart);
            profile.Walked++;
        }

        // An environment variable is the one substitution nothing here can answer - it is a value the
        // host has and the stylesheet does not - so the document is handed over rather than guessed at.
        // A var() is no longer such a case: the scope opened below reads it.
        if (cascade.Scan().UsesEnvironment)
        {
            supported = false;
            return null;
        }

        var variableStart = FusedProfile.Stamp(profile);
        var variables = VariableScope.Open(cascade, parent.Variables, parser, state.Heads);

        if (profile is not null)
        {
            profile.Cascading += FusedProfile.Since(variableStart);
        }

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
            var buildStart = FusedProfile.Stamp(profile);
            style = FusedStyleFactory.Build(element, cascade, parent, device, faces, state.Pool, state.Cache, variables);
            state.Remember(element, new StyleRecord(cascade, variables, style, marks));

            if (profile is not null)
            {
                profile.Cascading += FusedProfile.Since(buildStart);
                profile.Computed++;
            }

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
        // dropped, which is what collapses a card title or a nav row to nothing. `inline-flex` and
        // its fellows are blockified the same way, but only in their outer role: the kind keeps the
        // container the display named, so an item written `inline-flex` is still a flex container,
        // and only the inline that would have carried it on a line is taken away.
        if (inItemContainer && (kind is BoxKind.Inline or BoxKind.InlineBlock))
        {
            kind = BoxKind.Block;
        }

        var inlineOuter = !inItemContainer && HasInlineOuter(kind, style);

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
                        kind is BoxKind.Flex or BoxKind.Grid or BoxKind.Masonry, profile, ref supported);

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

        var box = new Box(kind, style) { Element = element, Flow = flow, IsInlineOuter = inlineOuter };

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
                MarkItems(box);
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

        // A marker is a box of its own, so an arm written `li::marker` styles that box rather than the item
        // it hangs on. It is resolved here, beside the item's own style, because the arms for it match no
        // element of the document and so never reach the cascade the item was read from.
        if (index.Pseudo.HasMarkers && kind is BoxKind.Block or BoxKind.InlineBlock)
        {
            box.MarkerStyle = ListMarkers.StyleForItem(
                element, style, cascade, variables, parent, index, device, faces, state, parser);
        }

        // The two generated boxes are children of this element, so they are made once every other
        // child is in place - and before the markers are, because CSS 2.1 §12.5.1 puts a marker
        // stated `inside` its item "before the element's content and before any :before
        // pseudo-elements", which is the order the reader sees them in.
        GeneratedContent.Place(box, element, index, style, cascade, variables, parent, device, faces, state, images, flow);

        // Every child is here now, so this is the first point at which an item can be told which item it
        // is. The same call runs in the render-tree builder, which is what keeps the two trees marking
        // the same items the same way.
        ListMarkers.Mark(box);

        return box;
    }

    /// <summary>
    /// Marks what a flex, grid or grid-lanes container lays out as its items. An item establishes an
    /// independent formatting context of its own (CSS Flexbox 1 §4, CSS Grid 1 §6.1) - the outer edge
    /// of the context it holds - so a margin inside it stays inside it.
    /// </summary>
    private static void MarkItems(Box container)
    {
        foreach (var child in container.Children)
        {
            // A child that left the flow is not an item: it is laid out against a containing block of
            // its own, and it seals a formatting context of its own anyway.
            if (!PositionedLayout.IsOutOfFlow(child))
            {
                child.IsItem = true;
            }
        }
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

/// <summary>
/// Where a fused build spent its time, split the way the build itself is: asking the index what
/// matches each element, and turning that match into a computed style. It also carries the counts
/// that say how much of the document had to be looked at again, which is what tells a rebuild that
/// was slow apart from one that merely touched a lot.
/// </summary>
/// <remarks>
/// Filled only when a caller asks for it. A renderer passes nothing, and the walk then reads one
/// null check per element rather than a pair of clock readings.
/// </remarks>
internal sealed class FusedProfile
{
    /// <summary>Wall clock spent matching elements, summed over the walk.</summary>
    public Double Matching;

    /// <summary>Wall clock spent opening variable scopes and computing styles, summed over the walk.</summary>
    public Double Cascading;

    /// <summary>How many elements the walk visited.</summary>
    public Int32 Walked;

    /// <summary>How many elements had their style computed rather than handed what the last build had.</summary>
    public Int32 Computed;

    /// <summary>How many elements stood on the match the last build left.</summary>
    public Int32 HeldMatches;

    /// <summary>How many elements had to be matched again.</summary>
    public Int32 FreshMatches;

    /// <summary>How many variable-scope links the walk created.</summary>
    public Int32 OpenedScopes;

    /// <summary>How many variable-scope lookups were handed a link that was already open.</summary>
    public Int32 ReusedScopes;

    /// <summary>Takes a timestamp, or zero when there is no profile to fill.</summary>
    public static Int64 Stamp(FusedProfile? profile) => profile is null ? 0L : Stopwatch.GetTimestamp();

    /// <summary>Milliseconds since a timestamp, or zero when there is no profile to fill.</summary>
    public static Double Since(Int64 start) =>
        start == 0L ? 0d : Stopwatch.GetElapsedTime(start).TotalMilliseconds;
}

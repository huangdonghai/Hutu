using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Turns the box tree into a layout tree with resolved geometry.
/// </summary>
/// <remarks>
/// Layout runs in two phases. <see cref="Measure"/> answers how large a box wants to be given the
/// space that is available to it, without producing any geometry; <see cref="Arrange"/> then fixes
/// that size into a rectangle and positions the content. A formatting context that has to decide how
/// to share space - flex, grid, a table - needs the sizes before it can hand positions out, which is
/// exactly what the split provides.
/// <para>
/// Sizes are resolved against the containing block rather than the viewport. A percentage is a
/// fraction of the containing block, <c>width: auto</c> fills whatever its margins leave of it, and a
/// declared size covers the padding and the border only when <c>box-sizing</c> says so. The
/// containing block of a box is its parent's content box; the one the root sees is the viewport.
/// Both facts are carried by <see cref="LayoutContext"/> rather than read from the render device.
/// </para>
/// <para>
/// Measurements are cached for the duration of a pass, keyed by the box and the basis the size was
/// resolved from. A viewport-sized layout therefore measures each box at most once per distinct
/// available width, however many times a formatting context negotiates around it. The intrinsic
/// sizes a flex or grid container asks its items for are cached the same way, which matters because
/// track sizing asks for them again on every pass of its negotiation.
/// </para>
/// </remarks>
public sealed class LayoutEngine
{
    private readonly InlineLayout _inline;
    private readonly BlockLayout _block;
    private readonly FlexFormattingContext _flex;
    private readonly GridFormattingContext _grid;
    private readonly TableFormattingContext _table;
    private readonly PositionedLayout _positioned;
    private readonly ITextMeasurer _measurer;
    private readonly Dictionary<MeasureKey, Size> _cache = new();
    private readonly Dictionary<MeasureKey, IntrinsicSize> _intrinsic = new();
    private readonly Dictionary<MeasureKey, IntrinsicSize> _contentIntrinsic = new();

    /// <summary>
    /// The intrinsic sizes of the boxes that answer the same at every containing block. Keyed by the
    /// box alone, because the basis is the one thing the answer does not depend on - see
    /// <see cref="Box.IntrinsicIgnoresBasis"/>.
    /// </summary>
    private readonly Dictionary<Box, IntrinsicSize> _intrinsicFree = new();

    /// <summary>The same, for the content-intrinsic cache.</summary>
    private readonly Dictionary<Box, IntrinsicSize> _contentIntrinsicFree = new();

    /// <summary>The engine the assertion measures against, made on first use.</summary>
    private LayoutEngine? _reference;

    /// <summary>
    /// Gets or sets whether a cached intrinsic size is checked against an honest measurement at the
    /// containing block in hand before it is handed back.
    /// </summary>
    /// <remarks>
    /// Leaving the basis out of the key is only safe while the size really does not depend on it, and
    /// the one place that can be caught being false is where a size measured at one basis is about to
    /// be returned at another. This is a verification switch rather than a feature: it multiplies the
    /// intrinsic work, so it stays off unless a probe turns it on.
    /// </remarks>
    public static Boolean AssertBasisFree { get; set; }

    /// <summary>Temporary: forces every box onto the basis-keyed cache, so a probe can measure what
    /// leaving the basis out is worth without a second build.</summary>
    public static Boolean DisableBasisFreeCache { get; set; }

    public LayoutEngine(ITextMeasurer measurer)
    {
        _measurer = measurer ?? throw new ArgumentNullException(nameof(measurer));
        _inline = new InlineLayout(measurer);
        _block = new BlockLayout(this, _inline);
        _flex = new FlexFormattingContext(this);
        _grid = new GridFormattingContext(this);
        _table = new TableFormattingContext(this);
        _positioned = new PositionedLayout(this);
    }

    /// <summary>
    /// Lays the box tree out in the given viewport. The root is placed at the origin; null means
    /// there is nothing to lay out.
    /// </summary>
    /// <param name="viewportHeight">
    /// The viewport's height, which is the initial containing block's. A percentage height only
    /// resolves against it when it is positive; zero means the initial containing block has no
    /// definite height and a percentage height behaves as <c>auto</c>.
    /// </param>
    public LayoutResult? Layout(Box? root, Double viewportWidth, Double viewportHeight = 0, Offset scroll = default)
    {
        if (root is null)
        {
            return null;
        }

        var width = Math.Max(1, viewportWidth);
        var viewport = new Size(width, Math.Max(0, viewportHeight));
        var context = new LayoutContext(viewport, viewport, scroll, _measurer);

        _cache.Clear();
        _intrinsic.Clear();
        _contentIntrinsic.Clear();
        _intrinsicFree.Clear();
        _contentIntrinsicFree.Clear();

        var size = Measure(root, MeasureConstraints.FitWidth(width), context);
        var margin = BoxModel.UsedMargin(root.Style, width, size.Width);
        var rootBox = Arrange(root, new Rect(margin.Left, 0, size.Width, size.Height), context);

        ResolveDecorations(rootBox);

        // What the document occupies is what a viewport has to be able to reach, which is not the
        // root's box: a page whose columns run sideways, or a box placed off an edge, reaches past
        // it. Measuring it settles the frame as well, because a scroll offset is a distance from
        // where the document begins.
        var content = ScrollableOverflow.Of(rootBox);
        ScrollableOverflow.MoveToOrigin(rootBox, content);

        return new LayoutResult(rootBox, content.Width, content.Height);
    }

    /// <summary>
    /// Resolves the corner radii of every box, together with the two keywords that decide what those
    /// radii clip.
    /// </summary>
    /// <remarks>
    /// A radius is relative to the size of the box it belongs to - a percentage by definition, and the
    /// rule that shrinks radii which would overlap by necessity - so it cannot be resolved while a box
    /// is being arranged, only once the whole tree has been placed. Keeping it out of the painter
    /// matters: painting runs again on every invalidation, layout only when the document changes.
    /// </remarks>
    private static void ResolveDecorations(LayoutBox box)
    {
        var style = box.Source.Style;

        box.Radii = CornerRadii.Resolve(style.BorderRadius, box.BorderBox.Width, box.BorderBox.Height);
        box.BackgroundClip = style.BackgroundClip;

        // An inline element has no box of its own to clip anything to, and CSS says `overflow` has no
        // effect on one: honouring it would start cutting off the element's own runs.
        box.ClipsOverflow = box.Kind != LayoutKind.Inline && style.ClipsOverflow;

        if (box.Kind == LayoutKind.Inline)
        {
            box.Radii = Sliced(box.Radii, box);
        }

        foreach (var child in box.Children)
        {
            ResolveDecorations(child);
        }
    }

    /// <summary>
    /// Squares off the pair of corners on the edge an inline fragment was cut at. Everything that
    /// would have curved along that edge was drawn on the line the element began or ended on, which is
    /// what <c>box-decoration-break: slice</c> asks for; without this every line of a broken element
    /// would grow a rounded end of its own.
    /// </summary>
    private static CornerRadii Sliced(CornerRadii radii, LayoutBox box)
    {
        if (!box.SlicesInlineStart && !box.SlicesInlineEnd)
        {
            return radii;
        }

        // The inline axis of a row runs across the box, so a row is cut down its left and right edges
        // and a column across its top and bottom ones. The cut begins at the edge the text begins
        // reading from - the near edge of a left-to-right row, the far one of a right-to-left row, and
        // the top one of a column unless the mode reads up the page.
        var startAtBeginning = box.Flow.InlineSign > 0;

        var top = box.Vertical &&
            ((box.SlicesInlineStart && startAtBeginning) || (box.SlicesInlineEnd && !startAtBeginning));

        var bottom = box.Vertical &&
            ((box.SlicesInlineStart && !startAtBeginning) || (box.SlicesInlineEnd && startAtBeginning));

        var left = !box.Vertical &&
            ((box.SlicesInlineStart && startAtBeginning) || (box.SlicesInlineEnd && !startAtBeginning));

        var right = !box.Vertical &&
            ((box.SlicesInlineStart && !startAtBeginning) || (box.SlicesInlineEnd && startAtBeginning));

        return new CornerRadii(
            top || left ? CornerRadius.Zero : radii.TopLeft,
            top || right ? CornerRadius.Zero : radii.TopRight,
            bottom || right ? CornerRadius.Zero : radii.BottomRight,
            bottom || left ? CornerRadius.Zero : radii.BottomLeft);
    }

    /// <summary>
    /// Measures a box against the available space, returning the size of its border box. The result
    /// is cached, so a formatting context may ask repeatedly without paying for it repeatedly.
    /// </summary>
    internal Size Measure(Box box, MeasureConstraints constraints, LayoutContext context)
    {
        LayoutProbe.CountMeasure();

        var key = new MeasureKey(
            box,
            constraints.AvailableWidth ?? context.ContainingBlock.Width,
            constraints.AvailableHeight ?? context.ContainingBlock.Height);

        if (_cache.TryGetValue(key, out var cached))
        {
            LayoutProbe.CountMeasureHit();
            return cached;
        }

        LayoutProbe.EnterMeasure();
        var size = MeasureCore(box, constraints, context);
        LayoutProbe.LeaveMeasure();

        _cache[key] = size;
        return size;
    }

    /// <summary>
    /// Produces the geometry of a measured box inside the given border box and applies whatever its
    /// <c>position</c> then asks of it: a relative displacement, or the rule that pins a sticky box.
    /// </summary>
    internal LayoutBox Arrange(Box box, Rect borderBox, LayoutContext context)
    {
        LayoutProbe.CountArrange();

        // An anonymous box borrows its parent's style; honouring the positioning it inherits would
        // displace the wrapper a second time, because the parent's own displacement already took it
        // along.
        if (box.IsAnonymous)
        {
            return ArrangeCore(box, borderBox, context);
        }

        LayoutProbe.EnterArrange();
        var layout = ArrangeCore(box, borderBox, context);
        LayoutProbe.LeaveArrange();

        var position = box.Style.Position;

        layout.Position = position.Position;
        layout.ZIndex = position.HasZIndex ? position.ZIndex : 0;

        _positioned.Finish(layout, position, context);
        return layout;
    }

    /// <summary>
    /// Lays an out-of-flow box out against its containing block. It is the block context that calls
    /// this, once the in-flow children have been placed, because an <c>absolute</c> or <c>fixed</c>
    /// box takes no part in their stacking.
    /// </summary>
    internal LayoutBox ArrangePositioned(Box box, Offset staticPosition, LayoutContext context) =>
        _positioned.Arrange(box, staticPosition, context);

    /// <summary>Produces the geometry of a measured box inside the given border box.</summary>
    private LayoutBox ArrangeCore(Box box, Rect borderBox, LayoutContext context)
    {
        LayoutProbe.EnterContext(ContextOf(box));

        try
        {
            return box.Kind switch
            {
                BoxKind.Flex => _flex.Arrange(box, borderBox, context),
                BoxKind.Grid => _grid.Arrange(box, borderBox, context),
                BoxKind.Table => _table.Arrange(box, borderBox, context),
                _ => box.IsReplaced
                    ? ArrangeReplaced(box, borderBox, context)
                    : _block.Arrange(box, borderBox, context),
            };
        }
        finally
        {
            LayoutProbe.LeaveContext();
        }
    }

    /// <summary>
    /// Measures the sizes a box's content wants when nothing constrains it: its <c>min-content</c>
    /// and <c>max-content</c> inline sizes. A flex or grid container answers for its items, so an
    /// item with an automatic basis can ask its own intrinsic width before anything is decided.
    /// </summary>
    internal IntrinsicSize MeasureIntrinsic(Box box, LayoutContext context)
    {
        LayoutProbe.CountIntrinsic();
        LayoutProbe.NoteIntrinsicKey(box, context.ContainingBlock.Width, context.ContainingBlock.Height, content: false);

        // A box that answers the same at every containing block is cached without the basis. The walk
        // that asks about it - a table sizing its tracks, a flex container negotiating its items -
        // asks under several, and every one of them used to be paid for separately even though the
        // answer could not differ.
        if (box.IntrinsicIgnoresBasis && !DisableBasisFreeCache)
        {
            if (_intrinsicFree.TryGetValue(box, out var free))
            {
                LayoutProbe.CountIntrinsicHit();
                VerifyBasisFree(box, context, free, content: false);
                return free;
            }

            var settled = MeasureIntrinsicUncached(box, context, content: false);
            _intrinsicFree[box] = settled;
            return settled;
        }

        var key = new MeasureKey(box, context.ContainingBlock.Width, context.ContainingBlock.Height);

        if (_intrinsic.TryGetValue(key, out var cached))
        {
            LayoutProbe.CountIntrinsicHit();
            return cached;
        }

        var size = MeasureIntrinsicUncached(box, context, content: false);
        _intrinsic[key] = size;
        return size;
    }

    /// <summary>
    /// Measures a size no cache held, under the probe's timer.
    /// </summary>
    private IntrinsicSize MeasureIntrinsicUncached(Box box, LayoutContext context, Boolean content)
    {
        LayoutProbe.EnterIntrinsic(box);
        LayoutProbe.EnterContext(ContextOf(box));

        try
        {
            return content
                ? MeasureContentIntrinsicCore(box, context)
                : MeasureIntrinsicCore(box, context);
        }
        finally
        {
            LayoutProbe.LeaveContext();
            LayoutProbe.LeaveIntrinsic();
        }
    }

    /// <summary>
    /// Checks a cached basis-free size against an honest measurement at the containing block in hand.
    /// </summary>
    /// <remarks>
    /// A box wrongly marked as wanting the same size everywhere can only be found out where its size
    /// is about to be reused, so this runs on the hit and not on the measurement - the first
    /// measurement is taken at the basis in hand anyway and cannot disagree with itself. The second
    /// engine is what keeps the honest measurement from polluting this one's caches, and it keeps the
    /// basis in its own keys all the way down, so a wrong flag deep in the subtree surfaces as a
    /// difference here rather than being papered over by the same mistake twice.
    /// </remarks>
    private void VerifyBasisFree(Box box, LayoutContext context, IntrinsicSize cached, Boolean content)
    {
        if (!AssertBasisFree)
        {
            return;
        }

        var reference = _reference ??= new LayoutEngine(_measurer);
        var honest = content
            ? reference.MeasureContentIntrinsic(box, context)
            : reference.MeasureIntrinsic(box, context);

        if (honest.MinContent != cached.MinContent || honest.MaxContent != cached.MaxContent)
        {
            throw new InvalidOperationException(
                $"A {box.Kind} box is marked as wanting the same intrinsic size at every containing " +
                $"block, but it was cached as {cached.MinContent}/{cached.MaxContent} and wants " +
                $"{honest.MinContent}/{honest.MaxContent} at {context.ContainingBlock.Width}.");
        }
    }

    private IntrinsicSize MeasureIntrinsicCore(Box box, LayoutContext context) =>
        box.Kind switch
        {
            BoxKind.Flex => _flex.MeasureIntrinsic(box, context),
            BoxKind.Grid => _grid.MeasureIntrinsic(box, context),
            BoxKind.Table => _table.MeasureIntrinsic(box, context),
            _ => _block.MeasureIntrinsic(box, context),
        };

    private IntrinsicSize MeasureContentIntrinsicCore(Box box, LayoutContext context) =>
        box.Kind switch
        {
            BoxKind.Flex => _flex.MeasureIntrinsic(box, context),
            BoxKind.Grid => _grid.MeasureIntrinsic(box, context),
            BoxKind.Table => _table.MeasureIntrinsic(box, context),
            _ => _block.MeasureContentIntrinsic(box, context),
        };

    /// <summary>
    /// The sizes a box's content wants once its own declared width is set aside. It is what the
    /// automatic minimum size of a flex item is built from, which is why the declared width must not
    /// be consulted here.
    /// </summary>
    internal IntrinsicSize MeasureContentIntrinsic(Box box, LayoutContext context)
    {
        LayoutProbe.CountContentIntrinsic();
        LayoutProbe.NoteIntrinsicKey(box, context.ContainingBlock.Width, context.ContainingBlock.Height, content: true);

        if (box.IntrinsicIgnoresBasis && !DisableBasisFreeCache)
        {
            if (_contentIntrinsicFree.TryGetValue(box, out var free))
            {
                LayoutProbe.CountIntrinsicHit();
                VerifyBasisFree(box, context, free, content: true);
                return free;
            }

            var settled = MeasureIntrinsicUncached(box, context, content: true);
            _contentIntrinsicFree[box] = settled;
            return settled;
        }

        var key = new MeasureKey(box, context.ContainingBlock.Width, context.ContainingBlock.Height);

        if (_contentIntrinsic.TryGetValue(key, out var cached))
        {
            LayoutProbe.CountIntrinsicHit();
            return cached;
        }

        var size = MeasureIntrinsicUncached(box, context, content: true);
        _contentIntrinsic[key] = size;
        return size;
    }

    private Size MeasureCore(Box box, MeasureConstraints constraints, LayoutContext context)
    {
        LayoutProbe.EnterContext(ContextOf(box));

        try
        {
            return box.Kind switch
            {
                BoxKind.Flex => _flex.Measure(box, constraints, context),
                BoxKind.Grid => _grid.Measure(box, constraints, context),
                BoxKind.Table => _table.Measure(box, constraints, context),
                _ => box.IsReplaced
                    ? MeasureReplaced(box, constraints, context)
                    : _block.Measure(box, constraints, context),
            };
        }
        finally
        {
            LayoutProbe.LeaveContext();
        }
    }

    /// <summary>
    /// Gets the formatting context a box is dispatched to, which is what <see cref="MeasureCore"/>,
    /// <see cref="ArrangeCore"/> and the intrinsic walk all switch on. The probe charges its timers by
    /// this, so a change to the dispatch has to be made here too.
    /// </summary>
    private static ProbeContext ContextOf(Box box) => box.Kind switch
    {
        BoxKind.Flex => ProbeContext.Flex,
        BoxKind.Grid => ProbeContext.Grid,
        BoxKind.Table => ProbeContext.Table,
        _ => box.IsReplaced ? ProbeContext.Replaced : ProbeContext.Block,
    };

    /// <summary>
    /// Measures a replaced box - an image. Its used size comes from the intrinsic size and the CSS
    /// constraints; the insets are added around it, because the constraints describe the content.
    /// </summary>
    private static Size MeasureReplaced(Box box, MeasureConstraints constraints, LayoutContext context)
    {
        var style = box.Style;
        var basisWidth = constraints.AvailableWidth ?? context.ContainingBlock.Width;
        var basisHeight = constraints.AvailableHeight ?? context.ContainingBlock.Height;
        var (width, height) = ReplacedSizing.Resolve(box, basisWidth, basisHeight);
        var padding = style.Padding.Resolve(basisWidth);

        return new Size(
            width + padding.Horizontal + BorderHorizontal(style),
            height + padding.Vertical + BorderVertical(style));
    }

    private static LayoutBox ArrangeReplaced(Box box, Rect borderBox, LayoutContext context)
    {
        var style = box.Style;
        var basis = context.ContainingBlock.Width;

        return new LayoutBox(box, LayoutKind.Image)
        {
            Margin = BoxModel.UsedMargin(style, basis, borderBox.Width),
            Padding = style.Padding.Resolve(basis),
            Border = new Thickness(
                style.BorderTopWidth,
                style.BorderRightWidth,
                style.BorderBottomWidth,
                style.BorderLeftWidth),
            ImageUrl = box.SourceUrl,
            ImageFit = style.ObjectFit,
            BorderBox = borderBox,
            Flow = box.Flow,
        };
    }

    private static Double BorderHorizontal(ComputedStyle style) =>
        style.BorderLeftWidth + style.BorderRightWidth;

    private static Double BorderVertical(ComputedStyle style) =>
        style.BorderTopWidth + style.BorderBottomWidth;

    /// <summary>
    /// The cache key: a box and the basis its size resolves from. Both dimensions are part of it
    /// because a percentage height depends on the containing block's height as much as a percentage
    /// width does on its width.
    /// </summary>
    private readonly struct MeasureKey : IEquatable<MeasureKey>
    {
        private readonly Box _box;
        private readonly Double _width;
        private readonly Double _height;

        public MeasureKey(Box box, Double width, Double height)
        {
            _box = box;
            _width = width;
            _height = height;
        }

        public Boolean Equals(MeasureKey other) =>
            ReferenceEquals(_box, other._box) &&
            _width.Equals(other._width) &&
            _height.Equals(other._height);

        public override Boolean Equals(Object? obj) => obj is MeasureKey other && Equals(other);

        public override Int32 GetHashCode() => HashCode.Combine(
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_box),
            _width,
            _height);
    }
}

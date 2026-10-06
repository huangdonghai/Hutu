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
/// available width, however many times a formatting context negotiates around it.
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

        var size = Measure(root, MeasureConstraints.FitWidth(width), context);
        var margin = BoxModel.UsedMargin(root.Style, width, size.Width);
        var rootBox = Arrange(root, new Rect(margin.Left, 0, size.Width, size.Height), context);
        var height = rootBox.BorderBox.Bottom + rootBox.Margin.Bottom;

        ResolveDecorations(rootBox);

        return new LayoutResult(rootBox, width, height);
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
        box.ClipsOverflow = style.ClipsOverflow;

        foreach (var child in box.Children)
        {
            ResolveDecorations(child);
        }
    }

    /// <summary>
    /// Measures a box against the available space, returning the size of its border box. The result
    /// is cached, so a formatting context may ask repeatedly without paying for it repeatedly.
    /// </summary>
    internal Size Measure(Box box, MeasureConstraints constraints, LayoutContext context)
    {
        var key = new MeasureKey(
            box,
            constraints.AvailableWidth ?? context.ContainingBlock.Width,
            constraints.AvailableHeight ?? context.ContainingBlock.Height);

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var size = MeasureCore(box, constraints, context);
        _cache[key] = size;
        return size;
    }

    /// <summary>
    /// Produces the geometry of a measured box inside the given border box and applies whatever its
    /// <c>position</c> then asks of it: a relative displacement, or the rule that pins a sticky box.
    /// </summary>
    internal LayoutBox Arrange(Box box, Rect borderBox, LayoutContext context)
    {
        var layout = ArrangeCore(box, borderBox, context);

        // An anonymous box borrows its parent's style; honouring the positioning it inherits would
        // displace the wrapper a second time, because the parent's own displacement already took it
        // along.
        if (box.IsAnonymous)
        {
            return layout;
        }

        var position = PositionStyle.Read(box.Style);

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
    private LayoutBox ArrangeCore(Box box, Rect borderBox, LayoutContext context) =>
        box.Kind switch
        {
            BoxKind.Flex => _flex.Arrange(box, borderBox, context),
            BoxKind.Grid => _grid.Arrange(box, borderBox, context),
            BoxKind.Table => _table.Arrange(box, borderBox, context),
            _ => box.IsReplaced
                ? ArrangeReplaced(box, borderBox, context)
                : _block.Arrange(box, borderBox, context),
        };

    /// <summary>
    /// Measures the sizes a box's content wants when nothing constrains it: its <c>min-content</c>
    /// and <c>max-content</c> inline sizes. A flex or grid container answers for its items, so an
    /// item with an automatic basis can ask its own intrinsic width before anything is decided.
    /// </summary>
    internal IntrinsicSize MeasureIntrinsic(Box box, LayoutContext context) =>
        box.Kind switch
        {
            BoxKind.Flex => _flex.MeasureIntrinsic(box, context),
            BoxKind.Grid => _grid.MeasureIntrinsic(box, context),
            BoxKind.Table => _table.MeasureIntrinsic(box, context),
            _ => _block.MeasureIntrinsic(box, context),
        };

    /// <summary>
    /// The sizes a box's content wants once its own declared width is set aside. It is what the
    /// automatic minimum size of a flex item is built from, which is why the declared width must not
    /// be consulted here.
    /// </summary>
    internal IntrinsicSize MeasureContentIntrinsic(Box box, LayoutContext context) =>
        box.Kind switch
        {
            BoxKind.Flex => _flex.MeasureIntrinsic(box, context),
            BoxKind.Grid => _grid.MeasureIntrinsic(box, context),
            BoxKind.Table => _table.MeasureIntrinsic(box, context),
            _ => _block.MeasureContentIntrinsic(box, context),
        };

    private Size MeasureCore(Box box, MeasureConstraints constraints, LayoutContext context) =>
        box.Kind switch
        {
            BoxKind.Flex => _flex.Measure(box, constraints, context),
            BoxKind.Grid => _grid.Measure(box, constraints, context),
            BoxKind.Table => _table.Measure(box, constraints, context),
            _ => box.IsReplaced
                ? MeasureReplaced(box, constraints, context)
                : _block.Measure(box, constraints, context),
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

using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System.Buffers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using System.Globalization;

namespace Hutu.Rendering.Layout;

/// <summary>The extent inline content occupies along each of the two axes it is laid out in.</summary>
/// <remarks>
/// Which physical axis either one lands on is the writing mode's business, not the content's: a block
/// asks for the pair and then decides what to do with it.
/// </remarks>
internal readonly struct InlineContentSize
{
    public InlineContentSize(Double inline, Double block)
    {
        Inline = inline;
        Block = block;
    }

    /// <summary>Gets the extent along the axis the text runs, which fills the content box's inline size.</summary>
    public Double Inline { get; }

    /// <summary>Gets the extent along the axis the lines advance, which is what the content needs to stack.</summary>
    public Double Block { get; }
}

/// <summary>
/// Lays out an inline formatting context: it flattens the inline content of a block into text runs,
/// breaks them into line boxes and positions every run.
/// </summary>
/// <remarks>
/// The tree is flattened into a single stream of runs plus explicit breaks, but the inline elements
/// the runs came from are not forgotten: every run carries the chain of boxes it sits inside, and
/// every such element is given a <see cref="LayoutKind.Inline"/> node on every line it reaches. Those
/// nodes nest, so an outer element's background lands under an inner one's, and an element broken over
/// several lines leaves one fragment per line with the cut edges left bare, which is what
/// <c>box-decoration-break: slice</c> asks for. An element is given a node whether or not it draws
/// anything, because whether it draws anything is a question for the painter: a change that only adds
/// a background is a change the layout is deliberately reused through, so the node has to be there
/// for the background to have somewhere to land. An <c>inline-block</c> is the one inline-level box
/// that does not enter the stream at all: it is placed whole, at the size its own content asks for,
/// through the engine that lays that content out.
/// <para>
/// Line height is the tallest run on the line, expanded by half-leading when the element sets an
/// explicit leading, and the baseline is shared by the whole line. An inline element's own band is
/// the exception: it is the font's content area, and it neither grows the line nor is grown by it -
/// inline padding and borders overflow the line rather than widen it. An inline-block is the other
/// exception the other way round: it is as tall as its own content and does grow the line, which is
/// what puts its text on the line's baseline with the text around it.
/// </para>
/// <para>
/// Everything is written in terms of the two logical axes rather than x and y, so the same code lays
/// out a row of lines and a column of them: <em>inline</em> is the extent a line runs along and where
/// a run sits within it, <em>block</em> is the extent a line occupies and where the next one starts.
/// Horizontal writing puts inline on x and block on y, vertical writing swaps the two, and
/// <see cref="WritingModeInfo"/> is the only thing that knows which is which.
/// </para>
/// </remarks>
internal sealed partial class InlineLayout
{
    private readonly ITextMeasurer _measurer;

    /// <summary>
    /// The lines each container was broken into, kept for the rest of the layout pass.
    /// </summary>
    /// <remarks>
    /// A context is broken into lines by the measuring phase and its lines are placed by the arranging
    /// one, and both of them used to run the whole breaking themselves: the same text chunked, oriented,
    /// measured and lined up twice, for the second time producing nothing but the placement of what the
    /// first had already settled. A line the placing phase reads is a line it does not have to make,
    /// which is what this dictionary is for - a plan is settled once <see cref="BuildLines"/> returns
    /// (<see cref="LineBuilder.Build"/> and <see cref="MarkSlices"/> are the only writers) and every
    /// reader after that only places.<para>
    /// The key carries the resolved indentation as well as the inline extent, because a percentage
    /// <c>text-indent</c> is a share of the containing block, and the two phases reach this container
    /// through contexts that are not always the same. Two that disagree about the indent are two
    /// different breakings and get their own plan.<br/>
    /// It lives exactly as long as the pass does, cleared with the engine's own caches, so a repeat
    /// layout never inherits lines from a tree it has not measured.<br/>
    /// What is held is not free: the lines stay live until the pass ends instead of becoming garbage
    /// when the measuring phase does. It is the cheaper half of the trade - the placing phase used to
    /// build its own copy of every one of them and throw it away straight after.
    /// </remarks>
    private readonly Dictionary<LinePlanKey, List<Line>> _plans = new();

    /// <summary>
    /// The baselines worked out by laying a box out for the question, held by the box and the size it
    /// was asked at. A box inside a box is asked once per level, and each answer lays its own
    /// children out for theirs, so without this a deeply nested chain of inline-blocks costs the
    /// whole subtree again at every step down it.
    /// </summary>
    private readonly Dictionary<BaselineKey, Double> _baselines = new();

    /// <summary>
    /// The steps taken after a break opportunity, kept for the next line and reused by the next call.
    /// A line may offer an opportunity after every character, so this list reaches the length of the
    /// longest line and is then cleared rather than let go: one buffer serves every block of a layout,
    /// and a fresh one per block cost the page its whole line length in entries each time.
    /// </summary>
    private readonly List<TailStep> _tail = new();

    /// <summary>The break opportunities each text and word-breaking rule set was cut into, together
    /// with the width of every chunk at the style and flow last asked for. The intrinsic, measuring and
    /// arranging phases ask the same text for the same boundaries, and the cache is cleared with the
    /// plans when the pass ends.</summary>
    private readonly Dictionary<ChunkKey, TextPlan> _textPlans = new();

    /// <summary>Marks that change how a character is drawn, kept out of the run strings. A mark names
    /// the character in its source by absolute position, so slicing a fragment is moving its bounds,
    /// not copying a mask.</summary>
    private readonly List<MaskMark> _maskMarks = new();

    /// <summary>Scratch mark lists the line builder fills before handing them to a run.</summary>
    private readonly List<MaskMark> _trimMarks = new();
    private readonly List<MaskMark> _autoMarks = new();

    /// <summary>Item lists are scratch buffers, not part of a plan: after their lines are built they
    /// are cleared and their backing arrays kept for the next container.</summary>
    private readonly Stack<List<InlineItem>> _itemLists = new();

    private const Int32 ItemListPoolLimit = 16;
    private const Int32 ItemListCapacityBudget = 8_192;

    private Int32 _itemListCapacity;

    [ThreadStatic]
    private static Stack<List<InlineAncestor>>? _ancestorLists;

    private const Int32 AncestorListPoolLimit = 16;
    private const Int32 AncestorListCapacityBudget = 1_024;

    [ThreadStatic]
    private static Int32 _ancestorListCapacity;

    private FragmentStore? _fragmentStore;
    private LineStore? _lineStore;

    [ThreadStatic]
    private static Stack<FragmentStore>? _fragmentStores;

    [ThreadStatic]
    private static Stack<LineStore>? _lineStores;

    /// <summary>Builders available to a line plan; a nested plan gets one of its own rather than
    /// clearing the one an outer plan is still filling.</summary>
    private readonly Stack<LineBuilder> _builders = new();

    private readonly record struct BaselineKey(Box Box, Double Width, Double Height, Double Basis, Double ContainingWidth, Double ContainingHeight);

    private static Int32[] RentInts(Int32 length) =>
        ArrayPool<Int32>.Shared.Rent(Math.Max(1, length));

    private static void ReturnInts(Int32[] values) =>
        ArrayPool<Int32>.Shared.Return(values);

    private const Int32 StackScratchLimit = 64;

    private static Double[] RentDoubles(Int32 length)
    {
        var values = ArrayPool<Double>.Shared.Rent(Math.Max(1, length));
        Array.Clear(values, 0, length);
        return values;
    }

    private static void ReturnDoubles(Double[] values) =>
        ArrayPool<Double>.Shared.Return(values);

    private List<InlineItem> RentItems()
    {
        if (_itemLists.Count == 0)
        {
            return new List<InlineItem>();
        }

        var list = _itemLists.Pop();
        _itemListCapacity -= list.Capacity;
        list.Clear();
        return list;
    }

    private void ReturnItems(List<InlineItem> items)
    {
        items.Clear();

        if (items.Capacity > ItemListCapacityBudget ||
            _itemLists.Count >= ItemListPoolLimit ||
            _itemListCapacity + items.Capacity > ItemListCapacityBudget)
        {
            return;
        }

        _itemLists.Push(items);
        _itemListCapacity += items.Capacity;
    }

    private static List<InlineAncestor> RentAncestorList()
    {
        var lists = _ancestorLists;

        if (lists is null || lists.Count == 0)
        {
            return new List<InlineAncestor>(3);
        }

        var list = lists.Pop();
        _ancestorListCapacity -= list.Capacity;
        list.Clear();
        return list;
    }

    private static void ReturnAncestorList(List<InlineAncestor> list)
    {
        list.Clear();

        if (list.Capacity > AncestorListCapacityBudget ||
            (_ancestorLists?.Count ?? 0) >= AncestorListPoolLimit ||
            _ancestorListCapacity + list.Capacity > AncestorListCapacityBudget)
        {
            return;
        }

        (_ancestorLists ??= new Stack<List<InlineAncestor>>()).Push(list);
        _ancestorListCapacity += list.Capacity;
    }

    private TextFragment NewFragment(
        String source,
        Int32 start,
        Int32 length,
        Double width,
        ComputedStyle style,
        Box box,
        WritingModeInfo flow,
        InlineAncestor? inline,
        Boolean isReplaced = false,
        Double contentHeight = 0,
        RubyAnnotation? ruby = null,
        Double baseWidth = 0,
        AtomicInline? atomic = null,
        Boolean outOfFlow = false,
        NoteRun? note = null,
        InterlineNoteSource? interlineNote = null,
        Boolean interlineGap = false,
        Double? interlinePosition = null,
        MarginNoteSource? marginNote = null,
        Double? hang = null)
    {
        var node = new FragmentNode(
            source, start, length, width, style, box, flow, inline, isReplaced,
            contentHeight, ruby, baseWidth, atomic, outOfFlow, note, interlineNote,
            interlineGap, interlinePosition, marginNote, hang);

        var store = _fragmentStore ??= Rent(_fragmentStores ??= new Stack<FragmentStore>());
        return new TextFragment(store, store.Add(node));
    }

    private Line NewLine(HashSet<InlineAncestor>? carried)
    {
        var node = new LineNode
        {
            Fragments = new List<TextFragment>(),
            Scale = 1,
            Alignment = CssInitialValues.TextAlign,
            ContinuesFromPrevious = carried,
            SlotIndex = -1,
        };

        var store = _lineStore ??= Rent(_lineStores ??= new Stack<LineStore>());
        return new Line(store, store.Add(node));
    }

    private static T Rent<T>(Stack<T> stores)
        where T : class, new() =>
        stores.Count > 0 ? stores.Pop() : new T();

    /// <summary>Drops the plans, which belong to one layout pass as the engine's own caches do.</summary>
    internal void ResetPlanCache()
    {
        ReleasePlans();
    }

    /// <summary>Releases the line and fragment arenas once the layout tree has been built. Their
    /// chunks stay allocated for the next pass; only the handles into them are dropped.</summary>
    internal void ReleasePlanCache()
    {
        ReleasePlans();
    }

    private void ReleasePlans()
    {
        _plans.Clear();
        _baselines.Clear();
        _textPlans.Clear();

        if (_lineStore is { } lineStore)
        {
            lineStore.Clear();
            (_lineStores ??= new Stack<LineStore>()).Push(lineStore);
            _lineStore = null;
        }

        if (_fragmentStore is { } fragmentStore)
        {
            fragmentStore.Clear();
            (_fragmentStores ??= new Stack<FragmentStore>()).Push(fragmentStore);
            _fragmentStore = null;
        }

        while (_builders.Count > 0)
        {
            _builders.Pop().ReleaseReferences();
        }

        _maskMarks.Clear();
        _trimMarks.Clear();
        _autoMarks.Clear();
    }

}

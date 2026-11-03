using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using System;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// Resolves what the sheets wrote for a block's first line, and hands that style to the fragments of the
/// line the layout actually reached.
/// </summary>
/// <remarks>
/// <para>
/// Pseudo-Elements 4 §5.2 makes <c>::first-line</c> stand for the first line box of a block container,
/// which is why nothing here can be settled while the boxes are being made: where the line ends is a
/// question for the layout, and the layout is the only reader of the answer. So the style is resolved on
/// the block beside its own (<see cref="Apply"/>) and only *distributed* once the lines are down,
/// which the decoration walk does for each box through <see cref="Step"/>.
/// </para>
/// <para>
/// Two things follow from where the specification puts the anonymous box, and the borrowed pairs read
/// both: the style reaches every fragment standing on that line - the text of a nested block as much as
/// the block's own (<c>first-line-on-ancestor-block</c>), and the mark an item wears inside it
/// (<c>first-line-and-marker</c>) - and it outranks what a descendant declared for its own text, because
/// the descendant's declaration is inherited by the text while the first line's is written on it.
/// </para>
/// <para>
/// A box that lays its own content out keeps its lines to itself: the line an outer block reaches first is
/// the one that *holds* an atomic inline, a table or a flex or grid container, not one of the lines drawn
/// inside it - which is what <c>first-line-with-inline-block</c> and the grid pairs show.
/// </para>
/// </remarks>
internal static class FirstLine
{
    /// <summary>
    /// What an arm ending in <c>::first-line</c> came to, and which of the values the engine paints with
    /// that arm actually wrote.
    /// </summary>
    /// <remarks>
    /// The second half is the point. §5.2 makes the pseudo-element a box standing <em>around</em> the first
    /// line, so a property the arm never mentions is not its to change: the text on the line keeps what it
    /// came to, exactly as it would keep its own colour inside any other box. The resolved style therefore
    /// answers only for the properties named here - and the colour a fragment inherits through the block is
    /// not one of them.
    /// </remarks>
    internal readonly record struct Paint(
        ComputedStyle Style,
        Boolean Color,
        Boolean Background,
        Boolean Fill,
        Boolean Stroke,
        Boolean Shadow);

    /// <summary>
    /// Resolves the style an arm ending in <c>::first-line</c> came to for this block and keeps it on the
    /// box, where <see cref="Step"/> finds it. Nothing is kept where no arm was written, so a block the
    /// sheets say nothing about carries nothing.
    /// </summary>
    internal static void Apply(
        Box block,
        IElement element,
        ComputedStyle style,
        MatchedCascade cascade,
        VariableScope? variables,
        FusedParent parent,
        RuleBucketIndex index,
        HutuRenderDevice device,
        FontFaceTable? faces,
        StyleResolution state,
        ICssParser? parser)
    {
        var pseudo = index.Pseudo.Cascade(PseudoArm.FirstLine, element, index.Rules, index.Origins);

        if (pseudo is null)
        {
            return;
        }

        // The arm inherits from the block it is written on, exactly as the marker's inherits from its item.
        var built = FusedStyleFactory.Build(
            element,
            pseudo,
            parent.Next(style, cascade, variables, state.Tag(element.LocalName), false),
            device,
            faces,
            state.Pool,
            state.Cache,
            variables);

        block.FirstLinePaint = new Paint(
            built,
            Declares(pseudo, "color"),
            Declares(pseudo, "background") || Declares(pseudo, "background-color"),
            Declares(pseudo, "-webkit-text-fill-color"),
            Declares(pseudo, "-webkit-text-stroke") || Declares(pseudo, "-webkit-text-stroke-color") ||
                Declares(pseudo, "-webkit-text-stroke-width"),
            Declares(pseudo, "text-shadow"));
    }

    /// <summary>Gets whether the block's own cascade - not one inherited down - writes a property.</summary>
    private static Boolean Declares(MatchedCascade cascade, String name) => cascade.Find(name) is not null;

    /// <summary>Gives this box alone the style of its first line, the children left to the walk that owns them.</summary>
    internal static void Step(LayoutBox box)
    {
        // A line box is not the block that owns the style: it is made from the same box as the lines beside
        // it, so taking the style from it again would hand every line of the block what belongs to one.
        if (box.Kind != LayoutKind.Line && !box.IsOutOfFlow &&
            box.Source.FirstLinePaint is { } paint && !OpensWithBreak(box.Source))
        {
            var line = Reach(box);

            if (line is not null || ContainsInitialLetter(box))
            {
                CoverLetters(box, paint);

                if (line is not null)
                {
                    Cover(line, paint);
                }
            }
        }
    }

    /// <summary>
    /// Gets whether the content a block reaches first is a line break the white-space rules keep, which
    /// makes the block's first line an empty one - and an empty line has no fragment to style. This is read
    /// of the boxes rather than of the lines, because a line that holds nothing is given no box of its own
    /// to be found.
    /// </summary>
    private static Boolean OpensWithBreak(Box block) => OpensWith(block) == true;

    /// <summary>
    /// Asks what the block's content opens with: true for a break that stands, false for anything with
    /// glyphs or a box of its own, and null where the content says nothing yet - empty, or white space a
    /// line may collapse - so the question passes to whatever comes next.
    /// </summary>
    private static Boolean? OpensWith(Box block)
    {
        foreach (var child in block.Children)
        {
            // A box that left the flow stands on no line, and one that lays its own content out keeps the
            // line it holds to itself: the outer block's first line is the one holding it, not an empty one.
            if (PositionedLayout.IsOutOfFlow(child) || child.Kind is BoxKind.InlineBlock or BoxKind.Table or
                BoxKind.Flex or BoxKind.Grid or BoxKind.Masonry || child.IsReplaced)
            {
                return false;
            }

            if (child.Kind == BoxKind.LineBreak)
            {
                return true;
            }

            if (child.Kind == BoxKind.Text && child.Text is { Length: > 0 } text)
            {
                var keeps = child.Style.KeepsSegmentBreaks;
                var at = 0;

                // A run of white space the line may collapse opens nothing on its own, so it is stepped
                // over - inside this run as well as across the boxes - until the text says what stands at
                // the head of the line.
                while (at < text.Length && IsCollapsible(text[at]) && !(keeps && text[at] is '\n' or '\r'))
                {
                    at++;
                }

                if (at == text.Length)
                {
                    continue;
                }

                if (keeps && text[at] is '\n' or '\r')
                {
                    return true;
                }

                return false;
            }

            if (OpensWith(child) is { } answer)
            {
                return answer;
            }
        }

        return null;
    }

    private static Boolean IsCollapsible(Char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    /// <summary>
    /// Gets the line the block reaches first: its own lines before those of a nested block, and never a
    /// line drawn inside a box that lays its own content out.
    /// </summary>
    private static LayoutBox? Reach(LayoutBox box)
    {
        if (box.Kind == LayoutKind.Line)
        {
            return box;
        }

        foreach (var child in box.Children)
        {
            // A box that took the flow is on no line of this formatting context, and one that lays its own
            // content out keeps the lines it makes to itself. A float is the same: its content stands in
            // no line box, so the block's first line runs past it - except for the initial letter, which
            // Coverage gives the style by hand because the line it was cut from is the first one.
            if (child.IsOutOfFlow || KeepsItsOwnLines(child) || child.Source.Style.Float != FloatSide.None)
            {
                continue;
            }

            if (Reach(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Gets whether any of the box's boxes is an initial letter.</summary>
    private static Boolean ContainsInitialLetter(LayoutBox box)
    {
        foreach (var child in box.Children)
        {
            if (child.Source.IsInitialLetter)
            {
                return true;
            }

            if (!child.IsOutOfFlow && ContainsInitialLetter(child))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gives the style to the initial letters the box opens with, which stand on its first line even
    /// though they have left it for the start edge.
    /// </summary>
    private static void CoverLetters(LayoutBox box, Paint style)
    {
        foreach (var child in box.Children)
        {
            if (child.Source.IsInitialLetter)
            {
                child.FirstLinePaint = style;
                Cover(child, style);
            }
            else if (!child.IsOutOfFlow)
            {
                CoverLetters(child, style);
            }
        }
    }

    /// <summary>
    /// Gives the style to a line and to every fragment standing on it. An inline element that states a
    /// colour of its own keeps it for its own text: the first line's box reaches that text by inheritance
    /// only, and an inherited value never outranks a written one.
    /// </summary>
    private static void Cover(LayoutBox line, Paint style)
    {
        foreach (var child in line.Children)
        {
            if (child.IsOutOfFlow || KeepsItsOwnLines(child))
            {
                continue;
            }

            var reaching = child.Source.DeclaresColor ? style with { Color = false } : style;

            child.FirstLinePaint = reaching;
            Cover(child, reaching);
        }
    }

    /// <summary>
    /// Gets whether the lines a box reaches are its own rather than the ones the block outside it counts:
    /// an atomic inline, a table and a flex, grid or masonry container all lay their content out from
    /// scratch, and a replaced box has no lines at all. The layout kind says block or inline for every one
    /// of these, so the question is asked of the box the layout was made from - and never of a line, which
    /// is made from the same box as the block whose line it is.
    /// </summary>
    private static Boolean KeepsItsOwnLines(LayoutBox box) =>
        box.Kind != LayoutKind.Line &&
        (box.Source.IsReplaced ||
         box.Source.Kind is BoxKind.InlineBlock or BoxKind.Table or BoxKind.Flex or
            BoxKind.Grid or BoxKind.Masonry ||
            (box.Source.Kind == BoxKind.Block &&
             box.Source.Style.Display.StartsWith("inline", StringComparison.OrdinalIgnoreCase)));
}

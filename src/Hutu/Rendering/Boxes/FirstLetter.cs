using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using System;
using System.Globalization;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// Gives the letter a block opens with the style the sheet wrote for its <c>::first-letter</c>, by
/// cutting that letter out of the run it stands in.
/// </summary>
/// <remarks>
/// <para>
/// Pseudo-Elements 4 §5.1 takes the first typographic unit of the first formatted line, together with any
/// punctuation standing before it - what Chrome measures as four characters for <c>...x</c> and two for
/// <c>“quoted”</c> - and says plainly that a list item's <c>::marker</c> is no part of the search, which is
/// what the borrowed <c>first-letter-skip-marker</c> and its references hold an engine to. The white space
/// a line starts behind is not part of it either, and a letter is a whole grapheme cluster rather than one
/// code unit, so an accented base and its combining marks are cut together.
/// </para>
/// <para>
/// The letter is looked for in the order the reader meets the content: after the text the <c>::before</c>
/// generates (measured: it is that run's first character that takes the style), through the inline children,
/// and no further than a box that stands in for a picture or keeps a formatting context of its own - a line
/// opening with an image has no first letter at all.
/// </para>
/// <para>
/// A marker stated <c>inside</c> its item is not in the tree while this runs, because the markers are
/// placed once the whole document has been counted, so the mark cannot be mistaken for the letter. The
/// split letter is wrapped in a box of its own rather than left as a run, since the style may ask for a
/// background, and a run paints none.
/// </para>
/// </remarks>
internal static class FirstLetter
{
    /// <summary>
    /// Cuts the first letter out of the block's content and gives it the style written for it, where the
    /// sheets wrote one and the content has a letter to give.
    /// </summary>
    internal static void Apply(
        Box block,
        IElement element,
        MatchedCascade cascade,
        VariableScope? variables,
        FusedParent parent,
        RuleBucketIndex index,
        HutuRenderDevice device,
        FontFaceTable? faces,
        StyleResolution state,
        ICssParser? parser)
    {
        var pseudo = index.Pseudo.Cascade(PseudoArm.FirstLetter, element, index.Rules, index.Origins);

        if (pseudo is null || !Find(block, out var found))
        {
            return;
        }

        var (host, run) = found.Value;
        var text = run.Text!;
        var taken = Take(text, run.Style);

        if (taken == 0)
        {
            return;
        }

        // The letter keeps what the run it came out of had come to, and takes the declarations written for
        // it over them: it is still where it stood, so the font of the span it was inside of travels with
        // it, and a property the arm never mentions is not the arm's to change.
        var style = FusedStyleFactory.Build(
            element,
            pseudo,
            parent.Next(run.Style, cascade, variables, state.Tag(element.LocalName), false),
            device,
            faces,
            state.Pool,
            state.Cache,
            variables);

        // `initial-letter` makes the letter a drop cap: it is drawn at the size the lines it spans ask
        // for and floats, so the lines beside it are shortened until its box ends (CSS Inline 3 §3.2).
        // A run the builder wrapped in an anonymous block is still this block's own content - an empty
        // block or a float standing before the text is enough to make one - so the letter it opens is
        // still this block's letter.
        var lettering = Lettering(pseudo.Find("initial-letter")?.Value);
        var initial = lettering is { } asked && (ReferenceEquals(host, block) || host.IsAnonymous)
            ? MeasureInitial(block, style, asked, device)
            : null;

        // Nothing the engine draws would change, so the text is left as the one shaped run it was. A letter
        // cut out of it is a boundary the shaper has to stop at, and CSS Text 3 §4.4 asks for shaping to
        // carry across a boundary that nothing at it says otherwise about - which is what the borrowed
        // text-transform-shaping pairs show: an arm that only asks for a capitalisation this engine never
        // reads must leave the word whole.
        if (initial is null && DrawnTheSame(run.Style, style))
        {
            return;
        }

        var at = host.Children.IndexOf(run);
        var letter = Letter(run, text[..taken], style, initial is not null);

        if (initial is { } box)
        {
            // The letter stands at the start of the first line, so a right-to-left row floats it to the
            // right edge rather than the left one, and a column floats it to the top - line-left, which
            // is the same edge in every writing mode (CSS Inline 3 §3.1).
            var vertical = block.Flow.IsVertical;
            var side = !vertical && block.Flow.IsRightToLeft ? FloatSide.Right : FloatSide.Left;
            var indent = block.Style.TextIndent;
            var empty = StyleLength.Auto;

            // A row's letter is stood off the block-start edge - the top - by the cap alignment, and takes
            // the line's indentation on the edge it floats to (§7.6.1, §7.8.2). A column's letter is stood
            // off the block-start edge, which is the right one in vertical-rl, and its box takes the whole
            // em square across the column.
            var extra = vertical
                ? block.Flow.BlockSign < 0 ? new Thickness(0, box.Top, 0, 0) : new Thickness(0, 0, 0, box.Top)
                : new Thickness(box.Top, 0, 0, 0);

            var indented = vertical
                ? new StyleThickness(empty, empty, empty, empty)
                : side == FloatSide.Right
                    ? new StyleThickness(empty, indent, empty, empty)
                    : new StyleThickness(empty, empty, empty, indent);

            style.MakeInitialLetter(
                box.FontSize,
                box.LineHeight,
                vertical ? box.Height : 0,
                box.Height,
                extra,
                side,
                indented);
        }

        if (taken == text.Length)
        {
            host.Children.RemoveAt(at);
        }
        else
        {
            host.Children[at] = Remainder(run, text[taken..]);
        }

        // A letter wrapped in an anonymous block joins the block itself, so that it floats beside every
        // float the block holds rather than beside only those inside its anonymous box.
        if (host.IsAnonymous && initial is not null)
        {
            var own = block.Children.IndexOf(host);
            block.Children.Insert(own, letter);

            if (host.Children.Count == 0)
            {
                block.Children.Remove(host);
            }
        }
        else
        {
            host.Children.Insert(at, letter);
        }

        Regroup(host, initial is not null);

        if (initial is { Push: > 0 } pushed)
        {
            Push(block, letter, pushed.Push);
        }
    }

    /// <summary>
    /// Stands the block's text that much lower, at the anonymous box the letter floats beside.
    /// </summary>
    private static void Push(Box block, Box letter, Double push)
    {
        foreach (var child in block.Children)
        {
            if (child.IsAnonymous && !ReferenceEquals(child, letter))
            {
                child.Style.AddTopMargin(StyleLength.FromPixels(push));
                return;
            }
        }
    }

    /// <summary>One initial letter's used shape, worked out before the letter's style is changed.</summary>
    /// <param name="Top">
    /// The distance the letter is stood off the block-start edge of the lines it opens: the cap
    /// alignment for a row, which is positive inward, and the share of its own box that hangs past
    /// the columns it spans for a column, which is negative when the box is wider than they are.
    /// </param>
    /// <param name="Push">
    /// How far the content beside the letter moves down: the lines the letter is high that it does not
    /// sink through, which the block-start edge rule turns into the originating line and everything
    /// after it standing that much lower (CSS Inline 3 §7.6.1).
    /// </param>
    private readonly record struct InitialBox(
        Double FontSize,
        Double LineHeight,
        Double Height,
        Double Top,
        Double Push);

    /// <summary>
    /// Reads an <c>initial-letter</c> value: the number of lines the letter's cap height spans, and how
    /// many lines it sinks through - the second integer, the legacy <c>drop</c> word standing for the
    /// number itself and <c>raise</c> for one line.
    /// </summary>
    private static (Int32 Size, Int32 Sink)? Lettering(String? value)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var size = 0;
        var sink = 0;
        var raised = false;

        foreach (var word in value.Split((Char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Int32.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= 1)
            {
                if (size == 0)
                {
                    size = count;
                }
                else if (sink == 0)
                {
                    sink = count;
                }

                continue;
            }

            if (String.Equals(word, "raise", StringComparison.OrdinalIgnoreCase))
            {
                raised = true;
            }
            else if (!String.Equals(word, "drop", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(word, "normal", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        if (size == 0)
        {
            return null;
        }

        return (size, Math.Max(1, raised ? 1 : sink == 0 ? size : sink));
    }

    /// <summary>
    /// Works out the initial letter's font size and box from the block's own lines and font: the cap
    /// height has to reach from the first line's cap top down to the baseline of the line the letter
    /// sinks to, and the box is the letter's em square at that size, floating from the cap top.
    /// </summary>
    private static InitialBox? MeasureInitial(Box block, ComputedStyle style, (Int32 Size, Int32 Sink) asked, HutuRenderDevice device)
    {
        if (device.Text is not { } measurer)
        {
            return null;
        }

        var font = block.Style.GetFont();
        var lineHeight = block.Style.ResolvedFont.LineHeight;

        if (lineHeight is not { } height || height <= 0 || font.Size <= 0)
        {
            return null;
        }

        var metrics = measurer.GetMetrics(font);

        if (metrics.CapHeight <= 0)
        {
            return null;
        }

        var capRatio = metrics.CapHeight / font.Size;
        var emRatio = (metrics.Ascent + metrics.Descent) / font.Size;
        var capTop = (height - (metrics.Ascent + metrics.Descent)) / 2 + (metrics.Ascent - metrics.CapHeight);
        var target = (asked.Size - 1) * height + metrics.CapHeight;
        var fontSize = target / capRatio;
        var box = emRatio * fontSize;

        // A column's letter stands centred on the lines it spans along the block axis: measured in
        // Chrome, a 20px Ahem face over 24px lines draws the box for `initial-letter: 3` four pixels
        // past the column edge (80 against three columns of 72), three at 30px lines and none at 40px,
        // which is the letter's own block extent shared out on either side of theirs. Its line extent
        // starts at the line's own start edge, with no half-leading to line a cap up to.
        if (block.Flow.IsVertical)
        {
            // A sideways mode turns the glyphs, so the cap height runs along the block axis and the
            // letter lines up by it exactly as a row's does. A column that keeps them upright aligns
            // the whole block extent of the letter on the lines it spans instead.
            return new InitialBox(
                fontSize,
                box,
                box,
                block.Flow.IsSidewaysMode ? capTop : (asked.Size * height - box) / 2,
                0);
        }

        // The letter's cap top stays at the first line's cap top whatever it sinks through: lining it
        // up with the baseline of the line it sinks to would lift it out of the block, which §7.6.1
        // forbids, so the lines it does not sink through are the ones that move instead.
        return new InitialBox(fontSize, box, box, capTop, Math.Max(0, asked.Size - asked.Sink) * height);
    }

    /// <summary>
    /// Puts the block's children back together now that the letter is a floated block of its own: the
    /// text that follows it is inline content beside a block, and has to become the anonymous block the
    /// float's lines are laid out in.
    /// </summary>
    private static void Regroup(Box host, Boolean initial)
    {
        if (!initial)
        {
            return;
        }

        var children = new System.Collections.Generic.List<Box>(host.Children);
        host.Children.Clear();
        BoxBuilder.GroupChildren(host, children);
    }

    /// <summary>
    /// Gets the box the letter is wrapped in, standing where the run it was cut from stood.
    /// </summary>
    private static Box Letter(Box run, String text, ComputedStyle style, Boolean block = false)
    {
        var letter = new Box(block ? BoxKind.Block : BoxKind.Inline, style)
        {
            Element = run.Element,
            Flow = run.Flow,
            IsGenerated = run.IsGenerated,
            IsInitialLetter = block,
        };

        letter.Children.Add(new Box(BoxKind.Text, style)
        {
            Element = run.Element,
            Source = run.Source,
            Text = text,
            Flow = run.Flow,
            IsGenerated = run.IsGenerated,
        });

        return letter;
    }

    /// <summary>Gets what of the run the letter left behind.</summary>
    private static Box Remainder(Box run, String text) =>
        new Box(BoxKind.Text, run.Style)
        {
            Element = run.Element,
            Source = run.Source,
            Text = text,
            Flow = run.Flow,
            IsGenerated = run.IsGenerated,
        };

    /// <summary>
    /// Gets whether a first letter is asked of a box of this kind at all: the pseudo-element belongs to a
    /// block container, and an atomic inline that keeps a formatting context of its own is one - which is
    /// what a <c>display: inline flow-root</c> box is, and what the borrowed
    /// <c>first-letter-exclude-inline-marker</c> writes <c>::first-letter</c> about.
    /// </summary>
    internal static Boolean AppliesTo(BoxKind kind, ComputedStyle style) =>
        kind is BoxKind.Block or BoxKind.InlineBlock or BoxKind.TableCell ||
        (kind == BoxKind.Inline && style.Display.Contains("flow-root", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Finds the run the block's content opens with, and the box holding it. False when the content opens
    /// with something a first letter cannot be: a replaced box, a box that keeps its own layout, or nothing.
    /// </summary>
    private static Boolean Find(Box parent, out (Box Host, Box Run)? found)
    {
        foreach (var child in parent.Children)
        {
            if (child.Kind == BoxKind.Text && child.Text is { Length: > 0 } text &&
                !String.IsNullOrWhiteSpace(text))
            {
                found = (parent, child);
                return true;
            }

            // A line that opens with a picture, or with a box that lays its own content out, has no first
            // letter: §5.1 puts the letter at the head of the line, and something else is there. A child
            // that keeps a formatting context of its own is such a box, and it is why the borrowed pairs
            // write no first letter inside an <c>inline flow-root</c> - its own arm, not the parent's.
            if (child.IsReplaced || child.Kind is BoxKind.InlineBlock or BoxKind.Table or BoxKind.Flex or
                BoxKind.Grid or BoxKind.Masonry or BoxKind.LineBreak or BoxKind.HorizontalRule ||
                KeepsItsOwnContext(child))
            {
                found = null;
                return false;
            }

            if (child.Kind is BoxKind.Inline or BoxKind.Block && Find(child, out var inside))
            {
                found = inside;
                return true;
            }
        }

        found = null;
        return false;
    }

    private static Boolean KeepsItsOwnContext(Box box) =>
        box.Style.Display.Contains("flow-root", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets how many code units of the run the first letter is: the punctuation that stands in front of it
    /// and the one grapheme cluster that is it. Zero where the run offers no letter at all.
    /// </summary>
    private static Int32 Take(String text, ComputedStyle style)
    {
        var keepsBreaks = style.KeepsSegmentBreaks;
        var at = 0;

        while (at < text.Length)
        {
            var length = Cluster(text, at);
            var c = text[at];

            // A break the white-space rules keep opens the block with a line that holds nothing, and the
            // letter of a later line is nobody's first letter - which is what the borrowed
            // first-letter-with-preceding-new-line and pre-line-3 pairs draw for it.
            if (keepsBreaks && c is '\n' or '\r')
            {
                return 0;
            }

            // White space the line may collapse stands before the letter rather than in front of it, and is
            // left out of what the pseudo-element takes.
            if (IsCollapsible(c))
            {
                at += length;
                continue;
            }

            if (IsLetter(c))
            {
                return at + length;
            }

            // A quotation mark, a period, a space that will not collapse: punctuation goes with the letter
            // it opens, which is why Chrome measures `...x` as four.
            at += length;
        }

        return 0;
    }

    /// <summary>
    /// Gets whether two styles would put the same glyphs on the page: the colour, the font it is drawn in,
    /// the room between the characters, the background it stands on, how strongly it is laid down and
    /// whether it is seen at all.
    /// </summary>
    /// <remarks>
    /// This is the question the split has to answer before it breaks a shaped word in two (CSS Text 3 §4.4
    /// keeps shaping whole across a boundary nothing at it disagrees about), and it is asked of the values
    /// the engine paints with rather than of the declarations the sheet wrote: an arm that asks only for a
    /// capitalisation, a transformation or a property the engine has no reader for changes nothing here.
    /// </remarks>
    private static Boolean DrawnTheSame(ComputedStyle from, ComputedStyle to) =>
        from.Color == to.Color &&
        from.BackgroundColor == to.BackgroundColor &&
        from.FontFamily == to.FontFamily &&
        from.FontSize == to.FontSize &&
        from.FontWeight == to.FontWeight &&
        from.IsItalic == to.IsItalic &&
        from.LetterSpacing == to.LetterSpacing &&
        from.WordSpacing == to.WordSpacing &&
        from.LineHeight == to.LineHeight &&
        from.TextFillColor == to.TextFillColor &&
        from.TextStrokeWidth == to.TextStrokeWidth &&
        from.Opacity == to.Opacity &&
        from.Visibility == to.Visibility;

    /// <summary>Gets the length of the grapheme cluster beginning at <paramref name="at"/>.</summary>
    private static Int32 Cluster(String text, Int32 at)
    {
        if (text[at] < 0x80 && (at + 1 == text.Length || text[at + 1] < 0x80))
        {
            // Nothing ASCII stands for is a mark, a joiner or half of a pair, and a character followed by
            // another plain one is a cluster of one. The second unit is asked about because an e under an
            // acute begins with an ASCII letter and its mark does not.
            return 1;
        }

        var elements = StringInfo.GetTextElementEnumerator(text, at);

        return elements.MoveNext() ? ((String)elements.Current).Length : 1;
    }

    private static Boolean IsLetter(Char c) => Char.IsLetter(c) || Char.IsDigit(c);

    private static Boolean IsCollapsible(Char c) =>
        c is ' ' or '\t' or '\n' or '\r' or '\f';
}

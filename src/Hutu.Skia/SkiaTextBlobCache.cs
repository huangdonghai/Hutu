using Hutu.Rendering.Layout;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Hutu.Skia;

/// <summary>
/// Keeps the drawn form of text: a positioned <see cref="SKTextBlob"/> per text, style and spacing,
/// built once out of the decomposition the measurer answers, and drawn as one call after that.
/// </summary>
/// <remarks>
/// Building a blob is where drawing text spends its per-frame time - the glyph buffers, the
/// positions and the native object behind them are made again for text a page draws every frame.
/// The blobs here carry their glyphs relative to their own origin, so the one object answers every
/// frame and every scroll: the caller draws it at the run's position and the glyphs land where the
/// layout measured them. A column's blob centres each upright character across the em box, so it
/// answers at whatever width the column is drawn at as well.
/// </remarks>
internal sealed class SkiaTextBlobCache : IDisposable
{
    private readonly SkiaTextMeasurer _measurer;
    private readonly ConcurrentDictionary<TextBlobKey, TextBlob> _blobs = new();

    // A glyph's ink box is the shape of what it puts on the page, and it belongs to the face rather than
    // to the run: the same glyph asked by a decoration and by the next one is asked for the same answer.
    private readonly Dictionary<(Int32 Font, UInt16 Glyph), SKRect> _bounds = new();

    // A blob is native memory, so the loser of the usual get-or-add race would leak what it built:
    // the build is kept under a lock, which costs nothing that matters, since it runs once per key.
    private readonly Object _gate = new();

    public SkiaTextBlobCache(SkiaTextMeasurer measurer) => _measurer = measurer;

    /// <summary>
    /// Gets the blob a run of horizontal text is drawn as, with its glyphs positioned relative to the
    /// run's own origin, so it is drawn at wherever the run starts.
    /// </summary>
    /// <summary>
    /// Gets the blob a run of horizontal text is drawn as, with its glyphs positioned relative to the
    /// run's own origin, so it is drawn at wherever the run starts.
    /// </summary>
    /// <param name="justification">
    /// The room a justified line owes after every place between the run's words, and how many of those
    /// places the text holds: the glyphs set after each of them stand that much further along, which is
    /// what makes the drawn text fill the room it was measured into rather than bunched at one end of it.
    /// </param>
    /// <param name="wordSpacing">
    /// The room <c>word-spacing</c> adds after each of the run's word separators - the same count the
    /// layout charged the run for, so the glyphs land where the run's width says they stand.
    /// </param>
    /// <summary>
    /// Gets the ink each glyph of a run puts on the page, in the same space the blob's glyphs are
    /// positioned in: relative to the run's origin, with the baseline as the zero the glyphs stand on. A
    /// decoration that skips ink is cut around these.
    /// </summary>
    public SKRect[] GetRunInk(
        FontStyle style,
        String text,
        Double letterSpacing,
        Double justification = 0,
        Int32 justifies = 0,
        String expand = "auto",
        Double wordSpacing = 0,
        Boolean reverse = false,
        String? trims = null) =>
        GetBlob(BlobKind.Run, style, text, letterSpacing, justification, justifies, expand, wordSpacing, reverse, trims).Ink;

    /// <summary>Gets the ink of a column's glyphs, in the space the column's blob is positioned in.</summary>
    public SKRect[] GetColumnInk(
        FontStyle style,
        String text,
        Double letterSpacing,
        Double justification = 0,
        Int32 justifies = 0,
        String expand = "auto",
        Double wordSpacing = 0,
        Boolean reverse = false,
        String? trims = null) =>
        GetBlob(BlobKind.Column, style, text, letterSpacing, justification, justifies, expand, wordSpacing, reverse, trims).Ink;

    public SKTextBlob? GetRunBlob(
        FontStyle style,
        String text,
        Double letterSpacing,
        Double justification = 0,
        Int32 justifies = 0,
        String expand = "auto",
        Double wordSpacing = 0,
        Boolean reverse = false,
        String? trims = null) =>
        GetBlob(BlobKind.Run, style, text, letterSpacing, justification, justifies, expand, wordSpacing, reverse, trims).Blob;

    /// <summary>
    /// Gets the blob a column of upright characters is drawn as, with its glyphs positioned relative
    /// to the column's own origin, so it is drawn at wherever the column starts.
    /// </summary>
    /// <remarks>
    /// Each character is centred across the em box rather than across the column's own width, so the
    /// one blob answers a column of any width: the caller draws it where the column's width puts it.
    /// </remarks>
    /// <param name="justification">
    /// The room a justified line owes after each of this column's places, which every cell after that
    /// place carries down the column with it - the same debt the run along a line is drawn with, paid
    /// here between the upright cells rather than between the glyphs of one run.
    /// </param>
    /// <param name="justifies">How many places this column holds shares for.</param>
    /// <param name="expand">Which boundaries of the text those places are, as text-justify says.</param>
    public SKTextBlob? GetColumnBlob(
        FontStyle style,
        String text,
        Double letterSpacing,
        Double justification = 0,
        Int32 justifies = 0,
        String expand = "auto",
        Double wordSpacing = 0,
        Boolean reverse = false,
        String? trims = null) =>
        GetBlob(BlobKind.Column, style, text, letterSpacing, justification, justifies, expand, wordSpacing, reverse, trims).Blob;

    /// <summary>
    /// Gets the blob a run or column is drawn as.
    /// </summary>
    /// <param name="reverse">
    /// Whether the text stands back to front along its own extent, which is how a line of
    /// <c>direction: rtl</c> lays the left-to-right text sitting on it: the run's box is where the line
    /// put it, and the characters fill it from the other end. Each glyph keeps its own face.
    /// </param>
    private TextBlob GetBlob(
        BlobKind kind,
        FontStyle style,
        String text,
        Double letterSpacing,
        Double justification,
        Int32 justifies,
        String expand,
        Double wordSpacing,
        Boolean reverse,
        String? trims)
    {
        // A mask of zeros says nothing: keeping it would only split the cache by a string that can
        // never change an answer.
        if (trims is not null && !HasTrims(trims))
        {
            trims = null;
        }

        var key = new TextBlobKey(
            kind, style, text, letterSpacing, justification, justifies, expand, wordSpacing, reverse, trims);

        lock (_gate)
        {
            if (_blobs.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var ink = new List<SKRect>();
            var blob = kind == BlobKind.Run
                ? BuildRunBlob(style, text, letterSpacing, justification, justifies, expand, wordSpacing, reverse, trims, ink)
                : BuildColumnBlob(style, text, letterSpacing, justification, justifies, expand, wordSpacing, reverse, trims, ink);
            var built = new TextBlob(blob, [.. ink]);
            _blobs[key] = built;
            return built;
        }
    }

    /// <summary>
    /// Empties the cache, so the next draw builds its blobs out of the glyphs of the faces that are
    /// here now rather than the ones a face still on the wire was standing in for.
    /// </summary>
    /// <remarks>
    /// What is dropped is not disposed: a paint already underway may still be drawing one of the
    /// blobs, and each is native memory the next build lays down again anyway. The gate is taken so a
    /// build racing the clear cannot put the blob it made back into the emptied cache.
    /// </remarks>
    public void Clear()
    {
        lock (_gate)
        {
            _blobs.Clear();
        }
    }

    public void Dispose()
    {
        foreach (var blob in _blobs.Values)
        {
            blob.Blob?.Dispose();
        }

        _blobs.Clear();
    }

    /// <summary>
    /// Builds a run's blob out of the runs of the decomposition: each stretch contributes one
    /// positioned run of the glyphs its font set it in, starting where the stretches before it ended.
    /// </summary>
    /// <remarks>
    /// The letter spacing is folded into each glyph's position and into the distance the next stretch
    /// starts at by the characters the stretch is made of - the same count the layout charged the line
    /// for, which is what makes the drawn text occupy exactly the room it was measured into. Where a
    /// glyph is set, though, is the shaper's knowledge: two glyphs welded out of one character are
    /// still standing in it, and only the buffer says which glyphs came from where.
    /// </remarks>
    private SKTextBlob? BuildRunBlob(
        FontStyle style,
        String text,
        Double letterSpacing,
        Double justification,
        Int32 justifies,
        String expand,
        Double wordSpacing,
        Boolean reverse,
        String? trims,
        List<SKRect> ink)
    {
        using var builder = new SKTextBlobBuilder();
        var placements = new List<Placement>();
        var pen = 0f;

        // A mask is aligned with the text the fragment wrote; the decomposition drops the characters
        // that only steer the ordering, so a text carrying them no longer lines up with its mask.
        if (trims is not null && (trims.Length != text.Length || HasBidiControls(text)))
        {
            trims = null;
        }

        // The places the line still owes a share to, paid out in the order the text runs: a stretch
        // takes as many as it holds and the rest go to the stretches after it.
        var owing = justifies;
        var points = Places(text, justifies, justification, expand);
        var separators = wordSpacing == 0
            ? Array.Empty<Int32>()
            : TextClusters.WordSeparatorPlaces(text);
        var offset = 0;

        foreach (var run in _measurer.Decompose(text, style))
        {
            var characters = letterSpacing == 0
                ? 0
                : TextClusters.Count(run.Text, 0, run.Text.Length);

            // The places were found in the whole run's text, so each stretch is given the ones that fall
            // inside it, told again from where the stretch itself begins.
            var ends = Slice(points, offset, run.Text.Length);
            var places = Math.Min(ends.Length, owing);
            owing -= places;
            var words = Slice(separators, offset, run.Text.Length);
            var runTrims = SliceTrims(trims, offset, run.Text.Length);

            var clusters = (letterSpacing != 0 || justification != 0 || wordSpacing != 0 || runTrims is not null) &&
                run.Shaped.Succeeded
                    ? _measurer.Shaper.ShapeClusters(run.Font, run.Text, style.Features)
                    : null;

            if (clusters is { Length: > 0 } && clusters.Length != run.Shaped.Glyphs.Length)
            {
                // A shaped answer and a clustered answer that disagree are not the same run twice
                // told: the spacing then falls by the glyph, as it did before it was asked where.
                clusters = null;
            }

            var trimmed = run.Shaped.Succeeded
                ? AddShaped(
                    placements, run, pen, letterSpacing, justification, places, ends,
                    wordSpacing, words, clusters, runTrims)
                : AddMapped(placements, run, pen, letterSpacing, wordSpacing, words, runTrims);

            offset += run.Text.Length;

            pen += (Single)(run.Advance + (letterSpacing * characters) + (justification * places) +
                (wordSpacing * words.Length) - trimmed);
        }

        Emit(builder, placements, pen, along: true, reverse, ink);

        return builder.Build();
    }

    /// <summary>
    /// Gets the places of the whole run that fall inside one stretch of it, named from where the stretch
    /// begins - which is the frame the glyphs of that stretch are set out in.
    /// </summary>
    private static Int32[] Slice(Int32[] points, Int32 start, Int32 length)
    {
        if (points.Length == 0)
        {
            return points;
        }

        var inside = new List<Int32>(points.Length);

        foreach (var point in points)
        {
            if (point >= start && point < start + length)
            {
                inside.Add(point - start);
            }
        }

        return inside.ToArray();
    }

    /// <summary>
    /// Gets where the room of a justified run goes, as the index within it after which each share is
    /// paid: the end of every run of white space and, where the text expands between its characters,
    /// every seam the style asks for between them.
    /// </summary>
    private static Int32[] Places(String text, Int32 justifies, Double justification, String expand)
    {
        if (justification == 0)
        {
            return Array.Empty<Int32>();
        }

        var points = TextClusters.ExpansionPoints(text, 0, text.Length, expand);

        // The seam a run cannot see in its own text - the one between its last ideograph and the first
        // of what the line goes on with, which the layout counted for this run - is paid at its end,
        // where it moves none of this run's glyphs and only carries the pen on.
        return points.Length < justifies ? [.. points, text.Length - 1] : points;
    }

    /// <summary>Gets whether a trim mask changes any character's room.</summary>
    private static Boolean HasTrims(String trims)
    {
        foreach (var code in trims)
        {
            if (code != '\0')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Gets the part of a mask one stretch of the text covers, or null where it trims nothing.</summary>
    private static String? SliceTrims(String? trims, Int32 start, Int32 length)
    {
        if (trims is null || start < 0 || length < 0 || start + length > trims.Length)
        {
            return null;
        }

        var slice = trims.Substring(start, length);

        return HasTrims(slice) ? slice : null;
    }

    /// <summary>
    /// Moves a trimmed glyph's ink into the half of its cell the trim left for it. The mask says which
    /// side the character belongs to, but the glyph decides where its ink stands: a colon the language
    /// calls closing may be drawn in the middle, and the half-width form then moves it a quarter
    /// instead of not moving it at all.
    /// </summary>
    private Single TrimShift(SKFont font, UInt16 glyph, Double delta, Char code)
    {
        var box = Bounds(font, glyph);

        if (!box.IsEmpty)
        {
            var half = (Single)delta;

            if (box.Right <= half)
            {
                return 0;
            }

            if (box.Left >= half)
            {
                return (Single)(-delta);
            }

            if (box.Width <= half && box.Left >= half / 2)
            {
                return (Single)(-delta / 2);
            }
        }

        return code switch
        {
            '\u0001' => (Single)(-delta),
            '\u0003' => (Single)(-delta / 2),
            _ => 0,
        };
    }

    /// <summary>
    /// Gets how far one character advances in the shaped run: the distance from the first glyph of its
    /// cluster to the first glyph of the next, or to the end of the run for the last cluster.
    /// </summary>
    private static Double ClusterAdvance(
        SKPoint[] points, Int32[]? clusters, Double runAdvance, Int32 index, Int32 count)
    {
        var next = index + 1;

        if (clusters is not null)
        {
            while (next < count && clusters[next] == clusters[index])
            {
                next++;
            }
        }

        var end = next < count ? points[next].X : (Single)runAdvance;

        return end - points[index].X;
    }

    /// <summary>Gets whether the text carries characters that only steer the ordering and paint nothing.</summary>
    private static Boolean HasBidiControls(String text)
    {
        foreach (var character in text)
        {
            if (character is '\u061C' or '\u200E' or '\u200F' or
                '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' or
                '\u2066' or '\u2067' or '\u2068' or '\u2069')
            {
                return true;
            }
        }

        return false;
    }

    private Double AddShaped(
        List<Placement> placements,
        SkiaTextRun run,
        Single pen,
        Double letterSpacing,
        Double justification,
        Int32 places,
        Int32[] ends,
        Double wordSpacing,
        Int32[] words,
        Int32[]? clusters,
        String? trims)
    {
        var glyphs = run.Shaped.Glyphs;
        var points = run.Shaped.Points;
        var count = glyphs.Length;

        if (count == 0 || points.Length < count)
        {
            return 0;
        }

        var positions = new SKPoint[count];
        var cells = new Single[count];
        var crossed = 0;
        var spread = 0;
        var reached = 0;
        var paid = 0;

        // A trimmed character takes half its advance out of the room the following glyphs stand in;
        // its own ink moves by that half (or a quarter for a centered middle dot), which is where the
        // half-width form would have put it. The counts are kept per cluster, because the glyphs a
        // face makes from one character stand together in one cell.
        var trimBefore = 0.0;
        var clusterDelta = 0.0;
        var clusterShift = 0f;
        var clusterCode = '\0';

        // The pen walks the text in the order the glyphs are drawn in, which is the order the writing
        // runs: a right-to-left run's characters count backwards from where they stand in the string, so
        // the places it has left behind are the ones further along the string rather than earlier.
        var backwards = clusters is { Length: > 1 } && clusters[0] > clusters[^1];

        for (var index = 0; index < count; index++)
        {
            // A place between two words is paid for once the pen has gone past it, and a glyph made
            // from the same character as the one before it has crossed nothing either way.
            while (clusters is not null &&
                   index < clusters.Length &&
                   spread < places &&
                   reached < ends.Length &&
                   (backwards
                        ? ends[reached] > clusters[index]
                        : ends[reached] < clusters[index]))
            {
                reached++;
                spread++;
            }

            // The room of word-spacing is owed at every separator rather than shared out among some of
            // them, so nothing caps it: the pen pays each one as it walks past.
            while (clusters is not null &&
                   index < clusters.Length &&
                   paid < words.Length &&
                   (backwards
                        ? words[paid] > clusters[index]
                        : words[paid] < clusters[index]))
            {
                paid++;
            }

            var newCluster = index == 0 || clusters is null || clusters[index] != clusters[index - 1];

            if (newCluster)
            {
                if (index > 0 && clusterCode != '\0')
                {
                    trimBefore += clusterDelta;
                }

                var character = clusters is not null && index < clusters.Length ? clusters[index] : index;
                clusterCode = trims is not null && character >= 0 && character < trims.Length
                    ? trims[character]
                    : '\0';
                clusterDelta = 0;
                clusterShift = 0;

                if (clusterCode != '\0')
                {
                    clusterDelta = ClusterAdvance(points, clusters, run.Shaped.Advance, index, count) / 2;
                    clusterShift = TrimShift(run.Font, glyphs[index], clusterDelta, clusterCode);
                }

                if (index > 0)
                {
                    crossed++;
                }
            }

            var cell = pen + points[index].X +
                (Single)((letterSpacing * crossed) + (justification * spread) + (wordSpacing * paid)) -
                (Single)trimBefore;

            positions[index] = new SKPoint(cell + clusterShift, points[index].Y);

            // The room the glyph owns begins where the pen stood when it was set, before the ink was
            // moved to its half of the cell; that is the cell a back-to-front run moves.
            cells[index] = cell;
        }

        placements.Add(new Placement(run.Font, glyphs, positions, cells));

        return trimBefore + (clusterCode != '\0' ? clusterDelta : 0);
    }

    /// <summary>
    /// Adds a run the shaper could not handle as the font's own mapping, one glyph per character,
    /// which is the way it was drawn before shaping answered for it.
    /// </summary>
    private Double AddMapped(
        List<Placement> placements,
        SkiaTextRun run,
        Single pen,
        Double letterSpacing,
        Double wordSpacing,
        Int32[] words,
        String? trims)
    {
        if (run.Text.Length == 0)
        {
            return 0;
        }

        var glyphs = run.Font.GetGlyphs(run.Text);
        var positions = run.Font.GetGlyphPositions(run.Text, new SKPoint(pen, 0));
        var cells = new Single[positions.Length];
        var crossed = 0;
        var trimBefore = 0.0;

        for (var index = 0; index < positions.Length; index++)
        {
            while (crossed < words.Length && words[crossed] < index)
            {
                crossed++;
            }

            // One glyph per character is the whole of what this path knows, so the separators it has
            // left behind are counted by the same index the spacing is. The trim is the em's half,
            // which is the room this path charged a fullwidth punctuation.
            var code = trims is not null && index < trims.Length ? trims[index] : '\0';
            var delta = code == '\0' ? 0 : run.Font.Size / 2;
            var shift = code switch
            {
                '\u0001' => -delta,
                '\u0003' => -delta / 2,
                _ => 0,
            };

            cells[index] = positions[index].X;
            positions[index].X += (Single)((letterSpacing * index) + (wordSpacing * crossed) - trimBefore + shift);
            trimBefore += delta;
        }

        placements.Add(new Placement(run.Font, glyphs, positions, cells));

        return trimBefore;
    }

    /// <summary>
    /// Builds a column's blob out of the cells of the decomposition: one glyph per character, at the
    /// place within its cell the layout's steps and the font's own baseline ask for.
    /// </summary>
    /// <remarks>
    /// The step is the one the layout gave the cell - the character's advance raised to the em box
    /// and the spacing added after it - and where the baseline goes inside that step is the font's
    /// to say, falling back to the middle of the em box for a font that will not. A character with
    /// no glyph of its own to draw is set by its font's own mapping of the text it stands for.
    /// </remarks>
    private SKTextBlob? BuildColumnBlob(
        FontStyle style,
        String text,
        Double letterSpacing,
        Double justification,
        Int32 justifies,
        String expand,
        Double wordSpacing,
        Boolean reverse,
        String? trims,
        List<SKRect> ink)
    {
        var metrics = _measurer.GetMetrics(style);
        var emBox = metrics.Ascent + metrics.Descent;
        var cells = _measurer.DecomposeUpright(text, style);
        var placements = new List<Placement>();

        // The room goes after each place in the text the cells were cut from, which is read by the
        // character each cell stands for rather than by the glyph it draws: the form a font substitutes
        // for a character down a column need not be the character the document wrote.
        var points = Places(text, justifies, justification, expand);
        var separators = wordSpacing == 0
            ? Array.Empty<Int32>()
            : TextClusters.WordSeparatorPlaces(text);

        using var builder = new SKTextBlobBuilder();
        var inline = 0.0;
        var crossed = 0;
        var paid = 0;
        var reached = 0.0;
        var end = 0f;

        foreach (var cell in cells)
        {
            while (crossed < points.Length && points[crossed] < cell.Index)
            {
                crossed++;
            }

            while (paid < separators.Length && separators[paid] < cell.Index)
            {
                paid++;
            }

            var code = trims is not null && cell.Index < trims.Length ? trims[cell.Index] : '\0';
            var delta = style.Size / 2;
            var emStep = cell.ExactAdvance ? cell.Advance : Math.Max(cell.Advance, style.Size);
            var step = emStep + letterSpacing;
            var shift = 0.0;

            if (code != '\0')
            {
                step = emStep - delta + letterSpacing;
                shift = code switch
                {
                    '\u0001' => -delta,
                    '\u0003' => -delta / 2,
                    _ => 0,
                };
            }

            reached = inline + (justification * crossed) + (wordSpacing * paid);
            end = (Single)(reached + step);

            var baseline = cell.BaselineOffset is { } offset
                ? (Single)(reached +
                    (cell.ExactAdvance && style.Size > 0 ? offset * (cell.Advance / style.Size) : offset) +
                    shift)
                : (Single)(reached + ((step - emBox) / 2) + metrics.Ascent + shift);
            var centre = (Single)((emBox - cell.CentreWidth) / 2);

            if (cell.DrawByGlyph)
            {
                AddGlyph(placements, cell.Font, cell.Glyph, centre, baseline, (Single)reached);
            }
            else
            {
                AddMappedText(placements, cell.Font, cell.Text, centre, baseline, (Single)reached, (Single)shift);
            }

            inline += step;
        }

        Emit(builder, placements, end, along: false, reverse, ink);

        return builder.Build();
    }

    private void AddGlyph(
        List<Placement> placements, SKFont font, UInt16 glyph, Single x, Single y, Single cell) =>
        placements.Add(new Placement(font, [glyph], [new SKPoint(x, y)], [cell]));

    private void AddMappedText(
        List<Placement> placements, SKFont font, String text, Single x, Single y, Single cell, Single shift = 0)
    {
        if (text.Length == 0)
        {
            return;
        }

        var glyphs = font.GetGlyphs(text);
        var positions = font.GetGlyphPositions(text, new SKPoint(x, y + shift));
        var cells = new Single[positions.Length];

        // Each of the glyphs the mapping made owns the room from where it stands to the next one down,
        // which is what a back-to-front column moves.
        for (var index = 0; index < cells.Length; index++)
        {
            cells[index] = positions[index].Y;
        }

        placements.Add(new Placement(font, glyphs, positions, cells));
    }

    /// <summary>
    /// Sets the collected glyphs on the builder and records the ink they put down.
    /// </summary>
    /// <param name="reverse">
    /// Whether to move each glyph to the mirror of the room it was given across the run's whole extent,
    /// which is how a line of <c>direction: rtl</c> lays the left-to-right text sitting on it. The face
    /// a glyph is drawn with does not change: it is the order of the characters that turns round.
    /// </param>
    private void Emit(
        SKTextBlobBuilder builder,
        List<Placement> placements,
        Single extent,
        Boolean along,
        Boolean reverse,
        List<SKRect> ink)
    {
        // The room one glyph owns reaches to where the next of the run begins and the last of them to the
        // end of the run, so the cells are counted across the stretches rather than within one.
        var begins = new List<Single>();

        foreach (var placement in placements)
        {
            begins.AddRange(placement.Cells);
        }

        var taken = 0;

        foreach (var placement in placements)
        {
            var positions = placement.Positions;

            if (reverse)
            {
                var moved = new SKPoint[positions.Length];

                for (var index = 0; index < positions.Length; index++)
                {
                    var stop = taken + index + 1 < begins.Count ? begins[taken + index + 1] : extent;
                    var offset = along
                        ? positions[index].X - placement.Cells[index]
                        : positions[index].Y - placement.Cells[index];

                    moved[index] = along
                        ? new SKPoint(extent - stop + offset, positions[index].Y)
                        : new SKPoint(positions[index].X, extent - stop + offset);
                }

                positions = moved;
            }

            taken += positions.Length;

            var placed = builder.AllocatePositionedRun(placement.Font, placement.Glyphs.Length);
            placed.SetGlyphs(placement.Glyphs);
            placed.SetPositions(positions);
            Record(placement.Font, placement.Glyphs, positions, ink);
        }
    }

    /// <summary>
    /// Keeps the ink box of every glyph that was just placed, in the space the position was given in: the
    /// face says how far the glyph reaches from the origin it stands at, and a decoration that skips ink is
    /// cut around that rather than around the room the glyph was given. A glyph that puts nothing down -
    /// the space, above all - leaves nothing to cut around.
    /// </summary>
    private void Record(
        SKFont font, ReadOnlySpan<UInt16> glyphs, ReadOnlySpan<SKPoint> positions, List<SKRect> ink)
    {
        for (var index = 0; index < glyphs.Length && index < positions.Length; index++)
        {
            var box = Bounds(font, glyphs[index]);

            if (box.IsEmpty)
            {
                continue;
            }

            ink.Add(new SKRect(
                positions[index].X + box.Left,
                positions[index].Y + box.Top,
                positions[index].X + box.Right,
                positions[index].Y + box.Bottom));
        }
    }

    /// <summary>
    /// Gets how far a glyph reaches from where it is set, which the face is asked once per glyph rather
    /// than once per drawing of it.
    /// </summary>
    private SKRect Bounds(SKFont font, UInt16 glyph)
    {
        var key = (RuntimeHelpers.GetHashCode(font), glyph);

        if (_bounds.TryGetValue(key, out var known))
        {
            return known;
        }

        using var path = font.GetGlyphPath(glyph);

        // A glyph's outline is given with the baseline as its zero and the page downward, which is the
        // same way the run's positions are counted - so the two add straight together.
        var box = path is null ? SKRect.Empty : path.Bounds;
        _bounds[key] = box;
        return box;
    }

    /// <summary>
    /// A built blob and the ink of the glyphs in it. Both come out of the one walk of the text, which is
    /// where the spacing of every kind is folded into where each glyph stands, so they are kept together
    /// rather than worked out twice.
    /// </summary>
    private sealed record TextBlob(SKTextBlob? Blob, SKRect[] Ink);

    /// <summary>
    /// One stretch of the text as the font that carries it, the glyphs it was set in, where each stands
    /// and where the room each owns begins along the axis the text is set on. Collected before any of it
    /// goes on the builder, so a run asked back to front can move the whole of it at once.
    /// </summary>
    private sealed record Placement(SKFont Font, UInt16[] Glyphs, SKPoint[] Positions, Single[] Cells);

    /// <summary>Whether a blob sets its text along a line or down a column.</summary>
    private enum BlobKind
    {
        Run,
        Column
    }

    /// <summary>The kind, style, text and spacing a blob was built for, which is what makes it reusable.</summary>
    private readonly struct TextBlobKey : IEquatable<TextBlobKey>
    {
        private readonly String _text;
        private readonly String? _trims;

        public TextBlobKey(
            BlobKind kind,
            FontStyle style,
            String text,
            Double spacing,
            Double justification,
            Int32 justifies,
            String expand,
            Double wordSpacing,
            Boolean reversed,
            String? trims)
        {
            Kind = kind;
            Style = style;
            _text = text;
            _trims = trims;
            Spacing = spacing;
            Justification = justification;
            Justifies = justifies;
            Expand = expand;
            WordSpacing = wordSpacing;
            Reversed = reversed;
        }

        public BlobKind Kind { get; }

        /// <summary>Gets whether the text stands back to front along its own extent.</summary>
        public Boolean Reversed { get; }

        public FontStyle Style { get; }

        public Double Spacing { get; }

        /// <summary>Gets the justified room a place between the words is owed, and how many places.</summary>
        public Double Justification { get; }

        public Int32 Justifies { get; }

        /// <summary>Gets which boundaries of the text the room goes after, as text-justify says.</summary>
        public String Expand { get; }

        /// <summary>Gets the room each of the run's word separators was given.</summary>
        public Double WordSpacing { get; }

        public Boolean Equals(TextBlobKey other) =>
            Kind == other.Kind &&
            Reversed == other.Reversed &&
            Style.Equals(other.Style) &&
            Spacing.Equals(other.Spacing) &&
            Justification.Equals(other.Justification) &&
            Justifies == other.Justifies &&
            WordSpacing.Equals(other.WordSpacing) &&
            String.Equals(Expand, other.Expand, StringComparison.OrdinalIgnoreCase) &&
            String.Equals(_text, other._text, StringComparison.Ordinal) &&
            String.Equals(_trims, other._trims, StringComparison.Ordinal);

        public override Boolean Equals(Object? obj) => obj is TextBlobKey other && Equals(other);

        public override Int32 GetHashCode()
        {
            unchecked
            {
                var hash = Kind.GetHashCode();
                hash = (hash * 397) ^ Reversed.GetHashCode();
                hash = (hash * 397) ^ Style.GetHashCode();
                hash = (hash * 397) ^ Spacing.GetHashCode();
                hash = (hash * 397) ^ Justification.GetHashCode();
                hash = (hash * 397) ^ Justifies;
                hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Expand);
                hash = (hash * 397) ^ WordSpacing.GetHashCode();
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(_text);
                return _trims is null ? hash : (hash * 397) ^ StringComparer.Ordinal.GetHashCode(_trims);
            }
        }
    }
}

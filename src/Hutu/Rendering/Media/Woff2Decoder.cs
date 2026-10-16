using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Hutu.Rendering.Media;

/// <summary>
/// Turns a WOFF 2.0 file back into the font file it was packed from.
/// </summary>
/// <remarks>
/// WOFF 2.0 (W3C Recommendation, 2024) is not a font format of its own - it is an SFNT font whose
/// tables were pulled apart, preprocessed, and then run through one Brotli stream. Decoding it is
/// three steps in reverse: undo the Brotli stream, undo the preprocessing applied to a handful of
/// tables, and write the tables back out under an SFNT directory.
/// <para>
/// The preprocessing is what costs the code here. Only three tables can be touched - <c>glyf</c> and
/// <c>loca</c> (§5.1, §5.3) and <c>hmtx</c> (§5.4) - and each transform is optional: <c>glyf</c> and
/// <c>loca</c> announce a null transform with transformation version 3, <c>hmtx</c> with version 0.
/// Everything else is literal and is copied out as it stands. A decoder has to accept whatever an
/// encoder was allowed to emit, so the transformed <c>glyf</c> is read in full here even though most
/// fonts do carry it.
/// </para>
/// <para>
/// What the transformed <c>glyf</c> holds is not glyph records but seven parallel streams, one per
/// field of a glyph record, which is what lets the entropy coder work on like data. Reading a glyph
/// back out means taking one value from each stream in turn and writing the record TrueType expects.
/// </para>
/// </remarks>
internal static class Woff2Decoder
{
    private const UInt32 Signature = 0x774F4632; // 'wOF2'

    /// <summary>The transformation version that means "the original table data, untouched".</summary>
    private const Int32 NullGlyf = 3;

    private const Int32 NullHmtx = 0;

    /// <summary>The tags §4.1 gives an index to, in the order it lists them.</summary>
    private static readonly String[] KnownTags =
    {
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm",
        "glyf", "loca", "prep", "CFF ", "VORG", "EBDT", "EBLC", "gasp", "hdmx", "kern",
        "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC",
        "JSTF", "MATH", "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar",
        "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar", "gvar", "hsty",
        "just", "lcar", "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat",
        "Gloc", "Feat", "Sill",
    };

    private readonly struct Entry
    {
        internal Entry(String tag, UInt32 original, UInt32 transformed, Int32 version)
        {
            Tag = tag;
            Original = original;
            Transformed = transformed;
            Version = version;
        }

        internal String Tag { get; }

        internal UInt32 Original { get; }

        internal UInt32 Transformed { get; }

        internal Int32 Version { get; }

        /// <summary>The bytes this table occupies of the decompressed stream: the transformed size when
        /// a transform was applied, and the original size otherwise (§5).</summary>
        internal UInt32 Length(Byte[] block) => block.Length == 0 ? 0u : (UInt32)block.Length;
    }

    /// <summary>
    /// Gets the SFNT font the given WOFF 2.0 file holds, or null when the file is not one this decoder
    /// can turn back.
    /// </summary>
    /// <remarks>
    /// A null is not an error to report: a caller reads it the way a browser reads a font that failed
    /// to load - skip it and take the next source the <c>src</c> descriptor names.
    /// </remarks>
    internal static Byte[]? ToSfnt(Byte[] file)
    {
        if (file.Length < 48 || U32(file, 0) != Signature)
        {
            return null;
        }

        // §3.2: signature, flavor and length, then numTables and reserved, then the two sizes - the
        // uncompressed one is only a hint, but the compressed one is how long the Brotli block is.
        var flavor = U32(file, 4);
        var count = U16(file, 12);
        var packed = U32(file, 20);

        // A collection is a directory of fonts rather than one font (§4.2), and a face is fetched one
        // at a time here. Nothing is lost by refusing it.
        if (flavor == 0x74746366)
        {
            return null;
        }

        var at = 48;
        var entries = new List<Entry>(count);

        for (var i = 0; i < count; i++)
        {
            if (at >= file.Length)
            {
                return null;
            }

            var flags = file[at++];
            var index = flags & 0x3f;
            var version = (flags >> 6) & 0x03;

            String tag;

            if (index == 63)
            {
                if (at + 4 > file.Length)
                {
                    return null;
                }

                tag = $"{(Char)file[at]}{(Char)file[at + 1]}{(Char)file[at + 2]}{(Char)file[at + 3]}";
                at += 4;
            }
            else if (index < KnownTags.Length)
            {
                tag = KnownTags[index];
            }
            else
            {
                return null;
            }

            if (!Base128(file, ref at, out var original))
            {
                return null;
            }

            // §4.1: the transformed length is present if, and only if, the table was put through a
            // non-null transform.
            var transformed = 0u;

            if (Transformed(tag, version) && !Base128(file, ref at, out transformed))
            {
                return null;
            }

            entries.Add(new Entry(tag, original, transformed, version));
        }

        if (packed > (UInt32)(file.Length - at))
        {
            return null;
        }

        Byte[] data;

        try
        {
            using var source = new MemoryStream(file, at, (Int32)packed, false);
            using var stream = new BrotliStream(source, CompressionMode.Decompress);
            using var sink = new MemoryStream();
            stream.CopyTo(sink);
            data = sink.ToArray();
        }
        catch (Exception)
        {
            return null;
        }

        // §5: the decompressed block is the tables in directory order, back to back, with nothing
        // between them - the sum of the directory's lengths has to come out exactly even.
        var blocks = new List<Byte[]>(entries.Count);
        var cursor = 0;

        for (var i = 0; i < entries.Count; i++)
        {
            var size = Transformed(entries[i].Tag, entries[i].Version)
                ? entries[i].Transformed
                : entries[i].Original;

            if (size > (UInt32)(data.Length - cursor))
            {
                return null;
            }

            var block = new Byte[size];
            Array.Copy(data, cursor, block, 0, (Int32)size);
            blocks.Add(block);
            cursor += (Int32)size;
        }

        if (cursor != data.Length)
        {
            return null;
        }

        Int32[]? bounds = null;

        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Tag != "glyf" || entries[i].Version == NullGlyf)
            {
                continue;
            }

            if (!Glyf(blocks[i], out var glyf, out var loca, out bounds))
            {
                return null;
            }

            blocks[i] = glyf;

            var where = IndexOf(entries, "loca", i);

            if (where < 0)
            {
                return null;
            }

            blocks[where] = loca;
            break;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Tag != "hmtx" || entries[i].Version == NullHmtx)
            {
                continue;
            }

            var metrics = Hmtx(blocks[i], entries, blocks, bounds);

            if (metrics is null)
            {
                return null;
            }

            blocks[i] = metrics;
            break;
        }

        return Sfnt(flavor, entries, blocks);
    }

    /// <summary>Gets whether the table named <paramref name="tag"/> was put through a non-null transform.</summary>
    private static Boolean Transformed(String tag, Int32 version) =>
        tag is "glyf" or "loca" ? version != NullGlyf : tag == "hmtx" && version != NullHmtx;

    private static Int32 IndexOf(List<Entry> entries, String tag, Int32 after)
    {
        for (var i = after + 1; i < entries.Count; i++)
        {
            if (entries[i].Tag == tag)
            {
                return i;
            }
        }

        return -1;
    }

    private static Int32 At(List<Entry> entries, String tag)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Tag == tag)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Reads the transformed <c>glyf</c> table (§5.1) back into glyph records, and with them the
    /// <c>loca</c> table that was dropped when it was transformed (§5.3).
    /// </summary>
    /// <remarks>
    /// The <c>loca</c> entry carries no data of its own - its transformed length is zero - because the
    /// offsets it holds are a by-product of writing the glyph records out: they are where each record
    /// landed. That is why §5.5 makes the two travel as a pair.
    /// </remarks>
    private static Boolean Glyf(Byte[] table, out Byte[] glyf, out Byte[] loca, out Int32[]? bounds)
    {
        glyf = Array.Empty<Byte>();
        loca = Array.Empty<Byte>();
        bounds = null;

        var at = 0;

        if (table.Length < 36)
        {
            return false;
        }

        at += 2; // reserved
        var options = U16(table, at);
        at += 2;
        var glyphs = U16(table, at);
        at += 2;
        var format = U16(table, at);
        at += 2;

        var sizes = new UInt32[7];

        for (var i = 0; i < sizes.Length; i++)
        {
            sizes[i] = U32(table, at);
            at += 4;
        }

        var streams = new Byte[7][];

        for (var i = 0; i < streams.Length; i++)
        {
            if (sizes[i] > (UInt32)(table.Length - at))
            {
                return false;
            }

            streams[i] = new Byte[sizes[i]];
            Array.Copy(table, at, streams[i], 0, (Int32)sizes[i]);
            at += (Int32)sizes[i];
        }

        var contours = streams[0];
        var points = streams[1];
        var flags = streams[2];
        var packed = streams[3];
        var composite = streams[4];
        var boxes = streams[5];
        var code = streams[6];

        // The bounding-box stream opens with a bitmap saying which glyphs carry a box of their own;
        // the rest is the boxes themselves, four Int16 each (§5.1).
        var map = 4 * ((glyphs + 31) / 32);

        if (map > boxes.Length)
        {
            return false;
        }

        var overlap = (options & 0x01) != 0 && at + ((glyphs + 7) / 8) <= table.Length
            ? Slice(table, at, (glyphs + 7) / 8)
            : Array.Empty<Byte>();

        var boxAt = map;
        var contourAt = 0;
        var pointAt = 0;
        var flagAt = 0;
        var packedAt = 0;
        var compositeAt = 0;
        var codeAt = 0;

        var records = new List<Byte>();
        var offsets = new List<Int32>(glyphs + 1);
        var minima = new Int32[glyphs];

        for (var glyph = 0; glyph < glyphs; glyph++)
        {
            offsets.Add(records.Count);

            if (contourAt + 2 > contours.Length)
            {
                return false;
            }

            var numberOf = (Int16)U16(contours, contourAt);
            contourAt += 2;

            var record = new List<Byte>();
            I16(record, numberOf);

            Int32 xMin = 0, yMin = 0, xMax = 0, yMax = 0;

            // What a glyph is made of is read out of the streams first and written out second: the
            // bounding box - which TrueType puts straight after numberOfContours, before any of the
            // outline - is only known once the points have been read, because a box the encoder left
            // out has to be worked out from them.
            var parts = new List<Byte>();
            var instructions = Array.Empty<Byte>();
            var count_ = 0;
            var ends_ = Array.Empty<Int32>();
            var onCurve_ = Array.Empty<Boolean>();
            var xs_ = Array.Empty<Int32>();
            var ys_ = Array.Empty<Int32>();

            if (numberOf > 0)
            {
                var ends = new Int32[numberOf];
                var total = 0;

                for (var i = 0; i < numberOf; i++)
                {
                    if (!Value(points, ref pointAt, out var size))
                    {
                        return false;
                    }

                    total += size;
                    ends[i] = total - 1;
                }

                var onCurve = new Boolean[total];
                var xs = new Int32[total];
                var ys = new Int32[total];

                for (var i = 0; i < total; i++)
                {
                    if (flagAt >= flags.Length)
                    {
                        return false;
                    }

                    var flag = flags[flagAt++];

                    // §5.2: the top bit says on- or off-curve, and it is the wrong way round from
                    // TrueType's - a set bit means the point is off the curve.
                    onCurve[i] = (flag & 0x80) == 0;

                    if (!Coordinates(packed, ref packedAt, flag & 0x7f, out var x, out var y))
                    {
                        return false;
                    }

                    xs[i] = i == 0 ? x : xs[i - 1] + x;
                    ys[i] = i == 0 ? y : ys[i - 1] + y;
                }

                for (var i = 0; i < total; i++)
                {
                    xMin = Math.Min(xMin, xs[i]);
                    yMin = Math.Min(yMin, ys[i]);
                    xMax = Math.Max(xMax, xs[i]);
                    yMax = Math.Max(yMax, ys[i]);
                }

                if (!Value(packed, ref packedAt, out var length))
                {
                    return false;
                }

                if (length > code.Length - codeAt)
                {
                    return false;
                }

                instructions = Slice(code, codeAt, length);
                codeAt += length;
                count_ = numberOf;
                ends_ = ends;
                onCurve_ = onCurve;
                xs_ = xs;
                ys_ = ys;
            }
            else if (numberOf < 0)
            {
                // A composite: flag word, glyph index and arguments per component, then the
                // instructions if any component asked for them (§5.1).
                var more = true;
                var any = false;

                while (more)
                {
                    if (compositeAt + 2 > composite.Length)
                    {
                        return false;
                    }

                    var flag = U16(composite, compositeAt);
                    compositeAt += 2;
                    more = (flag & 0x20) != 0;
                    any |= (flag & 0x0100) != 0;

                    var arguments = 2 +
                        ((flag & 0x0001) != 0 ? 4 : 2) +
                        ((flag & 0x0008) != 0 ? 2 : 0) +
                        ((flag & 0x0040) != 0 ? 4 : 0) +
                        ((flag & 0x0080) != 0 ? 8 : 0);

                    if (arguments > composite.Length - compositeAt)
                    {
                        return false;
                    }

                    U16(parts, flag);
                    Add(parts, composite, compositeAt, arguments);
                    compositeAt += arguments;
                }

                if (any)
                {
                    if (!Value(packed, ref packedAt, out var length) || length > code.Length - codeAt)
                    {
                        return false;
                    }

                    instructions = Slice(code, codeAt, length);
                    codeAt += length;
                }
            }

            // A box written out is taken as it stands; one that is not has to be derived from the
            // outline, which is the same computation the encoder did to decide it could be left out.
            if ((boxes[glyph >> 3] & (0x80 >> (glyph & 7))) != 0)
            {
                if (boxAt + 8 > boxes.Length)
                {
                    return false;
                }

                xMin = (Int16)U16(boxes, boxAt);
                yMin = (Int16)U16(boxes, boxAt + 2);
                xMax = (Int16)U16(boxes, boxAt + 4);
                yMax = (Int16)U16(boxes, boxAt + 6);
                boxAt += 8;
            }

            I16(record, (Int16)xMin);
            I16(record, (Int16)yMin);
            I16(record, (Int16)xMax);
            I16(record, (Int16)yMax);

            if (numberOf > 0)
            {
                for (var i = 0; i < count_; i++)
                {
                    U16(record, ends_[i]);
                }

                U16(record, instructions.Length);
                Add(record, instructions);

                for (var i = 0; i < xs_.Length; i++)
                {
                    // Bit 0 is TrueType's on-curve flag, and it is the opposite polarity to the one
                    // the stream carries; bit 6 is the overlap flag, kept in its own bitmap.
                    var flag = (Byte)(onCurve_[i] ? 0x01 : 0x00);

                    if (overlap.Length > 0 && (overlap[glyph >> 3] & (0x80 >> (glyph & 7))) != 0)
                    {
                        flag |= 0x40;
                    }

                    record.Add(flag);
                }

                // The deltas go out as plain Int16 with neither short-vector bit set. The encoder may
                // have packed them tighter, but a reconstruction only has to be a valid one.
                for (var i = 0; i < xs_.Length; i++)
                {
                    I16(record, (Int16)xs_[i]);
                }

                for (var i = 0; i < ys_.Length; i++)
                {
                    I16(record, (Int16)ys_[i]);
                }
            }
            else if (numberOf < 0)
            {
                Add(record, parts);

                if (instructions.Length > 0)
                {
                    U16(record, instructions.Length);
                    Add(record, instructions);
                }
            }

            minima[glyph] = xMin;
            Add(records, record);
        }

        offsets.Add(records.Count);
        bounds = minima;

        glyf = records.ToArray();

        var writer = new List<Byte>(offsets.Count * (format == 0 ? 2 : 4));

        foreach (var offset in offsets)
        {
            if (format == 0)
            {
                U16(writer, (UInt16)(offset / 2));
            }
            else
            {
                U32(writer, (UInt32)offset);
            }
        }

        loca = writer.ToArray();
        return true;
    }

    /// <summary>
    /// Reads one point's delta-x and delta-y out of the glyph stream (§5.2).
    /// </summary>
    /// <remarks>
    /// The seven low bits of the flag are an index into the triplet encoding: how many bytes the two
    /// coordinates were packed into, how many bits each got, and the sign of each. The 128 rows of that
    /// table collapse into the six cases below, which is how the encoder writes them.
    /// </remarks>
    private static Boolean Coordinates(Byte[] packed, ref Int32 at, Int32 index, out Int32 x, out Int32 y)
    {
        x = 0;
        y = 0;

        var width = index <= 83 ? 1 : index <= 119 ? 2 : index <= 123 ? 3 : 4;

        if (width > packed.Length - at)
        {
            return false;
        }

        var b0 = packed[at];
        var b1 = width > 1 ? packed[at + 1] : (Byte)0;
        var b2 = width > 2 ? packed[at + 2] : (Byte)0;
        var b3 = width > 3 ? packed[at + 3] : (Byte)0;
        at += width;

        Int32 magnitude, other;

        switch (index)
        {
            case <= 9:
                // x is zero; y is a high nibble of 256s from the index and a low byte from the stream.
                magnitude = (index >> 1) * 256 + b0;
                y = (index & 1) == 0 ? -magnitude : magnitude;
                return true;

            case <= 19:
                magnitude = ((index - 10) >> 1) * 256 + b0;
                x = ((index - 10) & 1) == 0 ? -magnitude : magnitude;
                return true;

            case <= 83:
                // Both coordinates are held as a value one less than the truth, in twelve bits split
                // four from the index and eight from the byte.
                var pair = index - 20;
                magnitude = (((pair & 0x30) | (b0 >> 4)) + 1);
                other = ((((pair & 0x0c) << 2) | (b0 & 0x0f)) + 1);
                x = (pair & 1) == 0 ? -magnitude : magnitude;
                y = ((pair >> 1) & 1) == 0 ? -other : other;
                return true;

            case <= 119:
                var sixteen = index - 84;
                magnitude = (((sixteen / 12) << 8) | b0) + 1;
                other = ((((sixteen % 12) & 0x0c) << 6) | b1) + 1;
                x = (sixteen & 1) == 0 ? -magnitude : magnitude;
                y = ((sixteen >> 1) & 1) == 0 ? -other : other;
                return true;

            case <= 123:
                magnitude = (b0 << 4) | (b1 >> 4);
                other = ((b1 & 0x0f) << 8) | b2;
                x = (index & 1) == 0 ? -magnitude : magnitude;
                y = ((index >> 1) & 1) == 0 ? -other : other;
                return true;

            default:
                magnitude = (b0 << 8) | b1;
                other = (b2 << 8) | b3;
                x = (index & 1) == 0 ? -magnitude : magnitude;
                y = ((index >> 1) & 1) == 0 ? -other : other;
                return true;
        }
    }

    /// <summary>
    /// Rebuilds the <c>hmtx</c> table when the transform of §5.4 has been applied to it.
    /// </summary>
    /// <remarks>
    /// The transform rests on a habit of TrueType design: the left side bearing of a glyph is usually
    /// its <c>xMin</c>. Where that holds the array is dropped from the table and has to be read back
    /// off the glyph outlines, which is why this runs after <c>glyf</c> and needs the boxes it built.
    /// </remarks>
    private static Byte[]? Hmtx(Byte[] table, List<Entry> entries, List<Byte[]> blocks, Int32[]? minima)
    {
        if (table.Length == 0 || minima is null)
        {
            return null;
        }

        var hhea = At(entries, "hhea");
        var maxp = At(entries, "maxp");

        if (hhea < 0 || maxp < 0 || blocks[hhea].Length < 36 || blocks[maxp].Length < 6)
        {
            return null;
        }

        var metrics = U16(blocks[hhea], 34);
        var glyphs = U16(blocks[maxp], 4);

        var at = 0;
        var flag = table[at++];
        var widths = new UInt16[metrics];

        for (var i = 0; i < metrics; i++)
        {
            if (at + 2 > table.Length)
            {
                return null;
            }

            widths[i] = U16(table, at);
            at += 2;
        }

        var left = new Int16[metrics];

        if ((flag & 0x01) == 0)
        {
            for (var i = 0; i < metrics && i < minima.Length; i++)
            {
                if (at + 2 > table.Length)
                {
                    return null;
                }

                left[i] = (Int16)U16(table, at);
                at += 2;
            }
        }
        else
        {
            for (var i = 0; i < metrics && i < minima.Length; i++)
            {
                left[i] = (Int16)minima[i];
            }
        }

        var trailing = new Int16[glyphs > metrics ? glyphs - metrics : 0];

        if ((flag & 0x02) == 0)
        {
            for (var i = 0; i < trailing.Length; i++)
            {
                if (at + 2 > table.Length)
                {
                    return null;
                }

                trailing[i] = (Int16)U16(table, at);
                at += 2;
            }
        }
        else
        {
            for (var i = 0; i < trailing.Length && metrics + i < minima.Length; i++)
            {
                trailing[i] = (Int16)minima[metrics + i];
            }
        }

        var writer = new List<Byte>(4 + glyphs * 4);

        for (var i = 0; i < metrics; i++)
        {
            U16(writer, widths[i]);
            I16(writer, left[i]);
        }

        for (var i = 0; i < trailing.Length; i++)
        {
            I16(writer, trailing[i]);
        }

        return writer.ToArray();
    }

    /// <summary>
    /// Writes the tables out as an SFNT file: a directory sorted by tag, the tables four-byte aligned
    /// behind it, and the checksums both the directory and the <c>head</c> table ask for.
    /// </summary>
    private static Byte[] Sfnt(UInt32 flavor, List<Entry> entries, List<Byte[]> blocks)
    {
        var order = new Int32[entries.Count];

        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        // §2: the directory is sorted by tag, whatever order the tables were packed in.
        Array.Sort(order, (left, right) => String.CompareOrdinal(entries[left].Tag, entries[right].Tag));

        var shift = 0;
        var selector = 0;

        while ((1 << (selector + 1)) <= entries.Count)
        {
            selector++;
        }

        var search = (1 << selector) * 16;
        shift = entries.Count * 16 - search;

        var head = new List<Byte>(16 + entries.Count * 16);
        U32(head, flavor);
        U16(head, (UInt16)entries.Count);
        U16(head, (UInt16)search);
        U16(head, (UInt16)selector);
        U16(head, (UInt16)shift);

        var body = new List<Byte>();
        var offsets = new Int32[entries.Count];
        var sums = new UInt32[entries.Count];

        for (var i = 0; i < order.Length; i++)
        {
            var block = blocks[order[i]];
            offsets[order[i]] = 12 + entries.Count * 16 + body.Count;
            sums[order[i]] = Sum(block);

            Add(body, block);

            while (body.Count % 4 != 0)
            {
                body.Add(0);
            }
        }

        for (var i = 0; i < order.Length; i++)
        {
            var tag = entries[order[i]].Tag;

            foreach (var c in tag)
            {
                head.Add((Byte)c);
            }

            U32(head, sums[order[i]]);
            U32(head, (UInt32)offsets[order[i]]);
            U32(head, (UInt32)blocks[order[i]].Length);
        }

        var file = new List<Byte>(head.Count + body.Count);
        Add(file, head);
        Add(file, body);

        // Every table was rewritten, so the font's own checksum no longer holds: it is recomputed with
        // head's adjustment cleared and the difference written back, the way §5 requires.
        var where = -1;

        for (var i = 0; i < order.Length; i++)
        {
            if (entries[order[i]].Tag == "head")
            {
                where = offsets[order[i]];
            }
        }

        if (where >= 0 && where + 12 <= file.Count)
        {
            for (var i = 0; i < 4; i++)
            {
                file[where + 8 + i] = 0;
            }

            var whole = Sum(file.ToArray());
            var adjustment = unchecked(0xB1B0AFBA - whole);

            U32At(file, where + 8, adjustment);
        }

        return file.ToArray();
    }

    private static UInt32 Sum(Byte[] block)
    {
        var sum = 0u;
        var at = 0;

        for (; at + 4 <= block.Length; at += 4)
        {
            sum += U32(block, at);
        }

        if (at < block.Length)
        {
            var tail = 0u;

            for (var i = at; i < block.Length; i++)
            {
                tail = (tail << 8) | block[i];
            }

            for (var i = block.Length - at; i < 4; i++)
            {
                tail <<= 8;
            }

            sum += tail;
        }

        return sum;
    }

    private static Boolean Base128(Byte[] file, ref Int32 at, out UInt32 value)
    {
        value = 0;

        for (var i = 0; i < 5; i++)
        {
            if (at >= file.Length)
            {
                return false;
            }

            var next = file[at++];

            // §3.1: a leading zero is a sub-optimal encoding and the file is invalid.
            if (i == 0 && next == 0x80)
            {
                return false;
            }

            if ((value & 0xFE000000) != 0)
            {
                return false;
            }

            value = (value << 7) | (UInt32)(next & 0x7f);

            if ((next & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads a 255UInt16: one byte for a small value, more for the rest (§3.1).</summary>
    private static Boolean Value(Byte[] stream, ref Int32 at, out Int32 value)
    {
        value = 0;

        if (at >= stream.Length)
        {
            return false;
        }

        var code = stream[at++];

        if (code == 253)
        {
            if (at + 2 > stream.Length)
            {
                return false;
            }

            value = (stream[at] << 8) | stream[at + 1];
            at += 2;
            return true;
        }

        if (code == 255)
        {
            if (at >= stream.Length)
            {
                return false;
            }

            value = stream[at++] + 253;
            return true;
        }

        if (code == 254)
        {
            if (at >= stream.Length)
            {
                return false;
            }

            value = stream[at++] + 506;
            return true;
        }

        value = code;
        return true;
    }

    private static Byte[] Slice(Byte[] file, Int32 at, Int32 length)
    {
        var slice = new Byte[length];
        Array.Copy(file, at, slice, 0, length);
        return slice;
    }

    private static void Add(List<Byte> target, IReadOnlyList<Byte> source)
    {
        for (var i = 0; i < source.Count; i++)
        {
            target.Add(source[i]);
        }
    }

    private static void Add(List<Byte> target, Byte[] source, Int32 at, Int32 length)
    {
        for (var i = 0; i < length; i++)
        {
            target.Add(source[at + i]);
        }
    }

    private static UInt16 U16(Byte[] file, Int32 at) => (UInt16)((file[at] << 8) | file[at + 1]);

    private static UInt32 U32(Byte[] file, Int32 at) =>
        ((UInt32)file[at] << 24) | ((UInt32)file[at + 1] << 16) | ((UInt32)file[at + 2] << 8) | file[at + 3];

    private static void U16(List<Byte> target, Int32 value)
    {
        target.Add((Byte)(value >> 8));
        target.Add((Byte)value);
    }

    private static void I16(List<Byte> target, Int32 value) => U16(target, value & 0xffff);

    private static void U32(List<Byte> target, UInt32 value)
    {
        target.Add((Byte)(value >> 24));
        target.Add((Byte)(value >> 16));
        target.Add((Byte)(value >> 8));
        target.Add((Byte)value);
    }

    private static void U32At(List<Byte> target, Int32 at, UInt32 value)
    {
        target[at] = (Byte)(value >> 24);
        target[at + 1] = (Byte)(value >> 16);
        target[at + 2] = (Byte)(value >> 8);
        target[at + 3] = (Byte)value;
    }
}

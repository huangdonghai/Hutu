using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Hutu.Rendering.Media;

/// <summary>
/// Turns a WOFF 1.0 file back into the font file it was packed from.
/// </summary>
/// <remarks>
/// WOFF 1.0 is the simpler of the two packings: every table is compressed on its own with zlib, and
/// nothing is preprocessed. There is no glyph transform to undo and no table to reconstruct - the
/// work is entirely in the directory, which stores for each table where its bytes start, how long
/// they are compressed, and how long they were before.
/// <para>
/// A table whose two lengths agree was stored uncompressed, which a table that does not shrink is
/// usually better off being: zlib on a table it cannot compress only makes it longer.
/// </para>
/// </remarks>
internal static class Woff1Decoder
{
    private const UInt32 Signature = 0x774F4646; // 'wOFF'

    private const Int32 Header = 44;

    private const Int32 DirectoryEntry = 20;

    /// <summary>Gets the SFNT font the given WOFF 1.0 file holds, or null when it is not one.</summary>
    internal static Byte[]? ToSfnt(Byte[] file)
    {
        if (file.Length < Header || U32(file, 0) != Signature)
        {
            return null;
        }

        var flavor = U32(file, 4);
        var count = U16(file, 12);

        if (count == 0 || Header + count * DirectoryEntry > file.Length)
        {
            return null;
        }

        var tags = new String[count];
        var offsets = new UInt32[count];
        var packed = new UInt32[count];
        var plain = new UInt32[count];

        for (var i = 0; i < count; i++)
        {
            var at = Header + i * DirectoryEntry;
            tags[i] = $"{(Char)file[at]}{(Char)file[at + 1]}{(Char)file[at + 2]}{(Char)file[at + 3]}";
            offsets[i] = U32(file, at + 4);
            packed[i] = U32(file, at + 8);
            plain[i] = U32(file, at + 12);

            if (offsets[i] + packed[i] > (UInt32)file.Length)
            {
                return null;
            }
        }

        var blocks = new Byte[count][];

        for (var i = 0; i < count; i++)
        {
            var size = (Int32)packed[i];
            var block = new Byte[size];
            Array.Copy(file, (Int32)offsets[i], block, 0, size);

            if (packed[i] != plain[i])
            {
                var inflated = Inflate(block, (Int32)plain[i]);

                if (inflated is null)
                {
                    return null;
                }

                block = inflated;
            }

            blocks[i] = block;
        }

        return Sfnt(flavor, tags, blocks);
    }

    /// <summary>
    /// Inflates one table. The two bytes zlib puts in front of a stream are skipped: they are a header
    /// WOFF writes itself, and a raw deflate reader is what is left to read them with.
    /// </summary>
    private static Byte[]? Inflate(Byte[] block, Int32 expected)
    {
        if (block.Length < 2)
        {
            return null;
        }

        try
        {
            using var source = new MemoryStream(block, 2, block.Length - 2, false);
            using var stream = new DeflateStream(source, CompressionMode.Decompress);
            using var sink = new MemoryStream(expected);
            stream.CopyTo(sink);
            return sink.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the tables out as an SFNT file: a directory sorted by tag, the tables four-byte aligned
    /// behind it, and the checksum the directory asks for.
    /// </summary>
    private static Byte[] Sfnt(UInt32 flavor, String[] tags, Byte[][] blocks)
    {
        var order = new Int32[tags.Length];

        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (left, right) => String.CompareOrdinal(tags[left], tags[right]));

        var selector = 0;

        while ((1 << (selector + 1)) <= tags.Length)
        {
            selector++;
        }

        var search = (1 << selector) * 16;
        var shift = tags.Length * 16 - search;

        var head = new List<Byte>(12 + tags.Length * 16);
        U32(head, flavor);
        U16(head, (UInt16)tags.Length);
        U16(head, (UInt16)search);
        U16(head, (UInt16)selector);
        U16(head, (UInt16)shift);

        var body = new List<Byte>();
        var offsets = new Int32[tags.Length];

        for (var i = 0; i < order.Length; i++)
        {
            offsets[order[i]] = 12 + tags.Length * 16 + body.Count;
            Add(body, blocks[order[i]]);

            while (body.Count % 4 != 0)
            {
                body.Add(0);
            }
        }

        for (var i = 0; i < order.Length; i++)
        {
            foreach (var c in tags[order[i]])
            {
                head.Add((Byte)c);
            }

            U32(head, Sum(blocks[order[i]]));
            U32(head, (UInt32)offsets[order[i]]);
            U32(head, (UInt32)blocks[order[i]].Length);
        }

        var file = new List<Byte>(head.Count + body.Count);
        Add(file, head);
        Add(file, body);

        // The font's own checksum no longer holds once the tables have been through this, so it is
        // recomputed with head's adjustment cleared and the difference written back.
        for (var i = 0; i < order.Length; i++)
        {
            if (tags[order[i]] != "head")
            {
                continue;
            }

            var where = offsets[order[i]];

            if (where + 12 > file.Count)
            {
                break;
            }

            for (var b = 0; b < 4; b++)
            {
                file[where + 8 + b] = 0;
            }

            var adjustment = unchecked(0xB1B0AFBA - Sum(file.ToArray()));

            for (var b = 0; b < 4; b++)
            {
                file[where + 8 + b] = (Byte)(adjustment >> (24 - b * 8));
            }

            break;
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

    private static void Add(List<Byte> target, IReadOnlyList<Byte> source)
    {
        for (var i = 0; i < source.Count; i++)
        {
            target.Add(source[i]);
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

    private static void U32(List<Byte> target, UInt32 value)
    {
        target.Add((Byte)(value >> 24));
        target.Add((Byte)(value >> 16));
        target.Add((Byte)(value >> 8));
        target.Add((Byte)value);
    }
}

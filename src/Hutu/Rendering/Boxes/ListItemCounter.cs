using System;
using System.Collections.Generic;
using System.Text;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// Resolves the <c>list-item</c> counter a generated <c>content</c> value asks for.
/// </summary>
/// <remarks>
/// The number an item lands on is worked out by <see cref="ListMarkers"/> once the whole tree stands -
/// a reversed list settles its start from the items it holds, and a marker may read the lists around
/// it - so the content of a pseudo-element cannot answer it while its box is built. The value is
/// carried as a private token and filled in here, walking the tree after every list has counted:
/// the item the text stands in answers <c>counter(list-item)</c>, and the chain of items around it
/// answers <c>counters(list-item, ".")</c> (CSS Lists 3 §4.4, §5.2).
/// </remarks>
internal static class ListItemCounter
{
    private const Char Start = '\uE020';
    private const Char End = '\uE021';
    private const Char Field = '\uE022';
    private const String Counter = "counter";
    private const String Counters = "counters";

    /// <summary>Gets the token a <c>counter(list-item, style)</c> leaves for this pass.</summary>
    internal static String ForCounter(String? spelling) =>
        new StringBuilder().Append(Start).Append(Counter).Append(Field).Append(spelling ?? String.Empty).Append(End).ToString();

    /// <summary>Gets the token a <c>counters(list-item, separator, style)</c> leaves.</summary>
    internal static String ForCounters(String separator, String? spelling) =>
        new StringBuilder().Append(Start).Append(Counters).Append(Field).Append(separator).Append(Field)
            .Append(spelling ?? String.Empty).Append(End).ToString();

    /// <summary>Fills in every token the tree carries, each with the list items around it.</summary>
    internal static void Resolve(Box root, CounterStyleTable styles)
    {
        var chain = new List<Int32>();
        Walk(root, chain, styles);
    }

    private static void Walk(Box box, List<Int32> chain, CounterStyleTable styles)
    {
        var item = box.ListItemNumber;

        if (item is { } number)
        {
            chain.Add(number);
        }

        if (box.Kind == BoxKind.Text && box.Text is { Length: > 0 } text && text.IndexOf(Start) >= 0)
        {
            box.Text = Rewrite(text, chain, styles);
        }

        foreach (var child in box.Children)
        {
            Walk(child, chain, styles);
        }

        if (item is not null)
        {
            chain.RemoveAt(chain.Count - 1);
        }
    }

    private static String Rewrite(String text, List<Int32> chain, CounterStyleTable styles)
    {
        var drawn = new StringBuilder(text.Length);
        var at = 0;

        while (at < text.Length)
        {
            var start = text.IndexOf(Start, at);

            if (start < 0)
            {
                drawn.Append(text, at, text.Length - at);
                break;
            }

            drawn.Append(text, at, start - at);

            var end = text.IndexOf(End, start + 1);

            if (end < 0)
            {
                drawn.Append(text, start, text.Length - start);
                break;
            }

            var fields = text[(start + 1)..end].Split(Field);

            if (fields.Length >= 2 && fields[0] == Counter)
            {
                var number = chain.Count > 0 ? chain[^1] : 0;
                drawn.Append(GeneratedContent.Spelled(number, fields[1], styles));
            }
            else if (fields.Length >= 3 && fields[0] == Counters)
            {
                if (chain.Count == 0)
                {
                    drawn.Append(GeneratedContent.Spelled(0, fields[2], styles));
                }
                else
                {
                    for (var index = 0; index < chain.Count; index++)
                    {
                        if (index > 0)
                        {
                            drawn.Append(fields[1]);
                        }

                        drawn.Append(GeneratedContent.Spelled(chain[index], fields[2], styles));
                    }
                }
            }

            at = end + 1;
        }

        return drawn.ToString();
    }
}

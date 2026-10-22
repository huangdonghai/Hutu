using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Inserts the anonymous boxes the table algorithms require, so the formatting context can assume a
/// well-formed structure instead of defending against every nesting the parser allows.
/// </summary>
/// <remarks>
/// The table rules are unusual in that a well-formed table needs no markup at all: a <c>td</c> with no
/// <c>tr</c>, a <c>tr</c> with no row group and even bare text inside a <c>table</c> are all legal and
/// have to be completed with generated boxes. The HTML parser already does most of this, but the
/// fixup cannot rely on it: it also has to hold for the boxes built by hand in tests, and for the
/// <c>display: table</c> spellings applied to elements the parser never treats as table parts.
/// </remarks>
internal static class TableFixup
{
    /// <summary>Completes a table's children: captions, columns, row groups and rows, with any stray
    /// cell or content wrapped into an anonymous row.</summary>
    public static void NormalizeTable(Box table) => Normalize(table, isRow: false);

    /// <summary>Completes a row group's children: rows, with stray cells and content wrapped.</summary>
    public static void NormalizeRowGroup(Box group) => Normalize(group, isRow: false);

    /// <summary>Completes a row's children: cells, with stray content wrapped into one.</summary>
    public static void NormalizeRow(Box row) => Normalize(row, isRow: true);

    private static void Normalize(Box parent, Boolean isRow)
    {
        if (parent.Children.Count == 0)
        {
            return;
        }

        var normalized = new List<Box>(parent.Children.Count);
        var row = new List<Box>();
        var run = new List<Box>();

        void FlushRun()
        {
            if (run.Count == 0)
            {
                return;
            }

            // A run of content that is not a cell becomes one anonymous cell, not one per box: bare
            // text between two rows must occupy a single column, the way a browser places it.
            var cell = new Box(BoxKind.TableCell, parent.Style) { IsAnonymous = true };
            BoxBuilder.GroupChildren(cell, run);
            run.Clear();
            row.Add(cell);
        }

        void FlushRow()
        {
            FlushRun();

            if (row.Count == 0)
            {
                return;
            }

            if (isRow)
            {
                // Already inside a row: there is nowhere further to wrap, so the surrounding box keeps
                // the cells. This is the shape a caller gets when it asks to normalise a row directly.
                normalized.AddRange(row);
            }
            else
            {
                var anonymous = new Box(BoxKind.TableRow, parent.Style) { IsAnonymous = true };
                anonymous.Children.AddRange(row);
                normalized.Add(anonymous);
            }

            row.Clear();
        }

        foreach (var child in parent.Children)
        {
            switch (child.Kind)
            {
                case BoxKind.TableCell:
                    FlushRun();
                    row.Add(child);
                    break;

                case BoxKind.TableRow:
                    if (isRow)
                    {
                        // A row nested in a row is not a table part; the spec wraps it in a cell.
                        run.Add(child);
                        break;
                    }

                    FlushRow();
                    normalized.Add(child);
                    break;

                case BoxKind.TableRowGroup:
                    if (isRow)
                    {
                        run.Add(child);
                        break;
                    }

                    FlushRow();
                    normalized.Add(child);
                    break;

                case BoxKind.TableCaption:
                case BoxKind.TableColumn:
                case BoxKind.TableColumnGroup:
                    if (isRow)
                    {
                        run.Add(child);
                        break;
                    }

                    FlushRow();
                    normalized.Add(child);
                    break;

                default:
                    if (IsCollapsibleWhitespace(child))
                    {
                        break;
                    }

                    // A child that is not a table part is not row content either, when it is a
                    // container of its own: a flex container written for a row group is a flex
                    // container, and the table holds it rather than a row around it (CSS Display 3
                    // §2.7 - what the page wrote is the outer role, and a flex container is not a
                    // row). Anything else is content, and the anonymous boxes the tables need are
                    // made around it as CSS 2.1 §17.2.1 asks.
                    if (!isRow && child.Kind is BoxKind.Flex or BoxKind.Grid or BoxKind.Masonry)
                    {
                        FlushRow();
                        normalized.Add(child);
                        break;
                    }

                    run.Add(child);
                    break;
            }
        }

        FlushRow();

        parent.Children.Clear();
        parent.Children.AddRange(normalized);
    }

    /// <summary>
    /// Gets whether the box is a white-space-only text box. Text that only separates two table parts
    /// collapses away, exactly as it does between two blocks.
    /// </summary>
    private static Boolean IsCollapsibleWhitespace(Box box) =>
        box.Kind == BoxKind.Text && String.IsNullOrWhiteSpace(box.Text);
}

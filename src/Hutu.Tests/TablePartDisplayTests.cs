using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The outer and the inner role of a table part (CSS Display 3 §2.7).
/// </summary>
/// <remarks>
/// A table part has two roles, and the page may change only the outer one: a <c>tbody</c> is a row
/// group and a <c>tr</c> a row whatever the display says, and the display says what box the part is.
/// A part that is no longer in a table keeps the role for what its children are - the cells of a row
/// that is a flex container are cells among themselves - while the rows of a row group that is a flex
/// container are rows of nothing and come to blocks.
/// </remarks>
[TestFixture]
public sealed class TablePartDisplayTests
{
    [Test]
    public async Task ARowGroupWrittenAsAFlexContainerIsAFlexContainer()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='width:400px'><tbody style='display:flex'><tr><td>A</td><td>B</td></tr></tbody></table>" +
            "</body></html>");

        var group = Find(view, "tbody").Single();
        var row = Find(view, "tr").Single();

        Assert.Multiple(() =>
        {
            Assert.That(group.BorderBox.Width, Is.EqualTo(400).Within(0.5),
                "the flex container fills the table's content width");
            Assert.That(row.BorderBox.X, Is.EqualTo(0).Within(0.5),
                "the row is a flex item of the group and stands at its start");
        });
    }

    [Test]
    public async Task TheCellsOfARowThatIsNotInATableStandSideBySide()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='width:400px'><tbody style='display:flex'><tr><td>A</td><td>B</td></tr></tbody></table>" +
            "</body></html>");

        var cells = Find(view, "td")
            .Where(cell => cell.Kind is LayoutKind.TableCell)
            .OrderBy(cell => cell.BorderBox.X)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(cells.Count, Is.EqualTo(2));
            Assert.That(cells[1].BorderBox.X, Is.GreaterThanOrEqualTo(cells[0].BorderBox.Right),
                "the cells are side by side, as the cells of a table are: " +
                String.Join(", ", cells.Select(c => c.BorderBox.X + "x" + c.BorderBox.Width)));
        });
    }

    [Test]
    public async Task ACellWrittenAsARowIsARow()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='width:400px'><tbody><tr><td style='display:table-row'>x</td><td>y</td></tr></tbody></table>" +
            "</body></html>");

        var cell = Find(view, "td").First();

        Assert.That(cell.Kind, Is.EqualTo(LayoutKind.TableRow),
            "a cell written `table-row` is a row, and the table lays it out as one");
    }

    [Test]
    public async Task ATablePartThatKeepsItsDisplayKeepsItsRole()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table><thead><tr><td>H</td></tr></thead><tbody><tr><td>B</td></tr></tbody></table>" +
            "</body></html>");

        var head = Find(view, "thead").Single();

        Assert.That(head.Kind, Is.EqualTo(LayoutKind.TableRowGroup),
            "a header group is a row group, as the user agent sheet says");
    }

    private static HutuViewInternal CreateView(Int32 viewportWidth)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static IEnumerable<LayoutBox> Find(HutuViewInternal view, String tag)
    {
        var root = view.Layout?.Root;

        if (root is null)
        {
            yield break;
        }

        foreach (var node in Walk(root))
        {
            if (node.Source.Element is { } element &&
                String.Equals(element.LocalName, tag, StringComparison.OrdinalIgnoreCase))
            {
                yield return node;
            }
        }
    }

    private static IEnumerable<LayoutBox> Walk(LayoutBox node)
    {
        yield return node;

        foreach (var child in node.Children)
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }
}

using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering;

/// <summary>
/// Decides whether a rebuild can keep the geometry of the previous one.
/// </summary>
/// <remarks>
/// A state change - hovering a link, marking an address as visited - has to recompute the styles,
/// but the styles it changes usually only decide how a box is painted. When the freshly built box tree
/// has the same shape and the same layout inputs as the previous one, nothing can have moved, so the
/// previous tree is handed the new styles in place and the layout in hand stays valid: only the paint
/// list has to be made again. This is what keeps a pointer moving over a document from laying it out
/// on every step.
/// <para>
/// The comparison is deliberately conservative. A property is ignored only when the painter reads it
/// straight from the box style, so changing it cannot invalidate any geometry or any value the layout
/// cached on its boxes. A property that is not ignored and differs - even only because one side spells
/// out an initial value the other leaves implicit - simply means no reuse, which is slower but never
/// wrong.
/// </para>
/// </remarks>
internal static class LayoutReuse
{
    /// <summary>
    /// The longhands the painter reads from the box style at paint time, and that no other stage
    /// consults. They can differ without the layout having to run again.
    /// </summary>
    private static readonly HashSet<String> PaintOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "background", "background-color", "background-image", "background-position",
        "background-position-x", "background-position-y", "background-size", "background-repeat",
        "background-repeat-x", "background-repeat-y", "background-attachment", "background-origin",
        "background-clip", "background-blend-mode",
        "border-color", "border-top-color", "border-right-color", "border-bottom-color",
        "border-left-color",
        "color",
        "outline", "outline-color", "outline-style", "outline-width", "outline-offset",
        "text-decoration", "text-decoration-line", "text-decoration-color", "text-decoration-style",
        "text-decoration-thickness",
    };

    /// <summary>
    /// The colours a collapsed border resolves into a shared edge. Under <c>border-collapse: collapse</c>
    /// the winning colour is cached on the box rather than read from the style at paint time, so these
    /// must not be ignored once a collapsing table is in the tree.
    /// </summary>
    private static readonly HashSet<String> CollapsedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "color", "border-color", "border-top-color", "border-right-color", "border-bottom-color",
        "border-left-color",
    };

    /// <summary>
    /// Gives <paramref name="previous"/> the styles of <paramref name="current"/> when the two trees
    /// differ only in how they are painted, and reports whether that was possible. When it is, the
    /// caller can keep the previous layout and only repaint.
    /// </summary>
    public static Boolean TryRefresh(Box? previous, Box? current)
    {
        if (previous is null || current is null || !Equivalent(previous, current, Collapses(previous)))
        {
            return false;
        }

        Refresh(previous, current);
        return true;
    }

    private static Boolean Equivalent(Box a, Box b, Boolean collapsed)
    {
        if (a.Kind != b.Kind || a.IsAnonymous != b.IsAnonymous || a.IsReplaced != b.IsReplaced)
        {
            return false;
        }

        if (!String.Equals(a.Text, b.Text, StringComparison.Ordinal) ||
            !String.Equals(a.SourceUrl, b.SourceUrl, StringComparison.Ordinal) ||
            !ReferenceEquals(a.Element, b.Element) || !ReferenceEquals(a.Source, b.Source))
        {
            return false;
        }

        if (!a.Flow.Equals(b.Flow) || !Nullable.Equals(a.IntrinsicSize, b.IntrinsicSize) ||
            !SameStyle(a.Style, b.Style, collapsed))
        {
            return false;
        }

        if (a.Children.Count != b.Children.Count)
        {
            return false;
        }

        for (var index = 0; index < a.Children.Count; index++)
        {
            if (!Equivalent(a.Children[index], b.Children[index], collapsed))
            {
                return false;
            }
        }

        return true;
    }

    private static Boolean SameStyle(ComputedStyle a, ComputedStyle b, Boolean collapsed)
    {
        // The background box is resolved while laying out and then cached on the box, so it is the one
        // paint-only value the painter cannot read from the style: it has to match even though
        // `background-clip` itself is ignored below. The initial value it falls back to makes an
        // undeclared clip equal to an explicit `border-box`, which is exactly what a shorthand that
        // mentions a background tends to spell out.
        if (a.BackgroundClip != b.BackgroundClip)
        {
            return false;
        }

        return SameDeclaration(a.Declaration, b.Declaration, collapsed) &&
            SameDeclaration(a.Specified, b.Specified, collapsed);
    }

    private static Boolean SameDeclaration(ICssStyleDeclaration? a, ICssStyleDeclaration? b, Boolean collapsed)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        // Every name either side mentions is compared, so a property only one of them carries is seen
        // as the difference it is. A name the painter reads live is skipped; under a collapsing table
        // the shared-edge colours are not.
        foreach (var property in a)
        {
            var name = property.Name;

            if (!Ignored(name, collapsed) &&
                !String.Equals(a.GetPropertyValue(name), b.GetPropertyValue(name), StringComparison.Ordinal))
            {
                return false;
            }
        }

        foreach (var property in b)
        {
            var name = property.Name;

            if (!Ignored(name, collapsed) &&
                !String.Equals(a.GetPropertyValue(name), b.GetPropertyValue(name), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static Boolean Ignored(String name, Boolean collapsed) =>
        !(collapsed && CollapsedColors.Contains(name)) && PaintOnly.Contains(name);

    private static void Refresh(Box previous, Box current)
    {
        previous.Style = current.Style;

        for (var index = 0; index < previous.Children.Count; index++)
        {
            Refresh(previous.Children[index], current.Children[index]);
        }
    }

    /// <summary>Gets whether the tree holds a table whose borders are collapsed.</summary>
    private static Boolean Collapses(Box box)
    {
        if (box.Kind == BoxKind.Table && box.Style.Table.Collapse)
        {
            return true;
        }

        foreach (var child in box.Children)
        {
            if (Collapses(child))
            {
                return true;
            }
        }

        return false;
    }
}

using AngleSharp.Css.Dom;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using System.Globalization;
using System.Text;

namespace Hutu.Conformance;

/// <summary>
/// The layout tree and the paint list, written out as text.
/// </summary>
/// <remarks>
/// A picture shows that a page is wrong; this says which box, and what the engine thought it was.
/// That difference is why the dump exists beside the browser comparison rather than behind it: the
/// comparison needs a browser and a captured reference, and so only notices a regression on a page
/// the corpus has, while the golden needs neither and catches any change to any box the tests
/// render - including the ones a page's own script would have had to run before a browser could
/// show them.
/// <para>
/// The two halves are in one file on purpose. Layout answers where everything is and paint answers in
/// what order it is drawn; a change that moves a command from one side of a clip to the other is
/// invisible in the first half and obvious in the second, and reading them together is what makes
/// that class of mistake short to find.
/// </para>
/// <para>
/// Formatting is fixed: invariant numbers, no trailing space, document order, and a flag only when it
/// is not the default. A golden that reshuffles itself between runs is worse than none, because the
/// first thing a reviewer stops doing is reading it.
/// </para>
/// </remarks>
internal static class LayoutDumper
{
    /// <summary>
    /// How much text is quoted before it is cut, which is enough to tell one run from another. The
    /// marker is three dots rather than an ellipsis: a golden is a checked-in text file that editors
    /// and shells round-trip, and every non-ASCII byte in it is one codepage translation away from a
    /// diff that has nothing to do with the layout.
    /// </summary>
    private const Int32 TextRoom = 28;

    private const String Cut = "...";

    public static String Dump(LayoutResult layout, PaintList paint)
    {
        var text = new StringBuilder();

        text.Append("layout ").Append(Size(layout.Width, layout.Height)).AppendLine();

        Box(layout.Root, 0, text);

        text.AppendLine("--- paint ---");
        Commands(paint.Commands, "flow", text);
        Commands(paint.FixedCommands, "fixed", text);

        foreach (var layer in paint.Sticky)
        {
            text.AppendLine($"  sticky {Constraint(layer.Constraint)}");
            Commands(layer.Commands, "sticky", text);
        }

        text.Append("bounds ").Append(Size(paint.Bounds.Width, paint.Bounds.Height)).AppendLine();

        // The golden is a checked-in file, so it carries one line ending wherever it was written.
        return text.ToString().Replace("\r\n", "\n");
    }

    private static void Box(LayoutBox node, Int32 depth, StringBuilder text)
    {
        var element = node.Source.Element;
        var identity = Identity(node, element);
        var box = node.BorderBox;

        text.Append(new String(' ', depth * 2))
            .Append(node.Kind.ToString().ToLowerInvariant().PadRight(6))
            .Append(' ')
            .Append(identity.PadRight(24))
            .Append(Number(box.X)).Append(',').Append(Number(box.Y)).Append(' ')
            .Append(Size(box.Width, box.Height));

        Flags(node, text);
        Content(node, text);

        text.AppendLine();

        if (node.MarkerBounds is { } marker)
        {
            text.Append(new String(' ', depth * 2 + 2))
                .Append("      marker")
                .Append(Number(marker.X)).Append(',').Append(Number(marker.Y)).Append(' ')
                .Append(Size(marker.Width, marker.Height))
                .Append(" base ").Append(Number(node.MarkerBaseline))
                .AppendLine();
        }

        foreach (var child in node.Children)
        {
            Box(child, depth + 1, text);
        }
    }

    private static void Flags(LayoutBox node, StringBuilder text)
    {
        // Only what is out of the ordinary: a tree where every line repeats six defaults is a tree
        // nobody reads, and a golden that changes when a default is restated is a false alarm.
        if (node.Position != PositionMode.Static)
        {
            text.Append(" pos:").Append(node.Position.ToString().ToLowerInvariant());
        }

        if (node.ZIndex != 0)
        {
            text.Append(" z:").Append(Number(node.ZIndex));
        }

        if (node.ClipsOverflow)
        {
            text.Append(" clips");
        }

        if (node.Vertical)
        {
            text.Append(" vertical");
        }

        if (node.SlicesInlineStart || node.SlicesInlineEnd)
        {
            text.Append(node.SlicesInlineStart && node.SlicesInlineEnd
                ? " sliced:both"
                : node.SlicesInlineStart ? " sliced:start" : " sliced:end");
        }

        if (!node.Radii.IsZero)
        {
            text.Append(" radii:")
                .Append(Number(node.Radii.TopLeft.Horizontal)).Append('/')
                .Append(Number(node.Radii.BottomRight.Horizontal));
        }

        var margin = node.Margin;

        if (margin.Top != 0 || margin.Right != 0 || margin.Bottom != 0 || margin.Left != 0)
        {
            text.Append(" m:").Append(BoxEdges(margin));
        }

        if (node.Source.Style.Visibility == "hidden")
        {
            text.Append(" invisible");
        }
    }

    private static void Content(LayoutBox node, StringBuilder text)
    {
        if (node.Kind == LayoutKind.Text)
        {
            text.Append(" \"").Append(Quote(node.Text)).Append('"')
                .Append(" base ").Append(Number(node.Baseline))
                .Append(" font ").Append(Number(node.Font.Size)).Append('@').Append(node.Font.Family);
        }

        if (node.Kind == LayoutKind.Image)
        {
            text.Append(" img ").Append(Tail(node.ImageUrl));
        }
    }

    private static String Identity(LayoutBox node, AngleSharp.Dom.IElement? element)
    {
        if (element is null)
        {
            // An anonymous box has no name to be given, but which one it is matters: the block that
            // wraps text pushed out of an inline run and the line that holds a row of them are
            // different things that both have no element.
            return node.Kind == LayoutKind.Line ? "~line" : "~" + node.Source.Kind.ToString().ToLowerInvariant();
        }

        var name = element.LocalName;

        if (element.Id.Length > 0)
        {
            name += "#" + element.Id;
        }

        if (element.ClassList?.Length > 0)
        {
            name += "." + element.ClassList[0];
        }

        return name.Length > 24 ? name[..24] : name;
    }

    private static void Commands(IEnumerable<PaintCommand> commands, String band, StringBuilder text)
    {
        foreach (var command in commands)
        {
            text.Append("  ").Append(band.PadRight(6)).Append(' ')
                .Append(Name(command).PadRight(18))
                .Append(Number(command.Bounds.X)).Append(',').Append(Number(command.Bounds.Y)).Append(' ')
                .Append(Size(command.Bounds.Width, command.Bounds.Height));

            Detail(command, text);

            text.AppendLine();
        }
    }

    private static void Detail(PaintCommand command, StringBuilder text)
    {
        switch (command)
        {
            case FillCommand fill:
                text.Append(" #").Append(fill.Color.ToString("x8"));
                break;
            case BorderCommand border:
                text.Append(" w:").Append(BoxEdges(border.Widths))
                    .Append(" #").Append(border.TopColor.ToString("x8"));
                break;
            case TextCommand run:
                text.Append(" \"").Append(Quote(run.Text))
                    .Append("\" base ").Append(Number(run.Baseline))
                    .Append(" font ").Append(Number(run.Font.Size)).Append('@').Append(run.Font.Family)
                    .Append(" #").Append(run.Color.ToString("x8"));
                break;
            case ImageCommand image:
                text.Append(' ').Append(image.Fit.ToString().ToLowerInvariant()).Append(' ').Append(Tail(image.Url));
                break;
            case BackgroundImageCommand background:
                text.Append(' ').Append(Tail(background.Layer.Url));
                break;
            case OutlineCommand outline:
                text.Append(" ").Append(outline.Style.ToString().ToLowerInvariant())
                    .Append(' ').Append(Number(outline.Width)).Append(" off ").Append(Number(outline.Offset));
                break;
            case LinearGradientCommand gradient:
                text.Append(" ").Append(Number(gradient.AngleDegrees)).Append("deg ")
                    .Append(gradient.Stops.Count).Append(" stops");
                break;
        }
    }

    private static String Name(PaintCommand command) => command.GetType().Name switch
    {
        "FillCommand" => "fill",
        "BorderCommand" => "border",
        "OutlineCommand" => "outline",
        "TextCommand" => "text",
        "ImageCommand" => "image",
        "BackgroundImageCommand" => "background-image",
        "LinearGradientCommand" => "linear-gradient",
        "PushClipCommand" => "push-clip",
        "PopClipCommand" => "pop-clip",
        var other => other,
    };

    /// <summary>
    /// The rule one sticky band was pinned by: where the box stands with nothing scrolled, and which
    /// edge it is held to. Without it the band's commands are a list of boxes with no reason.
    /// </summary>
    private static String Constraint(StickyConstraint constraint) =>
        $"box {Number(constraint.Box.X)},{Number(constraint.Box.Y)} " +
        $"{Size(constraint.Box.Width, constraint.Box.Height)}" +
        $" top {Number(constraint.Top)} left {Number(constraint.Left)}";

    private static String BoxEdges(Thickness value) =>
        $"{Number(value.Top)}/{Number(value.Right)}/{Number(value.Bottom)}/{Number(value.Left)}";

    private static String Size(Double width, Double height) =>
        Number(width) + "x" + Number(height);

    private static String Number(Double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// The text of a run, quoted and escaped, cut to the length that tells one run from another.
    /// </summary>
    /// <remarks>
    /// Anything outside printable ASCII is written as an escape - which is how a list's bullet or a
    /// page's CJK survives in a checked-in file without a byte that an editor, a shell or a git
    /// checkout might translate and report as a layout change.
    /// </remarks>
    private static String Quote(String? value)
    {
        var text = value ?? String.Empty;

        if (text.Length > TextRoom)
        {
            text = text[..TextRoom] + Cut;
        }

        var quoted = new StringBuilder(text.Length + 2);

        foreach (var character in text)
        {
            if (character == '\n')
            {
                quoted.Append("\\n");
            }
            else if (character is '"' or '\\')
            {
                quoted.Append('\\').Append(character);
            }
            else if (character < 0x20 || character > 0x7E)
            {
                quoted.Append("\\u").Append(((UInt16)character).ToString("x4"));
            }
            else
            {
                quoted.Append(character);
            }
        }

        return quoted.ToString();
    }

    private static String Tail(String? url)
    {
        if (String.IsNullOrEmpty(url))
        {
            return "(none)";
        }

        var slash = url.LastIndexOf('/');

        return slash < 0 ? url : url[(slash + 1)..];
    }
}

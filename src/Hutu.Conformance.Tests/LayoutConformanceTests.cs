using Hutu.Reference;
using Hutu.Rendering.Layout;
using NUnit.Framework;
using System.Globalization;
using System.Text;

namespace Hutu.Conformance;

/// <summary>
/// Reads each case's page through the engine and compares it, box by box, with what a real browser
/// measured for the same page in the same conditions.
/// </summary>
/// <remarks>
/// This is the differential half of the harness: not an assertion about what the specification says,
/// which the unit tests cover, but an assertion about what an implementation that finished the job
/// produces. A case is a page, a handful of selectors, and a reference captured from a browser with
/// its scripts disarmed - so the two sides are reading the same document rather than one that its
/// own script had already rearranged.
/// <para>
/// The report is the point as much as the verdict is. A failure prints every box the case asked
/// about, both sides of it, so the question "what did this change break" is answered by the test
/// output rather than by a debugging session - and so a green run is a claim that can be read rather
/// than a number.
/// </para>
/// <para>
/// The engine viewport is the reference's <em>content</em> width. A browser's window is another
/// number, and reading both sides at the same figure is how a centred column comes out seven and a
/// half pixels apart on a page that merely has a scrollbar.
/// </para>
/// </remarks>
[TestFixture]
public sealed class LayoutConformanceTests
{
    [TestCaseSource(nameof(Cases))]
    public async Task MatchesTheReference(String directory)
    {
        var request = Corpus.Case(directory);
        var reference = ReferenceDocument.Load(Path.Combine(directory, Corpus.Reference));

        var width = reference.Environment.ClientWidth;

        Assert.That(width, Is.EqualTo(request.ViewportWidth),
            $"the reference was read {reference.Environment.ClientWidth} wide and the case asks for " +
            $"{request.ViewportWidth}; re-capture it with the collector so both agree");

        using var engine = await Corpus.RenderAsync(request, directory, withTypefaces: true);

        var tolerance = request.Tolerance;
        var report = new StringBuilder();
        var differences = 0;

        report.AppendLine($"case {Path.GetFileName(directory)}  " +
                          $"reference {reference.Environment.Channel} {reference.Environment.Browser}, " +
                          $"{width}x{reference.Environment.ScrollHeight}, scripts neutralized " +
                          $"{reference.Environment.ScriptsNeutralized}");

        if (request.KnownGap is { Length: > 0 } registered)
        {
            report.AppendLine($"  registered gap: {registered}");
        }

        report.AppendLine("                            engine            |          browser           |");
        report.AppendLine("  selector                    x       y       w       h   |       x       y       w       h   | verdict");

        // The document's own extent first: it is the number a reader notices, and a case where every
        // box agrees while the page is still twice as tall is a case about something outside the boxes.
        var measured = engine.DocumentHeight;
        var expected = (Double)reference.Environment.ScrollHeight;

        if (Math.Abs(measured - expected) > tolerance + 1)
        {
            differences++;
            report.AppendLine($"  document height {measured:0.0} where the browser measured {expected:0} " +
                              "- the page is another size even where the rows below agree");
        }
        else
        {
            report.AppendLine($"  document height {measured:0.0}, the browser's {expected:0}");
        }

        foreach (var wanted in reference.Elements)
        {
            var actual = engine.Read(wanted.Selector, wanted.Index, request.Styles);
            var label = wanted.Index == 0 ? wanted.Selector : $"{wanted.Selector}[{wanted.Index}]";

            var row = Row(label, wanted, actual, request, tolerance, out var differs);

            if (differs)
            {
                differences++;
            }

            report.AppendLine(row);
        }

        TestContext.Out.WriteLine(report);

        if (request.KnownGap is { Length: > 0 } gap)
        {
            // The case is registered as something the engine gets wrong. It still has to be wrong in
            // the way it was registered as being wrong by, so a fix that lands reports itself here and
            // the marker comes out - rather than the corpus quietly losing a test nobody removed.
            Assert.That(differences, Is.GreaterThan(0),
                $"this case was registered as a known gap ({gap}) and now agrees with the browser; " +
                "remove the marker so it starts guarding the behaviour");
            return;
        }

        Assert.That(differences, Is.Zero,
            $"{differences} of {reference.Elements.Count + 1} measurements differ from the reference by more than {tolerance}px");
    }

    /// <summary>
    /// One line of the report: both boxes, and what is wrong with the engine's if anything is.
    /// </summary>
    private static String Row(
        String label,
        ReferenceElement expected,
        EngineBox? actual,
        ReferenceCase request,
        Double tolerance,
        out Boolean differs)
    {
        differs = false;
        var notes = new List<String>();

        if (!expected.Found)
        {
            // A box the browser did not find is a box the page does not show. One the engine shows
            // anyway is the case's whole subject - a dialog left open, an option laid out as text.
            if (actual?.HasBox == true)
            {
                differs = true;
                notes.Add("the engine drew a box the browser has none for");
            }

            return Line(label, null, null) + "  (absent in the reference)" + Differs(differs);
        }

        if (actual is null || !actual.HasBox)
        {
            differs = true;
            notes.Add("no box");
            return Line(label, null, expected) + Join(notes);
        }

        var box = actual.Box!.Value;
        var wanted = expected;

        Check(notes, "x", box.X, wanted.X, tolerance);
        Check(notes, "y", box.Y, wanted.Y, tolerance);
        Check(notes, "width", box.Width, wanted.Width, tolerance);
        Check(notes, "height", box.Height, wanted.Height, tolerance);

        foreach (var name in request.Styles)
        {
            var here = actual.Styles.TryGetValue(name, out var value) ? value : "?";
            var there = expected.Styles.TryGetValue(name, out var other) ? other : "?";

            if (there.StartsWith("?", StringComparison.Ordinal) || here == there)
            {
                continue;
            }

            // A size the browser spells in pixels and the engine spells the same way is compared as
            // a number, so that a half-pixel of rounding in a computed value is not a difference.
            if (TryPixels(here, out var herePixels) && TryPixels(there, out var therePixels))
            {
                if (Math.Abs(herePixels - therePixels) > tolerance)
                {
                    notes.Add($"{name} {here} not {there}");
                }
            }
            else
            {
                notes.Add($"{name} {here} not {there}");
            }
        }

        // The verdict is the notes: a row that printed something wrong with a box and was not counted
        // is the one failure mode this harness must not have, because it reads as a passing page with
        // a column of complaints beside it.
        differs = notes.Count > 0;

        return Line(label, actual.Box, expected) + (notes.Count == 0 ? "  ok" : Join(notes));
    }

    private static void Check(List<String> notes, String name, Double here, Double there, Double tolerance)
    {
        if (Math.Abs(here - there) > tolerance)
        {
            notes.Add($"{name} {Number(here)} not {Number(there)}");
        }
    }

    private static String Line(String label, Rect? actual, ReferenceElement? expected)
    {
        var here = actual is { } box
            ? $"{Number(box.X),8}{Number(box.Y),8}{Number(box.Width),8}{Number(box.Height),8}"
            : $"{ "-",8}{ "-",8}{ "-",8}{ "-",8}";

        var there = expected is { Found: true } wanted
            ? $"{Number(wanted.X),8}{Number(wanted.Y),8}{Number(wanted.Width),8}{Number(wanted.Height),8}"
            : $"{ "-",8}{ "-",8}{ "-",8}{ "-",8}";

        return $"  {Pad(label)}{here} |{there} |";
    }

    private static String Join(List<String> notes) => "  " + String.Join("; ", notes);

    private static String Differs(Boolean differs) => differs ? "  ✗" : String.Empty;

    private static String Pad(String label) =>
        label.Length <= 26 ? label.PadRight(26) : label[..23] + "...";

    private static String Number(Double value) =>
        value.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(7);

    private static Boolean TryPixels(String text, out Double pixels)
    {
        pixels = 0;

        return text.EndsWith("px", StringComparison.Ordinal) &&
               Double.TryParse(text[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out pixels);
    }

    /// <summary>Every case that has been captured against a browser.</summary>
    private static IEnumerable<String> Cases() => Corpus.With(Corpus.Reference);
}

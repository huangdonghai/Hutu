using AngleSharp.Css.Dom;
using Hutu.Rendering.Layout;
using NUnit.Framework;
using System.Globalization;
using System.Text;

namespace Hutu.Conformance;

/// <summary>
/// The properties a layout has to have whatever the page says - checked against the corpus rather
/// than against a browser, because none of them needs an outside opinion of the right answer.
/// </summary>
/// <remarks>
/// A differential test notices when the engine disagrees with a browser. These notice when the engine
/// disagrees with itself: the same page built twice, a box that fits inside the box that sizes itself
/// to it, a page that does not grow when the window does. That class of failure is otherwise
/// invisible - a layout can be self-inconsistent and still produce a picture that looks like a page,
/// and it will not differ from a reference until the reference happens to cover the case.
/// <para>
/// Each invariant is stated as something that must hold for every box of every case, so a violation is
/// either a bug or an invariant that was too naive about CSS. Both are worth the run: the second kind
/// shows up here as a failing test with a box named, not as a silently weakened assertion.
/// </para>
/// </remarks>
[TestFixture]
public sealed class LayoutInvariantsTests
{
    /// <summary>How far apart two numbers have to be to be different, given the rounding both sides do.</summary>
    private const Double Epsilon = 0.5;

    private static readonly Int32[] Widths = [240, 320, 480, 640, 800, 1024, 1280];

    [TestCaseSource(nameof(Cases))]
    public async Task TheSamePageTwiceGivesTheSameAnswer(String directory)
    {
        var request = Corpus.Case(directory);

        using var first = await Corpus.RenderAsync(request, directory, withTypefaces: false);
        var were = LayoutDumper.Dump(first.Layout!, first.Paint!);

        using var second = await Corpus.RenderAsync(request, directory, withTypefaces: false);
        var are = LayoutDumper.Dump(second.Layout!, second.Paint!);

        Assert.Multiple(() =>
        {
            Assert.That(are, Is.EqualTo(were),
                "the same page built twice came out differently, which means something in the layout " +
                "depends on what was built before it");

            Assert.That(LayoutDumper.Dump(second.Layout!, second.Paint!), Is.EqualTo(are),
                "and a third time as well");
        });
    }

    [TestCaseSource(nameof(Cases))]
    public async Task NoBoxIsGivenAnImpossibleGeometry(String directory)
    {
        var layout = await LayoutOfAsync(directory);
        var broken = new List<String>();

        foreach (var node in Walk(layout.Root))
        {
            var box = node.BorderBox;

            Require(broken, node, "x", box.X);
            Require(broken, node, "y", box.Y);
            Require(broken, node, "width", box.Width, nonNegative: true);
            Require(broken, node, "height", box.Height, nonNegative: true);
            Require(broken, node, "baseline", node.Baseline);

            Edges(broken, node, "margin", node.Margin);
            Edges(broken, node, "border", node.Border);
            Edges(broken, node, "padding", node.Padding);

            if (node.Kind == LayoutKind.Text && node.Font.Size <= 0)
            {
                broken.Add($"{Label(node)} text is set at {Number(node.Font.Size)}");
            }
        }

        Assert.That(broken, Is.Empty, String.Join(Environment.NewLine, broken));
    }

    [TestCaseSource(nameof(Cases))]
    public async Task SiblingsInFlowKeepTheOrderTheDocumentWrote(String directory)
    {
        var layout = await LayoutOfAsync(directory);
        var broken = new List<String>();

        foreach (var parent in Walk(layout.Root))
        {
            LayoutBox? previous = null;

            foreach (var child in parent.Children.Where(InFlow))
            {
                // A block that runs along a line rather than down the page - a row of a horizontal
                // column - advances on the other axis, so the pair is asked about the axis the parent
                // stacks its children along.
                var axis = parent.Vertical ? 0 : 1;
                var after = Axis(child.BorderBox, axis);

                if (previous is not null)
                {
                    var before = Axis(previous.BorderBox, axis);

                    if (after + Epsilon < before)
                    {
                        broken.Add($"{Label(parent)} stacks {Label(child)} before {Label(previous)} " +
                                   $"({Number(after)} against {Number(before)})");
                    }
                }

                previous = child;
            }
        }

        Assert.That(broken, Is.Empty, String.Join(Environment.NewLine, broken));
    }

    [TestCaseSource(nameof(Cases))]
    public async Task ABlockThatSizedItselfContainsItsChildren(String directory)
    {
        var layout = await LayoutOfAsync(directory);
        var broken = new List<String>();

        foreach (var parent in Walk(layout.Root))
        {
            if (parent.Kind != LayoutKind.Block || !parent.Source.Style.Height.IsAuto)
            {
                continue;
            }

            foreach (var child in parent.Children.Where(InFlow))
            {
                // A relatively positioned box is laid out where it would have stood and moved after,
                // so the height its parent took is the height of the place it left - and a negative
                // bottom margin is a box asking its parent to stop short of it.
                if (child.Position != PositionMode.Static || child.Margin.Bottom < 0)
                {
                    continue;
                }

                var beyond = child.BorderBox.Bottom - parent.BorderBox.Bottom;

                if (beyond > Epsilon)
                {
                    broken.Add($"{Label(parent)} sized itself to {Number(parent.BorderBox.Height)} " +
                               $"and leaves {Label(child)} hanging {Number(beyond)} below its edge");
                }
            }
        }

        Assert.That(broken, Is.Empty, String.Join(Environment.NewLine, broken));
    }

    [TestCaseSource(nameof(Cases))]
    public async Task EveryTextRunCarriesTheFontSizeOfItsBox(String directory)
    {
        var layout = await LayoutOfAsync(directory);
        var broken = new List<String>();

        foreach (var node in Walk(layout.Root).Where(box => box.Kind == LayoutKind.Text))
        {
            if (Math.Abs(node.Font.Size - node.Source.Style.FontSize) <= Epsilon)
            {
                continue;
            }

            broken.Add($"{Label(node)} draws at {Number(node.Font.Size)} while its box computed " +
                       $"{Number(node.Source.Style.FontSize)} - the two paths disagree about the size");
        }

        Assert.That(broken, Is.Empty, String.Join(Environment.NewLine, broken));
    }

    [TestCaseSource(nameof(Cases))]
    public async Task WideningTheWindowNeverMakesThePageTaller(String directory)
    {
        var request = Corpus.Case(directory);
        var heights = new List<(Int32 Width, Double Height)>();

        foreach (var width in Widths)
        {
            using var engine = await Corpus.RenderAsync(request, directory, withTypefaces: false, width: width);

            heights.Add((width, engine.DocumentHeight));
        }

        var broken = new List<String>();

        for (var index = 1; index < heights.Count; index++)
        {
            var (narrower, taller) = heights[index - 1];
            var (wider, shorter) = heights[index];

            if (shorter > taller + Epsilon)
            {
                broken.Add($"{wider} wide came out {Number(shorter)} tall where {narrower} was {Number(taller)}");
            }
        }

        TestContext.Out.WriteLine("  " + String.Join("  ", heights.Select(h => $"{h.Width}:{Number(h.Height)}")));

        Assert.That(broken, Is.Empty, String.Join(Environment.NewLine, broken));
    }

    [TestCaseSource(nameof(Cases))]
    public async Task ZoomingThePageReflowsItLikeANarrowerWindow(String directory)
    {
        var request = Corpus.Case(directory);

        using var plain = await Corpus.RenderAsync(request, directory, withTypefaces: false);
        using var scaled = await Corpus.RenderAsync(request, directory, withTypefaces: false,
            width: request.ViewportWidth * 2, zoom: 2);

        // The engine's zoom is a reflow, not a magnification: the document is laid out at the window
        // divided by the zoom. So a window twice as wide at twice the zoom is the same layout, and the
        // day the two disagree is the day one of those two sentences stopped being true.
        Assert.That(scaled.DocumentHeight, Is.EqualTo(plain.DocumentHeight).Within(Epsilon),
            "zoom 2 at double the window is the same viewport as zoom 1, so the page has to be the same height");
        Assert.That(scaled.DocumentWidth, Is.EqualTo(plain.DocumentWidth).Within(Epsilon));
    }

    /// <summary>
    /// The guard over the guards. An invariant that every page satisfies because no page ever asks
    /// the question is worse than no invariant: it is green, and it is read as coverage.
    /// </summary>
    /// <remarks>
    /// This one caught two of the tests above passing on a corpus where every box had a fixed width,
    /// so the page was the same height at 240 as at 1280 and "a wider window never makes the page
    /// taller" was only ever comparing a number with itself.
    /// </remarks>
    [Test]
    public async Task TheCorpusActuallyReflows()
    {
        var responsive = new List<String>();

        foreach (var directory in Corpus.All())
        {
            var request = Corpus.Case(directory);
            var heights = new HashSet<Double>();

            foreach (var width in Widths)
            {
                using var engine = await Corpus.RenderAsync(request, directory, withTypefaces: false, width: width);
                heights.Add(Math.Round(engine.DocumentHeight, 2));
            }

            if (heights.Count > 1)
            {
                responsive.Add(Path.GetFileName(directory));
            }
        }

        Assert.That(responsive, Is.Not.Empty,
            "no case in the corpus changes height as the window does, so the tests that ask what " +
            "happens as the window changes are comparing a number with itself");
    }

    /// <summary>Every case directory, whether or not it has been captured against anything.</summary>
    private static IEnumerable<String> Cases() => Corpus.All();

    private static async Task<LayoutResult> LayoutOfAsync(String directory)
    {
        var request = Corpus.Case(directory);
        var engine = await Corpus.RenderAsync(request, directory, withTypefaces: false);

        return engine.Layout ?? throw new InvalidOperationException("the page laid out nothing");
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

    /// <summary>
    /// Gets whether a box took its place in flow from its parent, which is what makes the two
    /// orderings above meaningful at all.
    /// </summary>
    private static Boolean InFlow(LayoutBox node) =>
        !node.IsOutOfFlow &&
        node.Position == PositionMode.Static &&
        node.Kind is not (LayoutKind.Line or LayoutKind.Text);

    private static Double Axis(Rect box, Int32 axis) => axis == 0 ? box.X : box.Y;

    private static void Require(List<String> broken, LayoutBox node, String name, Double value, Boolean nonNegative = false)
    {
        if (Double.IsNaN(value) || Double.IsInfinity(value))
        {
            broken.Add($"{Label(node)} has a {name} of {value}");
        }
        else if (nonNegative && value < -Epsilon)
        {
            broken.Add($"{Label(node)} has a {name} of {Number(value)}");
        }
    }

    private static void Edges(List<String> broken, LayoutBox node, String name, Thickness edges)
    {
        Require(broken, node, $"{name} top", edges.Top);
        Require(broken, node, $"{name} right", edges.Right);
        Require(broken, node, $"{name} bottom", edges.Bottom);
        Require(broken, node, $"{name} left", edges.Left);
    }

    private static String Label(LayoutBox node)
    {
        var element = node.Source.Element;
        var name = element?.Id.Length > 0 ? element!.Id : element?.LocalName ?? node.Kind.ToString().ToLowerInvariant();

        return $"{node.Kind.ToString().ToLowerInvariant()} {name} at {Number(node.BorderBox.X)},{Number(node.BorderBox.Y)}";
    }

    private static String Number(Double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

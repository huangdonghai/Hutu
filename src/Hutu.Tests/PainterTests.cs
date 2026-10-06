using Hutu.Rendering;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The paint layer is asserted through the commands it emits, which is the contract the platform
/// backends execute.
/// </summary>
[TestFixture]
public sealed class PainterTests
{
    [Test]
    public async Task BackgroundColourBecomesAFill()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='margin:0;background-color:#ff0000'>hi</p>" +
            "</body></html>");

        var fills = view.Paint!.Commands.OfType<FillCommand>().ToList();

        Assert.That(fills, Has.Exactly(1).Matches<FillCommand>(fill => fill.Color == 0xFFFF0000));
    }

    [Test]
    public async Task BorderBecomesABorderCommandWithPerSideWidths()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:10px;border:3px solid #00ff00'></div>" +
            "</body></html>");

        var border = view.Paint!.Commands.OfType<BorderCommand>().Single();

        Assert.That(border.Widths.Top, Is.EqualTo(3).Within(0.01));
        Assert.That(border.Widths.Left, Is.EqualTo(3).Within(0.01));
        Assert.That(border.TopColor, Is.EqualTo(0xFF00FF00));
    }

    [Test]
    public async Task BackgroundFillCarriesTheResolvedRadii()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:100px;height:100px;background-color:#ff0000;border-radius:20px'></div>" +
            "</body></html>");

        var fill = view.Paint!.Commands.OfType<FillCommand>().Single(command => command.Color == 0xFFFF0000);

        Assert.That(fill.Radii.TopLeft.Horizontal, Is.EqualTo(20).Within(0.01), "the background follows the corner");
    }

    [Test]
    public async Task BorderCommandCarriesTheResolvedRadiiAndStrokeStyles()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:100px;height:100px;border:3px dashed #00ff00;border-radius:10px'></div>" +
            "</body></html>");

        var border = view.Paint!.Commands.OfType<BorderCommand>().Single();

        Assert.That(border.Radii.TopLeft.Horizontal, Is.EqualTo(10).Within(0.01), "the border follows the corner");
        Assert.That(border.Styles.Top, Is.EqualTo(BorderLineStyle.Dashed), "the stroke reaches the backend");
        Assert.That(border.Styles.IsUniform, Is.True);
    }

    [Test]
    public async Task TextBecomesATextCommandOnTheBaseline()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='margin:0'>hi</p>" +
            "</body></html>");

        var text = view.Paint!.Commands.OfType<TextCommand>().Single();

        Assert.That(text.Text, Is.EqualTo("hi"));
        Assert.That(text.Font.Size, Is.GreaterThan(0));
        Assert.That(text.Bounds.Y + text.Baseline, Is.GreaterThan(text.Bounds.Y), "the baseline sits inside the run");
    }

    [Test]
    public async Task HorizontalRulePaintsAVisibleBorder()
    {
        using var view = CreateView();
        await view.SetHtml("<html><body style='margin:0'><hr></body></html>");

        var border = view.Paint!.Commands.OfType<BorderCommand>().Single();

        Assert.That(border.Widths.Top, Is.GreaterThan(0));
        Assert.That((border.TopColor >> 24) & 0xFF, Is.GreaterThan(0), "an unresolved currentcolor border would paint transparent");
    }

    [Test]
    public async Task PreformattedNewlinesBreakLinesInsteadOfBeingDrawn()
    {
        using var view = CreateView();
        await view.SetHtml("<html><body style='margin:0'><pre style='margin:0'>a\nb</pre></body></html>");

        var runs = view.Paint!.Commands.OfType<TextCommand>().Select(command => command.Text).ToList();

        Assert.That(runs, Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public async Task PaintListReportsTheScrollableDocumentSize()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:123px'></div>" +
            "</body></html>");

        Assert.That(view.Paint!.Bounds.Height, Is.EqualTo(123).Within(0.5));
    }

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        return view;
    }
}

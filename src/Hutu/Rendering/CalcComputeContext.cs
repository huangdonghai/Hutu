using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;

namespace Hutu.Rendering;

/// <summary>
/// A minimal <see cref="ICssComputeContext"/> that supplies the render dimensions a
/// <c>calc()</c> expression needs to fold to pixels. The compute pipeline AngleSharp provides
/// resolves every length operand against the device, turns percentages into pixels and then
/// combines the operands - which is exactly what the fused path needs, now that the old
/// render tree no longer does it.
/// </summary>
/// <remarks>
/// Only the <see cref="Device"/> is consulted by the calc evaluator; the browsing context,
/// converter and variable resolver are unused because a <c>calc()</c> expression holds plain
/// arithmetic, not variable references. A variable that survived into a calc would have been
/// substituted earlier in the fused cascade.
/// </remarks>
internal sealed class CalcComputeContext : ICssComputeContext
{
    private readonly IRenderDimensions _dimensions;

    public CalcComputeContext(IRenderDimensions dimensions)
    {
        _dimensions = dimensions;
    }

    /// <summary>
    /// Gets the device. When the dimensions are already an <see cref="IRenderDevice"/> the
    /// same instance is reused; otherwise a <see cref="DefaultRenderDevice"/> is sized to
    /// match, which is enough for the pixel conversion the calc evaluator performs.
    /// </summary>
    public IRenderDevice Device => _dimensions as IRenderDevice
        ?? new DefaultRenderDevice
        {
            ViewPortWidth = (Int32)_dimensions.RenderWidth,
            ViewPortHeight = (Int32)_dimensions.RenderHeight,
            FontSize = _dimensions.FontSize,
        };

    public IBrowsingContext? Context => null;

    public IValueConverter? Converter => null;

    public ICssValue? Resolve(String name) => null;
}

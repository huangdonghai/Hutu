namespace Hutu.Rendering.Layout;

/// <summary>
/// How replaced content is fitted into its box. This affects drawing only: the box geometry is
/// resolved independently of it, which is exactly what the property is for.
/// </summary>
public enum ObjectFit
{
    /// <summary>The content is stretched to fill the box, distorting its aspect ratio.</summary>
    Fill,

    /// <summary>The content is scaled to fit inside the box, preserving its aspect ratio.</summary>
    Contain,

    /// <summary>The content is scaled to cover the box, preserving its aspect ratio and cropping.</summary>
    Cover,

    /// <summary>The content keeps its intrinsic size and is cropped by the box.</summary>
    None,

    /// <summary>Behaves like <see cref="None"/> unless the content is larger, then like <see cref="Contain"/>.</summary>
    ScaleDown,
}

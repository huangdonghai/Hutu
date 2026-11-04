namespace Hutu.Rendering.Layout;

/// <summary>
/// Gives the renderer the font table a text backend keeps its own caches under.
/// </summary>
/// <remarks>
/// The ids are sequential within a manager and are used as hashes, so styles measured and drawn
/// through one backend have to come from that backend's table. A backend that implements this
/// interface lends the same table to style resolution; a measurer without one gets a table of its
/// own for the view.
/// </remarks>
public interface IFontStyleManagerSource
{
    FontStyleManager FontStyles { get; }
}

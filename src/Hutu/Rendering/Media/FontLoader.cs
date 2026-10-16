using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hutu.Rendering.Media;

/// <summary>
/// One file an <c>@font-face</c> rule asks to be fetched, together with the family it belongs to.
/// </summary>
public readonly record struct FontRequest(String Family, String Url);

/// <summary>
/// Loads and decodes the fonts a document refers to, ahead of layout.
/// </summary>
/// <remarks>
/// Fonts are fetched for the same reason images are, and at the same point: layout has to measure
/// text, and measuring text needs the typeface, so a font that has not arrived yet is a font the page
/// is laid out without. What makes them unlike images is that one family may name several files -
/// CSS Fonts 3 §4.3 has the agent "skip downloading a font resource if the format hints indicate only
/// unsupported or unknown font formats" and take the first source it can activate - so the requests
/// arrive in the order the rule wrote them and a loader stops at the first one it can use.
/// <para>
/// Implementations must be resilient in the same way an image loader is: one URL that cannot be
/// fetched or decoded must degrade to "not available" without disturbing the others, because a single
/// broken font must not take the whole page down.
/// </para>
/// </remarks>
public interface IFontLoader
{
    /// <summary>
    /// Loads every family that is not loaded yet. Implementations should skip a family they already
    /// hold, so a relayout never re-downloads anything.
    /// </summary>
    Task LoadAsync(IReadOnlyList<FontRequest> faces, ResourceFetcher fetch, CancellationToken cancellationToken);
}

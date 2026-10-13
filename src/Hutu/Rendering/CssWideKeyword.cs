using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using System;

namespace Hutu.Rendering;

/// <summary>
/// A keyword that stands for a value rather than naming one: <c>inherit</c>, <c>initial</c>,
/// <c>unset</c> and <c>revert</c>.
/// </summary>
/// <remarks>
/// Every property may be given one of these, so they belong to the properties rather than to any one
/// of them - and AngleSharp's value model is per property: the converters built on its
/// <c>StandardValueConverter</c> answer three of the four, and every other converter, the box model's
/// among them, drops the declaration outright. A page that reverts a margin therefore reads as a page
/// that never mentioned that side at all, leaving whatever else it said about the side - the very
/// declaration the revert was written to undo.
/// <para>
/// The declaration factory below hands them over as this value instead, and it is for the reader that
/// meets one to say what it stands for: the parent's value, the property's own initial value, or, for
/// <c>revert</c>, the value the reader would have given had the page said nothing - which is the
/// user-agent level, and the reader is that level.
/// </para>
/// </remarks>
internal sealed class CssWideKeyword : ICssValue
{
    public CssWideKeyword(String text) => CssText = text.Trim();

    public String CssText { get; }

    public ICssValue Compute(ICssComputeContext context) => this;

    /// <summary>Gets whether a piece of text names one of these keywords.</summary>
    public static Boolean Is(String? text) => text?.Trim().ToLowerInvariant() is
        "inherit" or "initial" or "unset" or "revert" or "revert-layer";

    public Boolean Equals(ICssValue? other) =>
        other is CssWideKeyword keyword && keyword.CssText.Equals(CssText, StringComparison.OrdinalIgnoreCase);

    public override Boolean Equals(Object? obj) => Equals(obj as ICssValue);

    public override Int32 GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(CssText);

    public override String ToString() => CssText;
}

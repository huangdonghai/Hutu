using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using System;

namespace Hutu.Rendering;

/// <summary>
/// A value kept exactly as the page wrote it, for the properties whose grammar AngleSharp does not
/// carry - the vendor <c>-webkit-</c> ones among them.
/// </summary>
/// <remarks>
/// AngleSharp drops a declaration it has no grammar for, so a page's <c>-webkit-text-stroke</c> never
/// reaches the engine to be read at all. The declaration factory hands these names a converter that
/// keeps the whole value as this instead, and it is for the reader that meets one to make sense of the
/// text: the length, colour or keyword written there, read the way the property means it. A wide keyword
/// written in front - <c>inherit</c> above all, since the text stroke and fill are inherited - is still
/// taken as a <see cref="CssWideKeyword"/>, so an <c>inherit</c> on one of them keeps its usual meaning.
/// </remarks>
internal sealed class CssRawValue : ICssValue
{
    public CssRawValue(String text) => CssText = text.Trim();

    public String CssText { get; }

    public ICssValue Compute(ICssComputeContext context) => this;

    public Boolean Equals(ICssValue? other) =>
        other is CssRawValue raw && raw.CssText.Equals(CssText, StringComparison.OrdinalIgnoreCase);

    public override Boolean Equals(Object? obj) => Equals(obj as ICssValue);

    public override Int32 GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(CssText);

    public override String ToString() => CssText;
}

using AngleSharp.Css;
using AngleSharp.Css.Dom;
using Hutu.Rendering.Layout;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering;

/// <summary>
/// The <c>@page</c> rules the sheets carry for a device, and the page box their declarations come to
/// (CSS Page 3 §3, §6).
/// </summary>
/// <remarks>
/// The rules are collected while the rule index is built, so a rule inside an <c>@media</c> or
/// <c>@supports</c> block that does not answer to the device never arrives here. This slice reads the
/// rules that name no page; a named page or a page selector needs a page sequence to be answered
/// against, which is the next slice. Among the rules it does read, importance and then source order
/// settle a value; the layer order of CSS Cascade 5 is not consulted yet, so a layered <c>@page</c>
/// and an unlayered one are still answered by where they stand. A <c>var()</c> in a page descriptor
/// is not substituted yet either: the page context has no variable scope of its own.
/// <para>
/// Page margins are resolved the way CSS Page 3 §6.2 says: a percentage on the top or bottom is a
/// share of the page box's height, while one on the left or right is a share of its width. That is
/// deliberately unlike a box's own margins, whose percentages all read the containing block's width.
/// </para>
/// </remarks>
internal sealed class PageStyleTable
{
    private static readonly Dictionary<String, PageSize> NamedSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a5"] = Millimetres(148, 210),
        ["a4"] = Millimetres(210, 297),
        ["a3"] = Millimetres(297, 420),
        ["b5"] = Millimetres(176, 250),
        ["b4"] = Millimetres(250, 353),
        ["jis-b5"] = Millimetres(182, 257),
        ["jis-b4"] = Millimetres(257, 364),
        ["letter"] = new PageSize(8.5 * 96, 11 * 96),
        ["legal"] = new PageSize(8.5 * 96, 14 * 96),
        ["ledger"] = new PageSize(11 * 96, 17 * 96),
    };

    private readonly IReadOnlyList<ICssPageRule> _rules;
    private readonly IRenderDimensions _dimensions;

    public PageStyleTable(IReadOnlyList<ICssPageRule> rules, IRenderDimensions dimensions)
    {
        _rules = rules;
        _dimensions = dimensions;
    }

    /// <summary>
    /// Gets the page box the default page comes to: the size the last applicable <c>size</c> settles,
    /// and the margins the declarations leave. A page whose rule states no size is Letter, the size
    /// the user agent produces when the document says nothing (see <see cref="PageSize.Default"/>).
    /// </summary>
    /// <param name="inheritedMargins">
    /// The margins a page context that says <c>inherit</c> takes (CSS Page 3 §6.2: the page context
    /// inherits from the root element). The values keep their units, because a percentage inherited
    /// from the root resolves against the page box and not against the root's own containing block.
    /// </param>
    public PageGeometry DefaultPage(StyleThickness inheritedMargins) => Page(null, inheritedMargins);

    /// <summary>
    /// Gets the page box a page named <paramref name="name"/> comes to. A named rule is more specific
    /// than an unnamed one and answers after it; a rule whose selector also carries a page
    /// pseudo-class is left for the selectors that read one.
    /// </summary>
    public PageGeometry Page(String? name, StyleThickness inheritedMargins)
    {
        var rules = Applicable(name);
        var size = PageSize.Default;

        foreach (var rule in rules)
        {
            if (TryReadSize(rule, out var stated))
            {
                size = stated;
            }
        }

        var top = default(SideSlot);
        var right = default(SideSlot);
        var bottom = default(SideSlot);
        var left = default(SideSlot);

        foreach (var rule in rules)
        {
            ReadMargins(rule.Style, ref top, ref right, ref bottom, ref left);
        }

        var margins = new Thickness(
            Resolve(top, size.Height, inheritedMargins.Top),
            Resolve(right, size.Width, inheritedMargins.Right),
            Resolve(bottom, size.Height, inheritedMargins.Bottom),
            Resolve(left, size.Width, inheritedMargins.Left));

        // CSS 2.1 §13.2 lets a page rule size the page area itself with width and height, which is
        // what a page box whose content is over-constrained comes to: the area is the width stated and
        // the box grows to hold it and its margins. A percentage on either is a share of the size the
        // `size` property stated, which is the box before the area is forced.
        var areaWidth = ReadAreaLength("width", size.Width, rules);
        var areaHeight = ReadAreaLength("height", size.Height, rules);

        var box = new PageSize(
            areaWidth is { } width ? Math.Max(0, width) + margins.Horizontal : size.Width,
            areaHeight is { } height ? Math.Max(0, height) + margins.Vertical : size.Height);

        return new PageGeometry(box, margins);
    }

    /// <summary>
    /// Gets the rules that answer a page of this name: the unnamed rules, then the rules that name it,
    /// because a named selector is the more specific one. A rule carrying a page pseudo-class is not
    /// read yet, so it is left out rather than applied to every page of the name.
    /// </summary>
    private List<ICssPageRule> Applicable(String? name)
    {
        var rules = new List<ICssPageRule>();

        foreach (var rule in _rules)
        {
            if (SelectorOf(rule).IsDefault)
            {
                rules.Add(rule);
            }
        }

        if (name is null)
        {
            return rules;
        }

        foreach (var rule in _rules)
        {
            var selector = SelectorOf(rule);

            if (!selector.HasPseudo && String.Equals(selector.Name, name, StringComparison.Ordinal))
            {
                rules.Add(rule);
            }
        }

        return rules;
    }

    private static SelectorInfo SelectorOf(ICssPageRule rule)
    {
        var text = rule.SelectorText?.Trim();

        if (String.IsNullOrEmpty(text))
        {
            return default;
        }

        var colon = text.IndexOf(':');
        var name = (colon >= 0 ? text[..colon] : text).Trim();

        return new SelectorInfo(name.Length == 0 ? null : name, colon >= 0);
    }

    private readonly record struct SelectorInfo(String? Name, Boolean HasPseudo)
    {
        public Boolean IsDefault => Name is null && !HasPseudo;
    }

    /// <summary>
    /// Reads the page area size a rule forces with <c>width</c> or <c>height</c>, or null where none
    /// does. A percentage is read against the page box the <c>size</c> property settled.
    /// </summary>
    private Double? ReadAreaLength(String name, Double basis, IReadOnlyList<ICssPageRule> rules)
    {
        StyleLength? value = null;
        var important = false;

        foreach (var rule in rules)
        {
            if (rule.Style.TryGetProperty(name) is not { } property)
            {
                continue;
            }

            var text = property.Value?.Trim();

            if (String.IsNullOrEmpty(text) ||
                String.Equals(text, "auto", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!LengthReader.TryReadProperty(
                    property, RenderMode.Horizontal, _dimensions,
                    new LengthReader.FontSource(_dimensions.FontSize), out var length) ||
                !length.HasValue)
            {
                continue;
            }

            if (value is not null && important && !property.IsImportant)
            {
                continue;
            }

            value = length;
            important = property.IsImportant;
        }

        return value?.Resolve(basis);
    }

    private Boolean TryReadSize(ICssPageRule rule, out PageSize size)
    {
        size = default;

        var text = rule.Style.TryGetProperty("size")?.Value?.Trim();

        if (String.IsNullOrEmpty(text))
        {
            return false;
        }

        var tokens = CssValueList.SplitWhitespace(text);

        if (tokens.Count == 0)
        {
            return false;
        }

        var lengths = new List<Double>(2);
        PageSize? named = null;
        Boolean? landscape = null;
        var auto = false;

        foreach (var token in tokens)
        {
            var word = token.Trim();

            if (word.Length == 0)
            {
                return false;
            }

            if (String.Equals(word, "auto", StringComparison.OrdinalIgnoreCase))
            {
                auto = true;
            }
            else if (String.Equals(word, "landscape", StringComparison.OrdinalIgnoreCase))
            {
                if (landscape is not null)
                {
                    return false;
                }

                landscape = true;
            }
            else if (String.Equals(word, "portrait", StringComparison.OrdinalIgnoreCase))
            {
                if (landscape is not null)
                {
                    return false;
                }

                landscape = false;
            }
            else if (NamedSizes.TryGetValue(word, out var known))
            {
                if (named is not null)
                {
                    return false;
                }

                named = known;
            }
            else if (TryReadPageLength(word, out var length))
            {
                lengths.Add(length);
            }
            else
            {
                return false;
            }
        }

        if (lengths.Count > 2 ||
            (lengths.Count > 0 && (auto || named is not null || landscape is not null)) ||
            (auto && (named is not null || landscape is not null)))
        {
            return false;
        }

        if (lengths.Count == 2)
        {
            size = new PageSize(lengths[0], lengths[1]);
        }
        else if (lengths.Count == 1)
        {
            size = new PageSize(lengths[0], lengths[0]);
        }
        else if (named is { } page)
        {
            size = page;
        }
        else if (auto || landscape is not null)
        {
            size = PageSize.Default;
        }
        else
        {
            return false;
        }

        if (landscape is { } orientation)
        {
            size = size.Oriented(orientation);
        }

        return size.Width > 0 && size.Height > 0;
    }

    private Boolean TryReadPageLength(String token, out Double pixels)
    {
        pixels = 0;

        var length = LengthReader.FromText(
            token, RenderMode.Horizontal, _dimensions, new LengthReader.FontSource(_dimensions.FontSize));

        // A page size cannot be a share of the page box: the box does not exist until the size does.
        if (length is not { } value || !value.HasValue || value.ReadsContainingBlock)
        {
            return false;
        }

        pixels = value.Resolve(0);
        return pixels > 0;
    }

    private void ReadMargins(
        ICssStyleDeclaration style,
        ref SideSlot top,
        ref SideSlot right,
        ref SideSlot bottom,
        ref SideSlot left)
    {
        var stated = false;

        stated |= TryApply(style.TryGetProperty("margin-top"), ref top);
        stated |= TryApply(style.TryGetProperty("margin-right"), ref right);
        stated |= TryApply(style.TryGetProperty("margin-bottom"), ref bottom);
        stated |= TryApply(style.TryGetProperty("margin-left"), ref left);

        if (stated)
        {
            return;
        }

        // AngleSharp expands a margin shorthand into longhands, which is where the reads above get
        // their values. A declaration it leaves whole - `margin: inherit` - falls back to the
        // shorthand text, so the wide keyword reaches the page context the same way a length does.
        if (style.GetProperty("margin") is not { } shorthand ||
            String.IsNullOrWhiteSpace(shorthand.Value))
        {
            return;
        }

        var tokens = CssValueList.SplitWhitespace(shorthand.Value);
        var sides = new SideSpec[4];

        if (tokens.Count is < 1 or > 4)
        {
            return;
        }

        if (tokens.Count == 1 && WideSpec(tokens[0]) is { } wide)
        {
            sides[0] = sides[1] = sides[2] = sides[3] = wide;
        }
        else
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                if (!TryReadSideText(tokens[i], out sides[i]))
                {
                    return;
                }
            }
        }

        switch (tokens.Count)
        {
            case 1:
                sides[1] = sides[2] = sides[3] = sides[0];
                break;
            case 2:
                sides[2] = sides[0];
                sides[3] = sides[1];
                break;
            case 3:
                sides[3] = sides[1];
                break;
        }

        Apply(ref top, sides[0], shorthand.IsImportant);
        Apply(ref right, sides[1], shorthand.IsImportant);
        Apply(ref bottom, sides[2], shorthand.IsImportant);
        Apply(ref left, sides[3], shorthand.IsImportant);
    }

    private Boolean TryApply(ICssProperty? property, ref SideSlot slot)
    {
        if (property is null)
        {
            return false;
        }

        if (TryReadSide(property, out var spec))
        {
            Apply(ref slot, spec, property.IsImportant);
        }

        return true;
    }

    private Boolean TryReadSide(ICssProperty property, out SideSpec spec)
    {
        var text = property.Value?.Trim();

        if (String.IsNullOrEmpty(text))
        {
            spec = default;
            return false;
        }

        if (WideSpec(text) is { } wide)
        {
            spec = wide;
            return true;
        }

        var length = LengthReader.FromText(
            text, RenderMode.Horizontal, _dimensions, new LengthReader.FontSource(_dimensions.FontSize));

        if (length is { IsAuto: true })
        {
            spec = SideSpec.Of(StyleLength.Zero);
            return true;
        }

        if (length is not { HasValue: true })
        {
            spec = default;
            return false;
        }

        spec = SideSpec.Of(length.Value);
        return true;
    }

    private Boolean TryReadSideText(String token, out SideSpec spec)
    {
        var text = token.Trim();

        if (text.Length == 0 || WideSpec(text) is not null)
        {
            spec = default;
            return false;
        }

        var length = LengthReader.FromText(
            text, RenderMode.Horizontal, _dimensions, new LengthReader.FontSource(_dimensions.FontSize));

        if (length is { IsAuto: true })
        {
            spec = SideSpec.Of(StyleLength.Zero);
            return true;
        }

        if (length is not { HasValue: true })
        {
            spec = default;
            return false;
        }

        spec = SideSpec.Of(length.Value);
        return true;
    }

    /// <summary>
    /// Reads a wide keyword as what a page margin comes to: <c>inherit</c> takes the root's margin,
    /// and every other wide keyword resets to the initial zero.
    /// </summary>
    private static SideSpec? WideSpec(String text)
    {
        var word = text.Trim();

        if (String.Equals(word, "inherit", StringComparison.OrdinalIgnoreCase))
        {
            return SideSpec.Inherit;
        }

        if (String.Equals(word, "initial", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(word, "unset", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(word, "revert", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(word, "revert-layer", StringComparison.OrdinalIgnoreCase))
        {
            return SideSpec.Of(StyleLength.Zero);
        }

        return null;
    }

    private static void Apply(ref SideSlot slot, SideSpec spec, Boolean important)
    {
        if (slot.Set && slot.Important && !important)
        {
            return;
        }

        slot = new SideSlot(spec, important);
    }

    private static Double Resolve(SideSlot slot, Double basis, StyleLength inherited) =>
        !slot.Set
            ? 0
            : slot.Value.Inherits
                ? inherited.Resolve(basis)
                : slot.Value.Length.Resolve(basis);

    private static PageSize Millimetres(Double width, Double height) =>
        new(width * 96.0 / 25.4, height * 96.0 / 25.4);

    private readonly struct SideSpec
    {
        private SideSpec(Boolean inherit, StyleLength length)
        {
            Inherits = inherit;
            Length = length;
        }

        public static SideSpec Inherit { get; } = new(true, default);

        public Boolean Inherits { get; }

        public StyleLength Length { get; }

        public static SideSpec Of(StyleLength length) => new(false, length);
    }

    private readonly struct SideSlot
    {
        public SideSlot(SideSpec value, Boolean important)
        {
            Value = value;
            Important = important;
            Set = true;
        }

        public SideSpec Value { get; }

        public Boolean Important { get; }

        public Boolean Set { get; }
    }
}

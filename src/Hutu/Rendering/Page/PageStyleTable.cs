using AngleSharp.Css;
using AngleSharp.Css.Dom;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using System;
using System.Collections.Generic;
using System.Linq;

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
    private readonly PageSize _defaultSize;
    private readonly Thickness _defaultMargins;

    public PageStyleTable(
        IReadOnlyList<ICssPageRule> rules,
        IRenderDimensions dimensions,
        PageSize defaultSize,
        Thickness defaultMargins)
    {
        _rules = rules;
        _dimensions = dimensions;
        _defaultSize = defaultSize;
        _defaultMargins = defaultMargins;
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
    public PageGeometry DefaultPage(StyleThickness inheritedMargins) =>
        Page(null, 1, false, false, inheritedMargins);

    /// <summary>
    /// Gets the page box a page named <paramref name="name"/> comes to. A named rule is more specific
    /// than an unnamed one, and a page pseudo-class adds to that; a rule that does not answer the page
    /// a page is on is left out (CSS Page 3 §7.3).
    /// </summary>
    public PageGeometry Page(String? name, StyleThickness inheritedMargins) =>
        Page(name, 1, false, false, inheritedMargins);

    public PageGeometry Page(
        String? name,
        Int32 number,
        Boolean isLeft,
        Boolean blank,
        StyleThickness inheritedMargins)
    {
        var rules = Applicable(name, number, isLeft, blank);
        var size = _defaultSize;

        foreach (var rule in rules)
        {
            if (TryReadSize(rule, out var stated))
            {
                size = stated;
            }
        }

        var top = SideSlot.FromPixels(_defaultMargins.Top);
        var right = SideSlot.FromPixels(_defaultMargins.Right);
        var bottom = SideSlot.FromPixels(_defaultMargins.Bottom);
        var left = SideSlot.FromPixels(_defaultMargins.Left);

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

        return new PageGeometry(box, margins, ReadFrame(rules), ReadOrientation(rules));
    }

    /// <summary>Reads the quarter turn a page rule asks for, if any.</summary>
    private static PageOrientation ReadOrientation(IReadOnlyList<ICssPageRule> rules)
    {
        var orientation = PageOrientation.None;

        foreach (var rule in rules)
        {
            var text = rule.Style.TryGetProperty("page-orientation")?.Value?.Trim();

            if (String.Equals(text, "rotate-left", StringComparison.OrdinalIgnoreCase))
            {
                orientation = PageOrientation.RotateLeft;
            }
            else if (String.Equals(text, "rotate-right", StringComparison.OrdinalIgnoreCase))
            {
                orientation = PageOrientation.RotateRight;
            }
            else if (String.Equals(text, "initial", StringComparison.OrdinalIgnoreCase) ||
                     String.Equals(text, "unset", StringComparison.OrdinalIgnoreCase) ||
                     String.Equals(text, "revert", StringComparison.OrdinalIgnoreCase))
            {
                orientation = PageOrientation.None;
            }
        }

        return orientation;
    }

    /// <summary>
    /// Reads what the page box itself wears. Only the colour of a background is read - the layers
    /// and images belong to the page box painter, which is the next slice - and a border is drawn
    /// only where its style asks for a line and its colour is not fully transparent.
    /// </summary>
    private PageFrame ReadFrame(IReadOnlyList<ICssPageRule> rules)
    {
        var chosen = new Dictionary<String, (ICssProperty Property, Boolean Important)>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            foreach (var name in FrameProperties)
            {
                if (rule.Style.TryGetProperty(name) is not { } property)
                {
                    continue;
                }

                if (chosen.TryGetValue(name, out var held) && held.Important && !property.IsImportant)
                {
                    continue;
                }

                chosen[name] = (property, property.IsImportant);
            }
        }

        var hidden = chosen.TryGetValue("visibility", out var visibility) &&
            IsOneOf(visibility.Property.Value, "hidden", "collapse");

        UInt32? background = chosen.TryGetValue("background-color", out var colour)
            ? BackgroundLayerParser.ParseColor(colour.Property.Value?.Trim() ?? String.Empty)
            : null;

        if (background is { } argb && (argb >> 24) == 0)
        {
            background = null;
        }

        var border = new PageBorder(
            BorderSide(chosen, "top"),
            BorderSide(chosen, "right"),
            BorderSide(chosen, "bottom"),
            BorderSide(chosen, "left"));

        return new PageFrame(background, border, hidden);
    }

    private PageBorderSide BorderSide(
        Dictionary<String, (ICssProperty Property, Boolean Important)> chosen,
        String side)
    {
        if (!chosen.TryGetValue("border-" + side + "-style", out var style) ||
            IsOneOf(style.Property.Value, "none", "hidden", ""))
        {
            return PageBorderSide.None;
        }

        var width = 0.0;

        if (chosen.TryGetValue("border-" + side + "-width", out var stated))
        {
            var text = stated.Property.Value?.Trim();

            if (String.Equals(text, "thin", StringComparison.OrdinalIgnoreCase))
            {
                width = 1;
            }
            else if (String.Equals(text, "medium", StringComparison.OrdinalIgnoreCase))
            {
                width = 3;
            }
            else if (String.Equals(text, "thick", StringComparison.OrdinalIgnoreCase))
            {
                width = 5;
            }
            else if (LengthReader.TryReadProperty(
                    stated.Property, RenderMode.Horizontal, _dimensions,
                    new LengthReader.FontSource(_dimensions.FontSize), out var length) &&
                length.HasValue)
            {
                width = length.Resolve(0);
            }
        }

        if (width <= 0)
        {
            return PageBorderSide.None;
        }

        var colour = chosen.TryGetValue("border-" + side + "-color", out var statedColour)
            ? BackgroundLayerParser.ParseColor(statedColour.Property.Value?.Trim() ?? String.Empty)
            : null;

        colour ??= 0xFF000000;

        // A transparent side draws nothing, and an invisible side is no side at all.
        if ((colour.Value >> 24) == 0)
        {
            return PageBorderSide.None;
        }

        return new PageBorderSide(width, colour.Value, true);
    }

    private static Boolean IsOneOf(String? value, params String[] names)
    {
        var text = value?.Trim();

        foreach (var name in names)
        {
            if (String.Equals(text, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly String[] FrameProperties =
    {
        "background-color", "visibility",
        "border-top-width", "border-top-style", "border-top-color",
        "border-right-width", "border-right-style", "border-right-color",
        "border-bottom-width", "border-bottom-style", "border-bottom-color",
        "border-left-width", "border-left-style", "border-left-color",
    };

    /// <summary>
    /// Gets the rules that answer a page: every rule whose page name equals the page's (or names no
    /// page) and whose pseudo-classes all hold, ordered from the least specific to the most. A later
    /// rule wins among equals, which is what makes the last matching declaration the used one.
    /// </summary>
    private List<ICssPageRule> Applicable(String? name, Int32 number, Boolean isLeft, Boolean blank)
    {
        var candidates = new List<(ICssPageRule Rule, Int32 Specificity, Int32 Order)>(_rules.Count);

        for (var index = 0; index < _rules.Count; index++)
        {
            var selector = SelectorOf(_rules[index]);

            if (selector.Matches(name, number, isLeft, blank))
            {
                candidates.Add((_rules[index], selector.Specificity, index));
            }
        }

        candidates.Sort((one, other) =>
            one.Specificity != other.Specificity
                ? one.Specificity.CompareTo(other.Specificity)
                : one.Order.CompareTo(other.Order));

        var rules = new List<ICssPageRule>(candidates.Count);

        foreach (var candidate in candidates)
        {
            rules.Add(candidate.Rule);
        }

        return rules;
    }

    private static SelectorInfo SelectorOf(ICssPageRule rule)
    {
        var text = rule.SelectorText?.Trim();

        if (String.IsNullOrEmpty(text))
        {
            return new SelectorInfo(null, Array.Empty<String>());
        }

        var parts = text.Split(':', StringSplitOptions.TrimEntries);
        var name = parts[0];
        var pseudos = parts.Length > 1
            ? parts[1..].Where(part => part.Length > 0).ToArray()
            : Array.Empty<String>();

        return new SelectorInfo(name.Length == 0 ? null : name, pseudos);
    }

    private readonly record struct SelectorInfo(String? Name, String[] Pseudos)
    {
        public Boolean IsDefault => Name is null && (Pseudos is null || Pseudos.Length == 0);

        public Int32 Specificity => (Name is null ? 0 : 1) + (Pseudos?.Length ?? 0);

        public Boolean Matches(String? pageName, Int32 number, Boolean isLeft, Boolean blank)
        {
            if (Name is not null && !String.Equals(Name, pageName, StringComparison.Ordinal))
            {
                return false;
            }

            if (Pseudos is null)
            {
                return true;
            }

            foreach (var pseudo in Pseudos)
            {
                var matches = pseudo.ToLowerInvariant() switch
                {
                    "first" => number == 1,
                    "left" => isLeft,
                    "right" => !isLeft,
                    "blank" => blank,
                    _ => false,
                };

                if (!matches)
                {
                    return false;
                }
            }

            return true;
        }
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
            size = _defaultSize;
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

        public static SideSlot FromPixels(Double pixels) =>
            new(SideSpec.Of(StyleLength.FromPixels(pixels)), false);

        public SideSpec Value { get; }

        public Boolean Important { get; }

        public Boolean Set { get; }
    }
}

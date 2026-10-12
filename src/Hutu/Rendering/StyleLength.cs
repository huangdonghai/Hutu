using System;
using System.Collections.Generic;

namespace Hutu.Rendering;

/// <summary>The shape of a <see cref="StyleLength"/>.</summary>
/// <remarks>
/// <see cref="Unset"/> exists to be the zero of the enum. A <c>default</c> <see cref="StyleLength"/> is
/// what an <c>out</c> parameter holds after a failed read, and without a distinct member that default
/// would be indistinguishable from <c>0px</c> - a length a box can be sized from, which is how a
/// missing <c>flex-basis</c> once came out as zero instead of <c>auto</c>.
/// </remarks>
public enum StyleLengthKind : byte
{
    /// <summary>Nothing was read. Never a valid length.</summary>
    Unset,

    /// <summary>A length that is already in pixels.</summary>
    Pixels,

    /// <summary>A fraction of the containing block, stored as a fraction (50% is 0.5).</summary>
    Percent,

    /// <summary>The <c>auto</c> keyword, which is a value rather than a length.</summary>
    Auto,

    /// <summary>The <c>none</c> keyword, as in <c>max-width: none</c>.</summary>
    Keyword,

    /// <summary>The result of <c>min()</c>, <c>max()</c> or <c>clamp()</c> over other lengths.</summary>
    Math,
}

/// <summary>Which of the length functions a <see cref="StyleLengthKind.Math"/> value is.</summary>
public enum StyleMathFunction : byte
{
    Min,
    Max,
    Clamp,
}

/// <summary>
/// A length as the layout engine wants it: with its unit still attached.
/// </summary>
/// <remarks>
/// Percentages deliberately survive here instead of being resolved when the style is read. A
/// percentage is relative to the containing block, which is a layout-time notion, so resolving it
/// earlier is what made <c>width: 100%</c> mean "one viewport" rather than "one container".
/// <c>width</c>, <c>height</c>, the min/max constraints and the margin and padding insets all go
/// through this type; the font-relative properties (font size, line height) keep the pixel path in
/// <see cref="ComputedStyle"/> because their percentages are relative to a font size, not a box.
/// <para>
/// <see cref="Auto"/> and <see cref="Keyword"/> are carried alongside the lengths because several
/// properties accept either a length or a keyword, and telling the two apart is what makes
/// <c>margin: auto</c> and <c>width: auto</c> work.
/// </para>
/// </remarks>
public readonly struct StyleLength : IEquatable<StyleLength>
{
    private readonly Double _value;
    private readonly StyleLengthKind _kind;
    private readonly StyleMathFunction _function;
    private readonly StyleLength[]? _operands;

    private StyleLength(Double value, StyleLengthKind kind)
    {
        _value = value;
        _kind = kind;
        _function = default;
        _operands = null;
    }

    private StyleLength(StyleMathFunction function, StyleLength[] operands)
    {
        _value = 0;
        _kind = StyleLengthKind.Math;
        _function = function;
        _operands = operands;
    }

    /// <summary>Gets the <c>auto</c> keyword.</summary>
    public static StyleLength Auto { get; } = new(0, StyleLengthKind.Auto);

    /// <summary>Gets the <c>none</c> keyword.</summary>
    public static StyleLength None { get; } = new(0, StyleLengthKind.Keyword);

    /// <summary>Gets a zero pixel length.</summary>
    public static StyleLength Zero { get; } = new(0, StyleLengthKind.Pixels);

    public StyleLengthKind Kind => _kind;

    public Boolean IsAuto => _kind == StyleLengthKind.Auto;

    /// <summary>Gets whether this is a number that a box can be sized from.</summary>
    public Boolean HasValue => _kind is StyleLengthKind.Pixels or StyleLengthKind.Percent or StyleLengthKind.Math;

    /// <summary>
    /// Gets whether this length is a fraction of the containing block, through itself or through an
    /// argument of a <c>min()</c>, <c>max()</c> or <c>clamp()</c>.
    /// </summary>
    /// <remarks>
    /// A box that reads the containing block cannot be sized from a hypothesis about it, which is what
    /// an intrinsic measurement hands down - see <c>LayoutContext.BasisIsHypothetical</c>.
    /// </remarks>
    public Boolean ReadsContainingBlock => Kind switch
    {
        StyleLengthKind.Percent => true,
        StyleLengthKind.Math => OperandsReadContainingBlock(_operands),
        _ => false,
    };

    /// <summary>Gets whether any argument of a math function reads the containing block.</summary>
    private static Boolean OperandsReadContainingBlock(StyleLength[]? operands)
    {
        if (operands is null)
        {
            return false;
        }

        foreach (var operand in operands)
        {
            if (operand.ReadsContainingBlock)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the arguments of a <see cref="StyleLengthKind.Math"/> length, or null for every other
    /// kind.
    /// </summary>
    /// <remarks>
    /// Exposed because whether a length is a fraction of the containing block decides whether the
    /// intrinsic size of the box holding it can be cached without the basis, and a
    /// <c>min()</c>, <c>max()</c> or <c>clamp()</c> hides that answer one level down: a length whose
    /// own kind says <see cref="StyleLengthKind.Math"/> still reads the containing block when one of
    /// its arguments does.
    /// </remarks>
    internal IReadOnlyList<StyleLength>? Operands => _operands;

    /// <summary>
    /// Gets the number: pixels for <see cref="StyleLengthKind.Pixels"/>, a fraction for
    /// <see cref="StyleLengthKind.Percent"/>.
    /// </summary>
    public Double Value => _value;

    public static StyleLength FromPixels(Double pixels) => new(pixels, StyleLengthKind.Pixels);

    /// <param name="fraction">The fraction of the containing block, so 50% is <c>0.5</c>.</param>
    public static StyleLength FromPercent(Double fraction) => new(fraction, StyleLengthKind.Percent);

    public static StyleLength Min(IReadOnlyList<StyleLength> operands) =>
        new(StyleMathFunction.Min, Copy(operands));

    public static StyleLength Max(IReadOnlyList<StyleLength> operands) =>
        new(StyleMathFunction.Max, Copy(operands));

    /// <param name="operands">Exactly three: the minimum, the preferred value and the maximum.</param>
    public static StyleLength Clamp(IReadOnlyList<StyleLength> operands) =>
        new(StyleMathFunction.Clamp, Copy(operands));

    /// <summary>
    /// Resolves to pixels against the size a percentage is measured from. Returns zero for
    /// <see cref="Auto"/> and <see cref="None"/>, which the caller has to handle before it gets here:
    /// they are not lengths.
    /// </summary>
    public Double Resolve(Double basis)
    {
        switch (_kind)
        {
            case StyleLengthKind.Pixels:
                return _value;

            case StyleLengthKind.Percent:
                return _value * basis;

            case StyleLengthKind.Math:
                return ResolveMath(basis);

            default:
                return 0;
        }
    }

    /// <summary>Resolves to pixels, or returns the fallback for the keyword forms.</summary>
    public Double Resolve(Double basis, Double fallback) => HasValue ? Resolve(basis) : fallback;

    private Double ResolveMath(Double basis)
    {
        var operands = _operands;

        if (operands is null || operands.Length == 0)
        {
            return 0;
        }

        var first = operands[0].Resolve(basis);

        if (_function == StyleMathFunction.Clamp)
        {
            return operands.Length == 3
                ? Math.Clamp(operands[1].Resolve(basis), first, operands[2].Resolve(basis))
                : first;
        }

        var result = first;

        for (var index = 1; index < operands.Length; index++)
        {
            var value = operands[index].Resolve(basis);
            result = _function == StyleMathFunction.Min ? Math.Min(result, value) : Math.Max(result, value);
        }

        return result;
    }

    private static StyleLength[] Copy(IReadOnlyList<StyleLength> operands)
    {
        var copy = new StyleLength[operands.Count];

        for (var index = 0; index < operands.Count; index++)
        {
            copy[index] = operands[index];
        }

        return copy;
    }

    /// <summary>
    /// Gets whether two lengths are the same length: the same kind, the same function, the same number,
    /// and for a math function the same arguments.
    /// </summary>
    /// <remarks>
    /// The number is compared by its bits rather than by <c>==</c>, which is what this struct's own
    /// equality already said: a value type with nothing to compare by falls back to walking its bytes,
    /// so <c>0</c> and <c>-0</c> were never equal and neither were two NaNs of different payload. That
    /// answer is kept deliberately rather than inherited - an area built from lengths is shared between
    /// every box whose areas agree, and a comparison that begins calling two different lengths one would
    /// hand a box a border it never asked for.
    /// <para>
    /// The arguments are compared by content, not by reference. A <c>min()</c> written in a stylesheet
    /// twice is parsed twice into two arrays, and comparing the arrays by reference would let those two
    /// lengths disagree - costing sharing, which alone would be harmless, but this equality is also the
    /// half a hash has to agree with, and the hash below does not look at the arguments at all. Two
    /// lengths that differ only in their arguments therefore have the same hash and unequal equals, the
    /// one direction a table tolerates; the other direction is the one that breaks it quietly.
    /// </para>
    /// </remarks>
    public Boolean Equals(StyleLength other) =>
        _kind == other._kind &&
        _function == other._function &&
        BitConverter.DoubleToInt64Bits(_value) == BitConverter.DoubleToInt64Bits(other._value) &&
        OperandsEqual(_operands, other._operands);

    public override Boolean Equals(Object? obj) => obj is StyleLength other && Equals(other);

    /// <summary>
    /// Gets a hash of the kind, the function and the number's bits - and not of the arguments, which
    /// <see cref="Equals"/> does compare. A table asks this on every probe while it asks the full
    /// equality only once per bucket, and dropping the arguments can only ever split a pair that was
    /// unequal anyway: equal lengths agree on everything hashed here.
    /// </summary>
    public override Int32 GetHashCode() =>
        HashCode.Combine(_kind, _function, BitConverter.DoubleToInt64Bits(_value));

    /// <summary>
    /// Compares two lengths the way the type answers it, which is not the way a bare <see cref="Double"/>
    /// would be. Declared rather than left absent: without it the language forbids <c>==</c> on this
    /// struct today, and whatever replaces that error tomorrow would be free to disagree with
    /// <see cref="Equals(StyleLength)"/> over <c>0</c>, <c>-0</c> and NaN - the one disagreement a
    /// shared area cannot survive.
    /// </summary>
    public static Boolean operator ==(StyleLength one, StyleLength other) => one.Equals(other);

    public static Boolean operator !=(StyleLength one, StyleLength other) => !one.Equals(other);

    private static Boolean OperandsEqual(StyleLength[]? one, StyleLength[]? other)
    {
        if (ReferenceEquals(one, other))
        {
            return true;
        }

        if (one is null || other is null || one.Length != other.Length)
        {
            return false;
        }

        for (var index = 0; index < one.Length; index++)
        {
            if (!one[index].Equals(other[index]))
            {
                return false;
            }
        }

        return true;
    }

    public override String ToString() => _kind switch
    {
        StyleLengthKind.Unset => "-",
        StyleLengthKind.Pixels => _value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        StyleLengthKind.Percent => (_value * 100).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "%",
        StyleLengthKind.Auto => "auto",
        StyleLengthKind.Keyword => "none",
        _ => _function.ToString().ToLowerInvariant() + "(...)",
    };
}

using System.Text.RegularExpressions;

namespace Hutu.Conformance;

/// <summary>
/// The difference a pair of pages is allowed to come to, as the test that declares it says.
/// </summary>
/// <remarks>
/// A reftest is a claim that two pages come to the same picture, and a page that draws a gradient or a
/// border on a curve is drawn a little differently by every engine that draws it at all. A test may
/// therefore say how much of a difference it will accept: the largest difference it will allow in one
/// colour channel of one pixel, and the number of pixels it will allow to differ at all. The two
/// numbers are what the reftest suite of the web platform tests calls a fuzzy comparison, and the
/// rule it applies is the one this class holds: the two pictures are the same when they are the same
/// outright, or when the difference they do have falls inside both allowances at once.
/// <para>
/// The allowances are ranges, and the range is a band the difference has to fall in rather than a
/// ceiling it has to stay under: a page that allows between zero and two pixels to differ is asking
/// for a difference of one or two pixels, not for any number up to two - which is what makes a page
/// that asks for a fuzz of its own a page that can still fail.
/// </para>
/// </remarks>
/// <param name="Channel">The band the largest difference in one colour channel has to fall in.</param>
/// <param name="Pixels">The band the number of differing pixels has to fall in.</param>
internal sealed record FuzzyAllowance((Int32 Low, Int32 High) Channel, (Int32 Low, Int32 High) Pixels)
{
    private static readonly Regex Meta = new(
        @"<meta\b(?<attrs>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Gets the allowance the page states, or null when it states none.
    /// </summary>
    /// <remarks>
    /// The page that declares the reference is the one that states the allowance, and the forms are the
    /// ones the reftest suite accepts: <c>&lt;meta name="fuzzy" content="maxDifference=15;totalPixels=300"&gt;</c>,
    /// the same without the argument names, and the same with the numbers given as ranges. A page whose
    /// allowance cannot be read is a page with no allowance - the pair then has to match outright,
    /// which is the stricter of the two readings and never the flattering one.
    /// </remarks>
    public static FuzzyAllowance? Of(String html)
    {
        if (String.IsNullOrEmpty(html))
        {
            return null;
        }

        foreach (Match meta in Meta.Matches(html))
        {
            var attrs = meta.Groups["attrs"].Value;

            if (!Attribute(attrs, "name").Equals("fuzzy", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var content = Attribute(attrs, "content");

            return Parse(content);
        }

        return null;
    }

    /// <summary>
    /// Reads the two numbers out of the content of the meta element.
    /// </summary>
    private static FuzzyAllowance? Parse(String content)
    {
        (Int32 Low, Int32 High)? channel = null;
        (Int32 Low, Int32 High)? pixels = null;
        var bare = 0;

        foreach (var part in content.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');

            if (separator < 0)
            {
                // A value without a name is the first of the two numbers, then the second.
                var value = Band(part);

                if (value is null)
                {
                    return null;
                }

                if (bare == 0)
                {
                    channel = value;
                }
                else if (bare == 1)
                {
                    pixels = value;
                }
                else
                {
                    return null;
                }

                bare++;
                continue;
            }

            var name = part[..separator].Trim();
            var band = Band(part[(separator + 1)..].Trim());

            if (band is null)
            {
                return null;
            }

            if (name.Equals("maxDifference", StringComparison.OrdinalIgnoreCase))
            {
                channel = band;
            }
            else if (name.Equals("totalPixels", StringComparison.OrdinalIgnoreCase))
            {
                pixels = band;
            }
            else
            {
                return null;
            }
        }

        // Both numbers are asked for: an allowance of one of them is not an allowance.
        return channel is null || pixels is null ? null : new FuzzyAllowance(channel.Value, pixels.Value);
    }

    /// <summary>
    /// Reads a number, or a band of numbers, as the pair of ends the value may take.
    /// </summary>
    private static (Int32 Low, Int32 High)? Band(String value)
    {
        var dash = value.IndexOf('-', 1);

        if (dash > 0)
        {
            return Int32.TryParse(value[..dash].Trim(), out var low) &&
                   Int32.TryParse(value[(dash + 1)..].Trim(), out var high)
                ? (low, high)
                : null;
        }

        return Int32.TryParse(value, out var single) ? (single, single) : null;
    }

    /// <summary>
    /// Gets whether the two pictures are the same under this allowance.
    /// </summary>
    /// <param name="differing">How many pixels differ at all.</param>
    /// <param name="widest">The largest difference in one colour channel of one differing pixel.</param>
    /// <remarks>
    /// The rule is the one the reftest suite of the web platform tests applies: the pictures are the
    /// same when they are the same outright and the allowance admits a difference of no pixels, or when
    /// the difference they do have is nothing at all and the allowance admits that, or when the widest
    /// difference and the number of differing pixels both fall inside the bands the page states.
    /// </remarks>
    public Boolean Allows(Int32 differing, Int32 widest)
    {
        if (differing == 0 && Pixels.Low == 0)
        {
            return true;
        }

        if (widest == 0 && Channel.Low == 0)
        {
            return true;
        }

        return Channel.Low <= widest && widest <= Channel.High &&
               Pixels.Low <= differing && differing <= Pixels.High;
    }

    /// <summary>
    /// Gets the value of an attribute, which may be quoted or bare.
    /// </summary>
    private static String Attribute(String attrs, String name)
    {
        var match = Regex.Match(attrs, $@"\b{name}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))",
            RegexOptions.IgnoreCase);

        return match.Success ? match.Groups["v"].Value.Trim() : String.Empty;
    }
}

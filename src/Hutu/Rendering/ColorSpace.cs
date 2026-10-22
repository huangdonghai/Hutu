using System;

namespace Hutu.Rendering;

/// <summary>
/// The space the two ends of a ramp are carried to before they are mixed - the space a page names with
/// the <c>in</c> a gradient head opens with (CSS Color 4 §13.2).
/// </summary>
/// <remarks>
/// Each of these is the space CSS Color 4 §13.2 lists for <c>&lt;color-space&gt;</c>. The list splits
/// into the rectangular ones - three axes that are read straight - and the polar ones, whose third axis
/// is a hue angle and so needs the way round the circle the ramp takes.
/// </remarks>
internal enum ColorSpace
{
    /// <summary>The gamma-encoded space every colour of the Web is written in, and the one a painter mixes in.</summary>
    Srgb,

    /// <summary>Encoded sRGB with the transfer function taken off, so a value is the light it adds.</summary>
    SrgbLinear,

    /// <summary>The wide gamut a screen is built to, encoded with the sRGB transfer function.</summary>
    DisplayP3,

    /// <summary>Display P3 with the transfer function taken off.</summary>
    DisplayP3Linear,

    /// <summary>Adobe RGB 1998.</summary>
    A98Rgb,

    /// <summary>Pro Photo RGB, whose white point is D50 rather than D65.</summary>
    ProPhotoRgb,

    /// <summary>ITU-R BT.2020, the television gamut.</summary>
    Rec2020,

    /// <summary>CIE XYZ against the D65 white, which is the light each colour adds.</summary>
    XyzD65,

    /// <summary>CIE XYZ against the D50 white the Lab spaces are built on.</summary>
    XyzD50,

    /// <summary>CIE Lab against D50: lightness and two opponent axes.</summary>
    Lab,

    /// <summary>CIE LCh: the Lab axes turned into a chroma and a hue angle.</summary>
    Lch,

    /// <summary>Oklab, which spaces its lightness so that an equal step is an equal difference.</summary>
    Oklab,

    /// <summary>OklCh: the Oklab axes turned into a chroma and a hue angle.</summary>
    Oklch,

    /// <summary>The hue, saturation and lightness a colour picker offers.</summary>
    Hsl,

    /// <summary>Hue, white and black - the same hue axis, mixed by adding white and black to it.</summary>
    Hwb,
}

/// <summary>
/// Which way round the circle a hue is taken when two colours that name one are mixed (CSS Color 4 §13.5).
/// </summary>
internal enum HueMethod
{
    /// <summary>The shorter of the two arcs, which is what a ramp is taken to mean when nothing says otherwise.</summary>
    Shorter,

    /// <summary>The longer of the two arcs, so the ramp keeps its chroma the way round instead of passing through grey.</summary>
    Longer,

    /// <summary>Always upwards, wrapping past 360 degrees.</summary>
    Increasing,

    /// <summary>Always downwards, wrapping past zero.</summary>
    Decreasing,
}

/// <summary>
/// Mixes two colours along the ramp between them in the space a page names.
/// </summary>
/// <remarks>
/// The painter's own shader only interpolates the eight bits it is handed, so a ramp that is mixed
/// anywhere else has to be told, at each of the places it is sampled, what the space between its two ends
/// comes to. That is this: carry both ends to the named space, take the two between there - the hue going
/// the way the page asked and the rest going straight, both weighed by how much light they carry - and
/// bring the answer back to sRGB to be drawn (CSS Color 4 §13.1 to §13.5).
/// <para>
/// The matrices and transfer functions are those of CSS Color 4 §19, the chapter's own reference code,
/// and the ones a route needs are composed once here rather than at every sample.
/// </para>
/// </remarks>
internal static class ColorMixing
{
    private static readonly Double[] D50 = { 0.3457 / 0.3585, 1.00000, (1 - 0.3457 - 0.3585) / 0.3585 };

    // The routes of CSS Color 4 §19, as rows of three.
    private static readonly Double[] LinSrgbToXyz =
    {
        506752 / 1228815.0, 87881 / 245763.0, 12673 / 70218.0,
        87098 / 409605.0, 175762 / 245763.0, 12673 / 175545.0,
        7918 / 409605.0, 87881 / 737289.0, 1001167 / 1053270.0,
    };

    private static readonly Double[] XyzToLinSrgb =
    {
        12831 / 3959.0, -329 / 214.0, -1974 / 3959.0,
        -851781 / 878810.0, 1648619 / 878810.0, 36519 / 878810.0,
        705 / 12673.0, -2585 / 12673.0, 705 / 667.0,
    };

    private static readonly Double[] LinP3ToXyz =
    {
        608311 / 1250200.0, 189793 / 714400.0, 198249 / 1000160.0,
        35783 / 156275.0, 247089 / 357200.0, 198249 / 2500400.0,
        0.0, 32229 / 714400.0, 5220557 / 5000800.0,
    };

    private static readonly Double[] XyzToLinP3 =
    {
        446124 / 178915.0, -333277 / 357830.0, -72051 / 178915.0,
        -14852 / 17905.0, 63121 / 35810.0, 423 / 17905.0,
        11844 / 330415.0, -50337 / 660830.0, 316169 / 330415.0,
    };

    private static readonly Double[] LinA98ToXyz =
    {
        573536 / 994567.0, 263643 / 1420810.0, 187206 / 994567.0,
        591459 / 1989134.0, 6239551 / 9945670.0, 374412 / 4972835.0,
        53769 / 1989134.0, 351524 / 4972835.0, 4929758 / 4972835.0,
    };

    private static readonly Double[] XyzToLinA98 =
    {
        1829569 / 896150.0, -506331 / 896150.0, -308931 / 896150.0,
        -851781 / 878810.0, 1648619 / 878810.0, 36519 / 878810.0,
        16779 / 1248040.0, -147721 / 1248040.0, 1266979 / 1248040.0,
    };

    private static readonly Double[] Lin2020ToXyz =
    {
        63426534 / 99577255.0, 20160776 / 139408157.0, 47086771 / 278816314.0,
        26158966 / 99577255.0, 472592308 / 697040785.0, 8267143 / 139408157.0,
        0.0, 19567812 / 697040785.0, 295819943 / 278816314.0,
    };

    private static readonly Double[] XyzToLin2020 =
    {
        30757411 / 17917100.0, -6372589 / 17917100.0, -4539589 / 17917100.0,
        -19765991 / 29648200.0, 47925759 / 29648200.0, 467509 / 29648200.0,
        792561 / 44930125.0, -1921689 / 44930125.0, 42328811 / 44930125.0,
    };

    // Pro Photo RGB is built on D50, so its matrix takes D50 XYZ and is reached through the adaptation.
    private static readonly Double[] XyzD50ToLinProPhoto =
    {
        1.34578688164715830, -0.25557208737979464, -0.05110186497554526,
        -0.54463070512490190, 1.50824774284514680, 0.02052744743642139,
        0.0, 0.0, 1.21196754563894520,
    };

    private static readonly Double[] D65ToD50 =
    {
        1.0479297925449969, 0.022946870601609652, -0.05019226628920524,
        0.02962780877005599, 0.9904344267538799, -0.017073799063418826,
        -0.009243040646204504, 0.015055191490298152, 0.7518742814281371,
    };

    private static readonly Double[] D50ToD65 =
    {
        0.955473421488075, -0.02309845494876471, 0.06325924320057072,
        -0.0283697093338637, 1.0099953980813041, 0.021041441191917323,
        0.012314014864481998, -0.020507649298898964, 1.330365926242124,
    };

    private static readonly Double[] XyzToLms =
    {
        0.8190224379967030, 0.3619062600528904, -0.1288737815209879,
        0.0329836539323885, 0.9292868615863434, 0.0361446663506424,
        0.0481771893596242, 0.2642395317527308, 0.6335478284694309,
    };

    private static readonly Double[] LmsToOklab =
    {
        0.2104542683093140, 0.7936177747023054, -0.0040720430116193,
        1.9779985324311684, -2.4285922420485799, 0.4505937096174110,
        0.0259040424655478, 0.7827717124575296, -0.8086757549230774,
    };

    private static readonly Double[] OklabToLms =
    {
        1.0, 0.3963377773761749, 0.2158037573099136,
        1.0, -0.1055613458156586, -0.0638541728258133,
        1.0, -0.0894841775298119, -1.2914855480194092,
    };

    private static readonly Double[] LmsToXyz =
    {
        1.2268798758459243, -0.5578149944602171, 0.2813910456659647,
        -0.0405757452148008, 1.1122868032803170, -0.0717110580655164,
        -0.0763729366746601, -0.4214933324022432, 1.5869240198367816,
    };

    // The whole way from one space's own numbers to another's, built from the routes above.
    private static readonly Double[] LinSrgbToXyzD50 = Compose(D65ToD50, LinSrgbToXyz);
    private static readonly Double[] XyzD50ToLinSrgb = Compose(XyzToLinSrgb, D50ToD65);
    private static readonly Double[] LinSrgbToLinP3 = Compose(XyzToLinP3, LinSrgbToXyz);
    private static readonly Double[] LinP3ToLinSrgb = Compose(XyzToLinSrgb, LinP3ToXyz);
    private static readonly Double[] LinSrgbToLinA98 = Compose(XyzToLinA98, LinSrgbToXyz);
    private static readonly Double[] LinA98ToLinSrgb = Compose(XyzToLinSrgb, LinA98ToXyz);
    private static readonly Double[] LinSrgbToLin2020 = Compose(XyzToLin2020, LinSrgbToXyz);
    private static readonly Double[] Lin2020ToLinSrgb = Compose(XyzToLinSrgb, Lin2020ToXyz);
    private static readonly Double[] LinSrgbToLinProPhoto = Compose(XyzD50ToLinProPhoto, LinSrgbToXyzD50);
    private static readonly Double[] LinProPhotoToLinSrgb = Compose(XyzD50ToLinSrgb, Invert(XyzD50ToLinProPhoto));
    private static readonly Double[] LinSrgbToLms = Compose(XyzToLms, LinSrgbToXyz);
    private static readonly Double[] LmsToLinSrgb = Compose(XyzToLinSrgb, LmsToXyz);

    private const Double LabEpsilon = 216 / 24389.0;
    private const Double LabKappa = 24389 / 27.0;

    /// <summary>
    /// Gets the colour <paramref name="progress"/> of the way from <paramref name="from"/> to
    /// <paramref name="to"/>, in the space <paramref name="space"/> and along the arc
    /// <paramref name="method"/> names, as the packed ARGB a painter draws with.
    /// </summary>
    public static UInt32 Mix(
        UInt32 from,
        UInt32 to,
        Double progress,
        ColorSpace space,
        HueMethod method)
    {
        var alphaFrom = Channel(from, 24) / 255.0;
        var alphaTo = Channel(to, 24) / 255.0;
        var left = Forward(space, Unpack(from));
        var right = Forward(space, Unpack(to));
        var axis = HueAxis(space);

        // A colour whose hue axis says nothing - a grey, whose colour is all lightness - has no angle to
        // be taken from, and the ramp keeps the other end's instead of turning towards zero (CSS Color 4
        // §7.2, §13.3).
        if (axis >= 0)
        {
            var powerlessLeft = IsPowerless(space, left);
            var powerlessRight = IsPowerless(space, right);

            if (powerlessLeft != powerlessRight)
            {
                var kept = powerlessLeft ? right[axis] : left[axis];

                left[axis] = kept;
                right[axis] = kept;
            }
        }

        // Translucent ends are weighed by the light they carry, so a ramp to nothing darkens nothing and
        // only lets the light out (CSS Color 4 §13.4).
        left = left.Times(alphaFrom, axis);
        right = right.Times(alphaTo, axis);

        var mixed = new Channels
        {
            A = Between(left, right, 0, method, axis, progress),
            B = Between(left, right, 1, method, axis, progress),
            C = Between(left, right, 2, method, axis, progress),
        };

        var alpha = alphaFrom + (alphaTo - alphaFrom) * progress;

        if (alpha > 0)
        {
            mixed = mixed.DividedBy(alpha, axis);
        }

        var drawn = Reverse(space, mixed);

        return Pack(alpha, drawn);
    }

    /// <summary>The axis of the three that holds a hue angle, or -1 for the spaces that have none.</summary>
    private static Int32 HueAxis(ColorSpace space) => space switch
    {
        ColorSpace.Hsl or ColorSpace.Hwb => 0,
        ColorSpace.Lch or ColorSpace.Oklch => 2,
        _ => -1,
    };

    /// <summary>Says whether the space's hue axis is standing on nothing, because the colour has no chroma.</summary>
    private static Boolean IsPowerless(ColorSpace space, Channels value) => space switch
    {
        ColorSpace.Lch => Math.Abs(value.B) <= 0.0015,
        ColorSpace.Oklch => Math.Abs(value.B) <= 0.000004,
        ColorSpace.Hsl => value.B == 0,
        ColorSpace.Hwb => value.B + value.C >= 1,
        _ => false,
    };

    /// <summary>Gets one axis of the mix: the hue by the arc it is told to take, every other axis straight.</summary>
    private static Double Between(
        Channels left,
        Channels right,
        Int32 axis,
        HueMethod method,
        Int32 hue,
        Double progress)
    {
        var from = left[axis];
        var to = right[axis];

        if (axis != hue)
        {
            return from + (to - from) * progress;
        }

        (from, to) = Arc(from, to, method);

        return from + (to - from) * progress;
    }

    /// <summary>
    /// Moves the two angles onto the arc their method names, so that the difference between them is the
    /// turn the ramp takes (CSS Color 4 §13.5).
    /// </summary>
    private static (Double From, Double To) Arc(Double from, Double to, HueMethod method) => method switch
    {
        HueMethod.Shorter when to - from > 180 => (from + 360, to),
        HueMethod.Shorter when to - from < -180 => (from, to + 360),
        HueMethod.Longer when to - from > 0 && to - from < 180 => (from + 360, to),
        HueMethod.Longer when to - from > -180 && to - from <= 0 => (from, to + 360),
        HueMethod.Increasing when to < from => (from, to + 360),
        HueMethod.Decreasing when to > from => (from, to - 360),
        _ => (from, to),
    };

    /// <summary>Carries a colour the painter can draw to the numbers of the space it is mixed in.</summary>
    private static Channels Forward(ColorSpace space, Channels srgb) => space switch
    {
        ColorSpace.Srgb => srgb,
        ColorSpace.SrgbLinear => srgb.Through(SrgbToLinear),
        ColorSpace.DisplayP3 => srgb.Through(SrgbToLinear).Through(LinSrgbToLinP3).Through(LinearToSrgb),
        ColorSpace.DisplayP3Linear => srgb.Through(SrgbToLinear).Through(LinSrgbToLinP3),
        ColorSpace.A98Rgb => srgb.Through(SrgbToLinear).Through(LinSrgbToLinA98).Through(LinearToA98),
        ColorSpace.ProPhotoRgb => srgb.Through(SrgbToLinear).Through(LinSrgbToLinProPhoto).Through(LinearToProPhoto),
        ColorSpace.Rec2020 => srgb.Through(SrgbToLinear).Through(LinSrgbToLin2020).Through(LinearToRec2020),
        ColorSpace.XyzD65 => srgb.Through(SrgbToLinear).Through(LinSrgbToXyz),
        ColorSpace.XyzD50 => srgb.Through(SrgbToLinear).Through(LinSrgbToXyzD50),
        ColorSpace.Lab => XyzToLab(srgb.Through(SrgbToLinear).Through(LinSrgbToXyzD50)),
        ColorSpace.Lch => RectangularToPolar(XyzToLab(srgb.Through(SrgbToLinear).Through(LinSrgbToXyzD50))),
        ColorSpace.Oklab => OklabOf(srgb),
        ColorSpace.Oklch => RectangularToPolar(OklabOf(srgb)),
        ColorSpace.Hsl => RgbToHsl(srgb),
        ColorSpace.Hwb => RgbToHwb(srgb),
        _ => srgb,
    };

    /// <summary>Brings a colour from the space it was mixed in back to the one the painter draws with.</summary>
    private static Channels Reverse(ColorSpace space, Channels value) => space switch
    {
        ColorSpace.Srgb => value,
        ColorSpace.SrgbLinear => value.Through(LinearToSrgb),
        ColorSpace.DisplayP3 => value.Through(SrgbToLinear).Through(LinP3ToLinSrgb).Through(LinearToSrgb),
        ColorSpace.DisplayP3Linear => value.Through(LinP3ToLinSrgb).Through(LinearToSrgb),
        ColorSpace.A98Rgb => value.Through(A98ToLinear).Through(LinA98ToLinSrgb).Through(LinearToSrgb),
        ColorSpace.ProPhotoRgb => value.Through(ProPhotoToLinear).Through(LinProPhotoToLinSrgb).Through(LinearToSrgb),
        ColorSpace.Rec2020 => value.Through(Rec2020ToLinear).Through(Lin2020ToLinSrgb).Through(LinearToSrgb),
        ColorSpace.XyzD65 => value.Through(XyzToLinSrgb).Through(LinearToSrgb),
        ColorSpace.XyzD50 => value.Through(XyzD50ToLinSrgb).Through(LinearToSrgb),
        ColorSpace.Lab => LabToXyz(value).Through(XyzD50ToLinSrgb).Through(LinearToSrgb),
        ColorSpace.Lch => LabToXyz(PolarToRectangular(value)).Through(XyzD50ToLinSrgb).Through(LinearToSrgb),
        ColorSpace.Oklab => SrgbOfOklab(value),
        ColorSpace.Oklch => SrgbOfOklab(PolarToRectangular(value)),
        ColorSpace.Hsl => HslToRgb(value),
        ColorSpace.Hwb => HwbToRgb(value),
        _ => value,
    };

    private static Channels OklabOf(Channels srgb)
    {
        var lms = srgb.Through(SrgbToLinear).Through(LinSrgbToLms);

        return new Channels
        {
            A = Math.Cbrt(lms.A),
            B = Math.Cbrt(lms.B),
            C = Math.Cbrt(lms.C),
        }.Through(LmsToOklab);
    }

    private static Channels SrgbOfOklab(Channels oklab)
    {
        var lms = oklab.Through(OklabToLms);

        return new Channels { A = lms.A * lms.A * lms.A, B = lms.B * lms.B * lms.B, C = lms.C * lms.C * lms.C }
            .Through(LmsToLinSrgb)
            .Through(LinearToSrgb);
    }

    // --- the spaces that are not matrices ---------------------------------

    /// <summary>Turns the two opponent axes of a rectangular space into a chroma and a hue angle.</summary>
    private static Channels RectangularToPolar(Channels rectangular)
    {
        var hue = Math.Atan2(rectangular.C, rectangular.B) * 180 / Math.PI;

        return new Channels
        {
            A = rectangular.A,
            B = Math.Sqrt(rectangular.B * rectangular.B + rectangular.C * rectangular.C),
            C = hue < 0 ? hue + 360 : hue,
        };
    }

    private static Channels PolarToRectangular(Channels polar)
    {
        var radians = polar.C * Math.PI / 180.0;

        return new Channels
        {
            A = polar.A,
            B = polar.B * Math.Cos(radians),
            C = polar.B * Math.Sin(radians),
        };
    }

    private static Channels XyzToLab(Channels xyz)
    {
        var scaled = new Channels
        {
            A = CubeRootOrLinear(xyz.A / D50[0]),
            B = CubeRootOrLinear(xyz.B / D50[1]),
            C = CubeRootOrLinear(xyz.C / D50[2]),
        };

        return new Channels
        {
            A = 116 * scaled.B - 16,
            B = 500 * (scaled.A - scaled.B),
            C = 200 * (scaled.B - scaled.C),
        };
    }

    private static Channels LabToXyz(Channels lab)
    {
        var lightness = lab.A / 116;
        var fy = lightness + 16 / 116.0;
        var fx = fy + lab.B / 500;
        var fz = fy - lab.C / 200;

        return new Channels
        {
            A = CubedOrLinear(fx, lightness + lab.B / 500) * D50[0],
            B = lab.A > LabKappa * LabEpsilon
                ? Math.Pow((lab.A + 16) / 116, 3)
                : lab.A / LabKappa * D50[1],
            C = CubedOrLinear(fz, lightness - lab.C / 200) * D50[2],
        };
    }

    private static Double CubeRootOrLinear(Double value) =>
        value > LabEpsilon ? Math.Cbrt(value) : (LabKappa * value + 16) / 116;

    /// <summary>The first axis of Lab, which is a cube unless the second of the pair the step lands on is small.</summary>
    private static Double CubedOrLinear(Double f, Double scaled) =>
        f * f * f > LabEpsilon ? f * f * f : scaled * (116 / LabKappa);

    private static Channels RgbToHsl(Channels rgb)
    {
        var highest = Math.Max(rgb.A, Math.Max(rgb.B, rgb.C));
        var lowest = Math.Min(rgb.A, Math.Min(rgb.B, rgb.C));
        var difference = highest - lowest;
        var lightness = (highest + lowest) / 2;

        var hue = difference switch
        {
            0 => 0.0,
            _ when highest == rgb.A => ((rgb.B - rgb.C) / difference) % 6 * 60,
            _ when highest == rgb.B => ((rgb.C - rgb.A) / difference + 2) * 60,
            _ => ((rgb.A - rgb.B) / difference + 4) * 60,
        };

        var saturation = difference switch
        {
            0 => 0.0,
            _ when lightness == 0 || lightness == 1 => 0.0,
            _ when lightness <= 0.5 => difference / (highest + lowest),
            _ => difference / (2 - highest - lowest),
        };

        return new Channels { A = hue, B = saturation, C = lightness };
    }

    private static Channels HslToRgb(Channels hsl)
    {
        var lightness = hsl.C;

        if (hsl.B == 0)
        {
            return new Channels { A = lightness, B = lightness, C = lightness };
        }

        var q = lightness < 0.5
            ? lightness * (1 + hsl.B)
            : lightness + hsl.B - lightness * hsl.B;
        var p = 2 * lightness - q;
        var hue = hsl.A / 360;

        return new Channels
        {
            A = HslChannel(p, q, hue + 1.0 / 3),
            B = HslChannel(p, q, hue),
            C = HslChannel(p, q, hue - 1.0 / 3),
        };
    }

    private static Double HslChannel(Double low, Double high, Double hue)
    {
        hue = hue switch
        {
            < 0 => hue + 1,
            > 1 => hue - 1,
            _ => hue,
        };

        return hue switch
        {
            < 1.0 / 6 => low + (high - low) * 6 * hue,
            < 1.0 / 2 => high,
            < 2.0 / 3 => low + (high - low) * (2.0 / 3 - hue) * 6,
            _ => low,
        };
    }

    private static Channels RgbToHwb(Channels rgb)
    {
        var hue = RgbToHsl(rgb).A;
        var white = Math.Min(rgb.A, Math.Min(rgb.B, rgb.C));
        var black = 1 - Math.Max(rgb.A, Math.Max(rgb.B, rgb.C));

        return new Channels { A = hue, B = white, C = black };
    }

    private static Channels HwbToRgb(Channels hwb)
    {
        if (hwb.B + hwb.C >= 1)
        {
            var grey = hwb.B / (hwb.B + hwb.C);

            return new Channels { A = grey, B = grey, C = grey };
        }

        var full = HslToRgb(new Channels { A = hwb.A, B = 1, C = 0.5 });
        var left = 1 - hwb.B - hwb.C;

        return new Channels
        {
            A = full.A * left + hwb.B,
            B = full.B * left + hwb.B,
            C = full.C * left + hwb.B,
        };
    }

    // --- transfer functions ----------------------------------------------

    /// <summary>Takes the sRGB curve off one channel, the way the chapter extends it past the two ends.</summary>
    private static Double SrgbToLinear(Double value)
    {
        var away = Math.Abs(value);

        return away <= 0.04045
            ? value / 12.92
            : Math.Sign(value) * Math.Pow((away + 0.055) / 1.055, 2.4);
    }

    private static Double LinearToSrgb(Double value)
    {
        var away = Math.Abs(value);

        return away > 0.0031308
            ? Math.Sign(value) * (1.055 * Math.Pow(away, 1 / 2.4) - 0.055)
            : 12.92 * value;
    }

    private static Double A98ToLinear(Double value) => SignRoot(value, 563.0 / 256.0);

    private static Double LinearToA98(Double value) => SignRoot(value, 256.0 / 563.0);

    private static Double LinearToRec2020(Double value) => SignRoot(value, 1 / 2.4);

    private static Double Rec2020ToLinear(Double value) => SignRoot(value, 2.4);

    private static Double ProPhotoToLinear(Double value)
    {
        var away = Math.Abs(value);

        return away <= 16 / 512.0
            ? value / 16
            : Math.Sign(value) * Math.Pow(away, 1.8);
    }

    private static Double LinearToProPhoto(Double value)
    {
        var away = Math.Abs(value);

        return away < 1 / 512.0
            ? 16 * value
            : Math.Sign(value) * Math.Pow(away, 1 / 1.8);
    }

    private static Double SignRoot(Double value, Double exponent) =>
        Math.Sign(value) * Math.Pow(Math.Abs(value), exponent);

    // --- the packed colour -----------------------------------------------

    private static Channels Unpack(UInt32 color) => new()
    {
        A = Channel(color, 16) / 255.0,
        B = Channel(color, 8) / 255.0,
        C = Channel(color, 0) / 255.0,
    };

    private static UInt32 Pack(Double alpha, Channels rgb)
    {
        Byte Stated(Double value) =>
            (Byte)Math.Round(Math.Clamp(value, 0, 1) * 255.0, MidpointRounding.AwayFromZero);

        return (UInt32)(
            ((UInt32)Stated(alpha) << 24) |
            ((UInt32)Stated(rgb.A) << 16) |
            ((UInt32)Stated(rgb.B) << 8) |
            Stated(rgb.C));
    }

    private static Double Channel(UInt32 color, Int32 shift) => (color >> shift) & 0xFF;

    // --- matrices ---------------------------------------------------------

    /// <summary>The two routes done one after the other, as the one matrix they come to.</summary>
    private static Double[] Compose(Double[] after, Double[] before)
    {
        var both = new Double[9];

        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                both[row * 3 + column] =
                    after[row * 3] * before[column] +
                    after[row * 3 + 1] * before[3 + column] +
                    after[row * 3 + 2] * before[6 + column];
            }
        }

        return both;
    }

    private static Double[] Invert(Double[] matrix)
    {
        var determinant =
            matrix[0] * (matrix[4] * matrix[8] - matrix[5] * matrix[7]) -
            matrix[1] * (matrix[3] * matrix[8] - matrix[5] * matrix[6]) +
            matrix[2] * (matrix[3] * matrix[7] - matrix[4] * matrix[6]);

        return new[]
        {
            (matrix[4] * matrix[8] - matrix[5] * matrix[7]) / determinant,
            (matrix[2] * matrix[7] - matrix[1] * matrix[8]) / determinant,
            (matrix[1] * matrix[5] - matrix[2] * matrix[4]) / determinant,
            (matrix[5] * matrix[6] - matrix[3] * matrix[8]) / determinant,
            (matrix[0] * matrix[8] - matrix[2] * matrix[6]) / determinant,
            (matrix[2] * matrix[3] - matrix[0] * matrix[5]) / determinant,
            (matrix[3] * matrix[7] - matrix[4] * matrix[6]) / determinant,
            (matrix[1] * matrix[6] - matrix[0] * matrix[7]) / determinant,
            (matrix[0] * matrix[4] - matrix[1] * matrix[3]) / determinant,
        };
    }

    /// <summary>Three numbers of one colour in one space, which is all a ramp ever needs.</summary>
    private struct Channels
    {
        public Double A;
        public Double B;
        public Double C;

        public Double this[Int32 index]
        {
            readonly get => index switch { 0 => A, 1 => B, _ => C };
            set
            {
                switch (index)
                {
                    case 0:
                        A = value;
                        break;
                    case 1:
                        B = value;
                        break;
                    default:
                        C = value;
                        break;
                }
            }
        }

        /// <summary>Gets the three through one function, as each of them is read on its own.</summary>
        public readonly Channels Through(Func<Double, Double> channel) => new()
        {
            A = channel(A),
            B = channel(B),
            C = channel(C),
        };

        /// <summary>Gets the three through one matrix.</summary>
        public readonly Channels Through(Double[] matrix) => new()
        {
            A = matrix[0] * A + matrix[1] * B + matrix[2] * C,
            B = matrix[3] * A + matrix[4] * B + matrix[5] * C,
            C = matrix[6] * A + matrix[7] * B + matrix[8] * C,
        };

        /// <summary>Gets the three weighed by how much light they carry, leaving the hue axis alone.</summary>
        public readonly Channels Times(Double by, Int32 except) => new()
        {
            A = except == 0 ? A : A * by,
            B = except == 1 ? B : B * by,
            C = except == 2 ? C : C * by,
        };

        public readonly Channels DividedBy(Double by, Int32 except) => Times(1 / by, except);
    }
}

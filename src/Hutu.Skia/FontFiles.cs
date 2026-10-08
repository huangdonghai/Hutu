using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Hutu.Skia;

/// <summary>
/// The font files the machine keeps, each under the name its file carries.
/// </summary>
/// <remarks>
/// CSS asks for a font by the name it holds inside itself rather than by the file it is kept in, and
/// the platform offers no way to ask for one by that name: the font manager matches families. The two
/// names are usually close - a file is named after its font, give or take the decorations a platform
/// adds - so the files are listed and matched by name, and the handful that look right are opened and
/// asked what they really are. Listing a font directory costs a directory read; opening a font costs
/// a font, which is why only the candidates are opened.
/// </remarks>
internal static class FontFiles
{
    private static readonly String[] Extensions = { ".ttf", ".otf", ".ttc", ".otc" };

    /// <summary>Gets every font file, as its normalised name and the path it is kept at.</summary>
    public static IEnumerable<(String Name, String Path)> All()
    {
        var seen = new HashSet<String>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directories())
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Files(directory))
            {
                if (seen.Add(path))
                {
                    yield return (Normalize(Path.GetFileNameWithoutExtension(path)), path);
                }
            }
        }
    }

    /// <summary>
    /// Gets a name as it is compared: letters and digits only, in one case.
    /// </summary>
    /// <remarks>
    /// The decorations a name is written with - the spaces, the hyphen between a family and its style,
    /// the case - are the platform's business rather than the font's, so `LXGW WenKai Light` and
    /// `LXGWWenKai-Light` are the same name written twice.
    /// </remarks>
    public static String Normalize(String name)
    {
        var builder = new StringBuilder(name.Length);

        foreach (var character in name)
        {
            if (Char.IsLetterOrDigit(character))
            {
                builder.Append(Char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static IEnumerable<String> Files(String directory)
    {
        IEnumerable<String> files;

        try
        {
            files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            // A directory that cannot be read is one this platform keeps its fonts elsewhere than in.
            yield break;
        }

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file);

            foreach (var known in Extensions)
            {
                if (String.Equals(extension, known, StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                    break;
                }
            }
        }
    }

    /// <summary>Gets the directories a platform keeps its fonts in.</summary>
    private static IEnumerable<String> Directories()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Fonts");

            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts";
            yield return "/Library/Fonts";
            yield return Path.Combine(home, "Library", "Fonts");

            yield break;
        }

        yield return "/usr/share/fonts";
        yield return "/usr/local/share/fonts";
        yield return Path.Combine(home, ".fonts");
        yield return Path.Combine(home, ".local", "share", "fonts");
    }
}

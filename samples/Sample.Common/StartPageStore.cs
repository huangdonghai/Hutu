using System;
using System.IO;

namespace Sample.Common;

/// <summary>
/// Remembers the address of the page that was open when the app was last used, so that the next run
/// can open it again instead of starting from nothing.
/// </summary>
/// <remarks>
/// <para>
/// The address is kept in a one-line text file under the per-user application data folder. It is
/// written after a page has loaded and read once at start-up; there is nothing else to it, so there
/// is no format to keep in step with the addresses themselves.
/// </para>
/// <para>
/// A shell with nowhere to write - the browser build, whose file system is gone by the time the
/// page is reloaded - reports that nothing was remembered and forgets what it is told, which is what
/// it would do had the file never been written.
/// </para>
/// <para>
/// Nothing here is allowed to throw. A store that cannot be read or written is not worth stopping a
/// start-up over: the reader is given the page they would have been given anyway.
/// </para>
/// </remarks>
public static class StartPageStore
{
    private static readonly string? FilePath = Locate();

    /// <summary>The last address, or null when there is none to go back to.</summary>
    public static string? Load()
    {
        if (FilePath is null)
            return null;

        try
        {
            var address = File.ReadAllText(FilePath).Trim();

            return address.Length == 0 ? null : address;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read is treated as one that was never written. The reader lands
            // on the page they would have landed on had they never been here before.
            return null;
        }
    }

    /// <summary>Records <paramref name="address"/> as the page to open next time.</summary>
    public static void Save(string address)
    {
        if (FilePath is null || string.IsNullOrWhiteSpace(address))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, address);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the record of where the reader was is not worth interrupting them for: the run
            // carries on, and the next one simply starts at the home page.
        }
    }

    /// <summary>The file the address is kept in, or null when this shell cannot keep one.</summary>
    private static string? Locate()
    {
        // The browser build's file system does not outlive the page, so a file is not a record there.
        // What would outlive it is the browser's own storage, which takes JavaScript interop to reach
        // and so is left out of this sample.
        if (OperatingSystem.IsBrowser())
            return null;

        var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrEmpty(folder))
            return null;

        return Path.Combine(folder, "Hutu.Samples", "start-page.txt");
    }
}

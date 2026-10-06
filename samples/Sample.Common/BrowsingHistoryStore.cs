using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Sample.Common;

/// <summary>
/// Remembers the addresses the reader had visited and where they stood among them, so that the next run
/// can open the same page and still walk back and forward through the rest.
/// </summary>
/// <remarks>
/// <para>
/// The history is kept in a small text file under the per-user application data folder: the position on
/// the first line and one address per line after it. It is written after each navigation and read once
/// at start-up. A file written before the history was kept holds the single address that was open, and
/// that is read as a one-entry history so an older install still comes back to where it was.
/// </para>
/// <para>
/// A shell with nowhere to write - the browser build, whose file system is gone by the time the page is
/// reloaded - reports that nothing was remembered and forgets what it is told, which is what it would
/// do had the file never been written.
/// </para>
/// <para>
/// Nothing here is allowed to throw. A store that cannot be read or written is not worth stopping a
/// start-up over: the reader is given the page they would have been given anyway.
/// </para>
/// </remarks>
public static class BrowsingHistoryStore
{
    private static readonly string? FilePath = Locate();

    /// <summary>The remembered history, or null when there is none to go back to.</summary>
    /// <param name="path">The file to read, or null for the one this shell keeps.</param>
    public static BrowsingHistory? Load(string? path = null)
    {
        var file = path ?? FilePath;

        if (file is null)
            return null;

        try
        {
            var lines = File.ReadAllLines(file);

            if (lines.Length == 0)
                return null;

            var addresses = new List<string>();
            var position = 0;

            if (Int32.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var saved))
            {
                position = saved;

                for (var index = 1; index < lines.Length; index++)
                {
                    Add(addresses, lines[index]);
                }
            }
            else
            {
                // A file from before the history was kept: a single address and no position.
                Add(addresses, lines[0]);
            }

            if (addresses.Count == 0)
                return null;

            return new BrowsingHistory(addresses, Math.Clamp(position, 0, addresses.Count - 1));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read is treated as one that was never written. The reader lands
            // on the page they would have landed on had they never been here before.
            return null;
        }
    }

    /// <summary>Records the history so that the next run can open where this one left off.</summary>
    /// <param name="path">The file to write, or null for the one this shell keeps.</param>
    public static void Save(BrowsingHistory history, string? path = null)
    {
        var file = path ?? FilePath;

        if (file is null || history.Addresses.Count == 0)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            var lines = new List<string>(history.Addresses.Count + 1)
            {
                history.Position.ToString(CultureInfo.InvariantCulture),
            };

            lines.AddRange(history.Addresses);
            File.WriteAllLines(file, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the record of where the reader was is not worth interrupting them for: the run
            // carries on, and the next one simply starts at the home page.
        }
    }

    private static void Add(List<string> addresses, string line)
    {
        var address = line.Trim();

        if (address.Length > 0)
            addresses.Add(address);
    }

    /// <summary>The file the history is kept in, or null when this shell cannot keep one.</summary>
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

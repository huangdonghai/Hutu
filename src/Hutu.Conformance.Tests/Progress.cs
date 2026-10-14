using System.Globalization;

namespace Hutu.Conformance;

/// <summary>
/// Where a long run says what it is doing, while it is doing it.
/// </summary>
/// <remarks>
/// A run that writes its report only at the end is indistinguishable from a run that has stopped: the
/// file on disk is either yesterday's or today's, and nothing in between says which. One line every so
/// often costs nothing and turns a silent wait into something that can be watched - which is what makes
/// it possible to start the thing in the background and look in on it, instead of sitting in front of a
/// terminal waiting for a spinner to mean something.
/// <para>
/// Every line carries a clock, so the file answers two questions and not one: how far the run has got,
/// and whether it is still going. A line that stops moving is the whole point - it is the difference
/// between "slow" and "stuck", and there is no other way to tell them apart from the outside.
/// </para>
/// </remarks>
internal static class Progress
{
    /// <summary>How often a run says where it is: often enough to watch, rare enough not to cost.</summary>
    private const Int32 Every = 100;

    private static readonly Object Gate = new();

    /// <summary>Where a named run keeps its log.</summary>
    public static String Log(String run) => Path.Combine(Corpus.Root, "build", $"{run}.log");

    /// <summary>Opens a fresh log, so every line in it belongs to the run that is going now.</summary>
    public static void Begin(String run, Int32 total)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Log(run))!);

            File.WriteAllText(Log(run), $"{Stamp()} begin, {total} to do\n");
        }
    }

    /// <summary>Says where the run has got to, when it has got far enough to be worth saying.</summary>
    public static void Tick(String run, Int32 done)
    {
        if (done <= 0 || done % Every != 0)
        {
            return;
        }

        lock (Gate)
        {
            File.AppendAllText(Log(run), $"{Stamp()} {done}\n");
        }
    }

    /// <summary>Closes the log, so a reader can tell a finished run from an abandoned one.</summary>
    public static void Done(String run, Int32 done)
    {
        lock (Gate)
        {
            File.AppendAllText(Log(run), $"{Stamp()} done, {done}\n");
        }
    }

    private static String Stamp() =>
        DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}

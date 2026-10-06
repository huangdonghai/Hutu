using NUnit.Framework;
using Sample.Common;
using System;
using System.IO;

namespace Hutu.Tests;

/// <summary>
/// Covers the sample's remembered browsing history: the round trip through the file, the shape a file
/// written before the history was kept is read in, and the guards around nothing being remembered.
/// </summary>
[TestFixture]
public sealed class BrowsingHistoryStoreTests
{
    private String _file = null!;

    [SetUp]
    public void SetUp() => _file = Path.Combine(Path.GetTempPath(), "hutu-history-" + Guid.NewGuid().ToString("N") + ".txt");

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_file))
        {
            File.Delete(_file);
        }
    }

    [Test]
    public void AHistoryRoundTrips()
    {
        BrowsingHistoryStore.Save(new BrowsingHistory(new[] { "a.htm", "b.htm", "c.htm" }, 2), _file);

        var loaded = BrowsingHistoryStore.Load(_file);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Addresses, Is.EqualTo(new[] { "a.htm", "b.htm", "c.htm" }));
        Assert.That(loaded.Position, Is.EqualTo(2), "the reader comes back to the page they left, not the last one");
    }

    [Test]
    public void AnAddressFromBeforeTheHistoryIsKeptAsAOneEntryHistory()
    {
        // The old store wrote the one address the reader was on, with no position line.
        File.WriteAllText(_file, "res:Pages/typography.htm");

        var loaded = BrowsingHistoryStore.Load(_file);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Addresses, Is.EqualTo(new[] { "res:Pages/typography.htm" }));
        Assert.That(loaded.Position, Is.EqualTo(0));
    }

    [Test]
    public void APositionBeyondTheEndIsClamped()
    {
        File.WriteAllText(_file, "9\na.htm\nb.htm");

        var loaded = BrowsingHistoryStore.Load(_file);

        Assert.That(loaded!.Position, Is.EqualTo(1), "a corrupt position lands on the last address rather than nothing");
    }

    [Test]
    public void NothingIsRememberedWithoutAFile()
    {
        Assert.That(BrowsingHistoryStore.Load(_file), Is.Null);
    }
}

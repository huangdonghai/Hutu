using System;
using System.Collections.Generic;

namespace Sample.Common;

/// <summary>
/// The addresses a reader has visited, and where in them they were standing.
/// </summary>
/// <remarks>
/// The list is in the order the addresses were first reached, and <see cref="Position"/> names the one
/// that was open. Everything before it can be walked back to and everything after it walked forward to,
/// which is what makes the buttons mean something again after a restart.
/// </remarks>
public sealed class BrowsingHistory
{
    public BrowsingHistory(IReadOnlyList<string> addresses, Int32 position)
    {
        Addresses = addresses;
        Position = position;
    }

    /// <summary>Gets the addresses that were visited, oldest first.</summary>
    public IReadOnlyList<string> Addresses { get; }

    /// <summary>Gets the index of the address that was open.</summary>
    public Int32 Position { get; }
}

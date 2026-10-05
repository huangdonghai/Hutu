using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hutu.Rendering.Media;

/// <summary>
/// Retrieves the raw bytes of a resource, resolved to an absolute URL. Supplied by the host, which
/// owns the browsing context and therefore the requesters.
/// </summary>
/// <returns>
/// A readable stream positioned at the start, or null when the resource is not available. The
/// caller disposes it.
/// </returns>
public delegate Task<Stream?> ResourceFetcher(String url, CancellationToken cancellationToken);

/// <summary>
/// Loads and decodes the images a document refers to, ahead of layout.
/// </summary>
/// <remarks>
/// Implementations must be resilient: one URL that cannot be fetched or decoded must degrade to
/// "not available" without disturbing the others, because a single broken image should never take
/// the whole page down.
/// </remarks>
public interface IImageLoader : IImageProvider
{
    /// <summary>
    /// Loads every URL that is not cached yet. Implementations should skip URLs already loaded so a
    /// relayout never re-downloads anything.
    /// </summary>
    Task LoadAsync(IReadOnlyList<String> urls, ResourceFetcher fetch, CancellationToken cancellationToken);
}

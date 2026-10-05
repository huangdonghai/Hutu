using System;

namespace Hutu.Rendering.Media;

/// <summary>
/// Answers the intrinsic size of an image that has already been loaded.
/// </summary>
/// <remarks>
/// Layout needs this synchronously, which is why loading happens ahead of layout rather than during
/// it. A null answer means "not available" and the caller falls back to the width/height attributes
/// and then to the specification defaults - a missing image must never fail a layout pass.
/// </remarks>
public interface IImageProvider
{
    ImageSize? GetSize(String url);
}

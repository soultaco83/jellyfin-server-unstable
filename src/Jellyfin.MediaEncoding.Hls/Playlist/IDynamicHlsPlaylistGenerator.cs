using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.MediaEncoding.Hls.Playlist;

/// <summary>
/// Generator for dynamic HLS playlists where the segment lengths aren't known in advance.
/// </summary>
public interface IDynamicHlsPlaylistGenerator
{
    /// <summary>
    /// Creates the main playlist containing the main video or audio stream.
    /// </summary>
    /// <param name="request">An instance of the <see cref="CreateMainPlaylistRequest"/> class.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The playlist as a formatted string.</returns>
    Task<string> CreateMainPlaylistAsync(CreateMainPlaylistRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the length of every segment of the main playlist when the playlist is cut at the keyframes of the file.
    /// </summary>
    /// <param name="request">An instance of the <see cref="CreateMainPlaylistRequest"/> class.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The segment lengths in seconds, in playlist order, or <c>null</c> when the playlist uses equal lengths because the video is transcoded or has no keyframe data.</returns>
    Task<IReadOnlyList<double>?> GetKeyframeSegmentLengthsAsync(CreateMainPlaylistRequest request, CancellationToken cancellationToken);
}

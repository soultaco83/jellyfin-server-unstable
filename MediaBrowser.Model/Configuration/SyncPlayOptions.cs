#pragma warning disable CS1591

namespace MediaBrowser.Model.Configuration;

/// <summary>
/// Represents the SyncPlay timing options for tuning group synchronization behavior.
/// </summary>
public class SyncPlayOptions
{
    /// <summary>
    /// Gets or sets the default ping value used for new sessions, in milliseconds.
    /// Increase for high-latency connections (e.g., trans-pacific 300ms+ RTT needs ~1000ms).
    /// </summary>
    /// <value>The default ping value in milliseconds.</value>
    public long DefaultPing { get; set; } = 500;

    /// <summary>
    /// Gets or sets the maximum offset error accepted for position reported by clients, in milliseconds.
    /// With trans-Pacific connections (~250ms RTT + jitter), position reports can differ
    /// by more than 500ms, causing constant re-syncs. Increase to 2000ms for such scenarios.
    /// </summary>
    /// <value>The maximum playback offset in milliseconds.</value>
    public long MaxPlaybackOffset { get; set; } = 500;

    /// <summary>
    /// Gets or sets the maximum time a member can be buffering before being auto-ignored, in milliseconds.
    /// After this timeout, the buffering member will be ignored and the group can resume playback.
    /// </summary>
    /// <value>The buffering timeout in milliseconds.</value>
    public long BufferingTimeoutMs { get; set; } = 30000;

    /// <summary>
    /// Gets or sets the maximum time offset error accepted for dates reported by clients, in milliseconds.
    /// </summary>
    /// <value>The time sync offset in milliseconds.</value>
    public long TimeSyncOffset { get; set; } = 2000;
}

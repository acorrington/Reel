using MediaBrowser.Model.Plugins;

namespace Reel.Configuration;

/// <summary>
/// User-visible plugin settings, stored as XML under plugins/configurations/Reel.xml.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Automatically download trailers when a new movie is added.</summary>
    public bool EnableAutoDownload { get; set; } = true;

    /// <summary>Seconds to wait after ItemAdded before downloading, so metadata providers can fill in RemoteTrailers.</summary>
    public int TriggerDelaySeconds { get; set; } = 30;

    /// <summary>Optional full path to ffmpeg. Leave empty to use Emby's bundled encoder.</summary>
    public string FfmpegPathOverride { get; set; } = string.Empty;

    /// <summary>Maximum video height in pixels (0 = best available).</summary>
    public int MaxVideoHeight { get; set; } = 0;

    /// <summary>When a movie has no RemoteTrailers, search YouTube for "{Title} {Year} official trailer".</summary>
    public bool EnableYouTubeSearchFallback { get; set; } = true;

    /// <summary>Maximum YouTube search candidates to try when RemoteTrailers is empty.</summary>
    public int MaxSearchResults { get; set; } = 3;

    /// <summary>Human readable summary of the last scheduled-task run (shown on the config page).</summary>
    public string LastRunSummary { get; set; } = string.Empty;

    // ------------------------------------------------------------------
    // YouTube-proofing settings: when YouTube deprecates a client version,
    // update it here (dashboard → Plugins → Reel) and restart — no rebuild.
    // ------------------------------------------------------------------

    /// <summary>InnerTube ANDROID client version. Empty = built-in default.</summary>
    public string AndroidClientVersion { get; set; } = "20.10.3";

    /// <summary>InnerTube IOS client version. Empty = built-in default.</summary>
    public string IosClientVersion { get; set; } = "20.10.4";

    /// <summary>Query the IOS client before ANDROID (use when ANDROID requests are blocked from your network).</summary>
    public bool PreferIosClient { get; set; } = false;
}

using MediaBrowser.Model.Plugins;

namespace Reel.Configuration;

/// <summary>
/// User-visible plugin settings, stored as XML under plugins/configurations/Reel.xml.
/// Field set specified by REQUIREMENTS.md §3.6 (F-50..F-57) plus the download /
/// YouTube-proofing settings inherited from Trawler.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ------------------------------------------------------------------
    // Scan control (F-50, F-51)
    // ------------------------------------------------------------------

    /// <summary>Master switch for the scheduled task (F-50). Manual "Run Now" still works when false.</summary>
    public bool EnableScheduledScan { get; set; } = true;

    /// <summary>Maximum number of music videos installed per run (F-51).</summary>
    public int MaxVideosPerRun { get; set; } = 100;

    // ------------------------------------------------------------------
    // Output (F-40 / F-52)
    // ------------------------------------------------------------------

    /// <summary>Folder where downloaded music videos are installed (F-52). Should be the
    /// path of an Emby library with content type "Music videos".</summary>
    public string TargetFolder { get; set; } = @"E:\Emby Server\Music Videos";

    // ------------------------------------------------------------------
    // Confidence gate (F-20 / F-21 / F-53 / F-54)
    // ------------------------------------------------------------------

    /// <summary>Allowed video-vs-audio duration difference, in percent (F-20).</summary>
    public int DurationTolerancePercent { get; set; } = 20;

    /// <summary>Newline/comma separated title patterns that disqualify a candidate (F-21).
    /// A trailing * makes the pattern a prefix wildcard (e.g. remix* matches remixes).</summary>
    public string ExcludeTitlePatterns { get; set; } =
        "lyric\nlyrics\nkaraoke\ncover\nreaction\nremix*\nvisualizer\ntopic\ninterview\nbehind the scenes\nmaking of\nshorts";

    // ------------------------------------------------------------------
    // Discovery (F-10 / F-11 / F-55)
    // ------------------------------------------------------------------

    /// <summary>IMVDb app key (F-55). Required by their API; when empty, Reel skips IMVDb
    /// and uses YouTube search only. Register at imvdb.com/developers/apps.</summary>
    public string ImvdbApiKey { get; set; } = string.Empty;

    /// <summary>Maximum YouTube search candidates evaluated per song (F-11).</summary>
    public int MaxSearchResults { get; set; } = 5;

    // ------------------------------------------------------------------
    // Download (inherited from Trawler)
    // ------------------------------------------------------------------

    /// <summary>Optional full path to ffmpeg. Leave empty to use Emby's bundled encoder.</summary>
    public string FfmpegPathOverride { get; set; } = string.Empty;

    /// <summary>Maximum video height in pixels (0 = best available).</summary>
    public int MaxVideoHeight { get; set; } = 0;

    // ------------------------------------------------------------------
    // YouTube-proofing settings (F-56): when YouTube deprecates a client version,
    // update it here (dashboard → Plugins → Reel) and restart — no rebuild.
    // ------------------------------------------------------------------

    /// <summary>InnerTube ANDROID client version. Empty = built-in default.</summary>
    public string AndroidClientVersion { get; set; } = "20.10.3";

    /// <summary>InnerTube IOS client version (player chain). Empty = built-in default.</summary>
    public string IosClientVersion { get; set; } = "20.10.4";

    /// <summary>InnerTube WEB client version used for SEARCH (the IOS client stopped serving
    /// search results entirely in 2026-10; WEB works). Empty = built-in default.</summary>
    public string WebSearchClientVersion { get; set; } = "2.20251006.01.00";

    /// <summary>Query the IOS client before ANDROID (use when ANDROID requests are blocked from your network).</summary>
    public bool PreferIosClient { get; set; } = false;

    // ------------------------------------------------------------------
    // Reporting (F-57)
    // ------------------------------------------------------------------

    /// <summary>Human readable summary of the last scheduled-task run (shown on the config page).</summary>
    public string LastRunSummary { get; set; } = string.Empty;
}

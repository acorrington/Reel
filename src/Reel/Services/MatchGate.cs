using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Logging;
using Reel.Configuration;

namespace Reel.Services;

/// <summary>Verdict of the confidence gate for one candidate.</summary>
public sealed class GateDecision
{
    public bool Passed { get; private set; }

    /// <summary>Rejecting gate: metadata | hygiene | artist | title | duration.</summary>
    public string Gate { get; private set; }

    /// <summary>Human readable reject reason, logged with the videoId (F-22, N-04).</summary>
    public string Reason { get; private set; }

    public long VideoDurationSeconds { get; private set; }

    public static GateDecision Pass(long videoDurationSeconds) =>
        new GateDecision { Passed = true, VideoDurationSeconds = videoDurationSeconds };

    public static GateDecision Reject(string gate, string reason, long videoDurationSeconds = 0) =>
        new GateDecision { Passed = false, Gate = gate, Reason = reason, VideoDurationSeconds = videoDurationSeconds };
}

/// <summary>
/// The confidence gate (F-20..F-22): a candidate passes only if EVERY enabled gate passes.
/// On any doubt → reject and log the reason with the videoId; never "best effort".
///
/// Gates:
///   1. metadata  — video title/duration could not be determined at all
///   2. hygiene   — YouTube title matches an exclude pattern (lyric/karaoke/cover/…, F-21)
///   3. artist    — the artist's name appears neither in the title nor the channel name
///   4. title     — the song's title does not appear in the video title (wrong-song guard)
///   5. duration  — video length outside DurationTolerancePercent of the audio track (F-20)
///
/// <see cref="PreCheck"/> runs the cheap gates (hygiene/title/duration) against search-result
/// metadata first, so obviously-bad candidates never spend a player request. The full
/// <see cref="Evaluate"/> then runs against authoritative player metadata.
///
/// Every decision is logged at debug level so match quality can be tuned (N-04).
/// </summary>
public sealed class MatchGate
{
    private readonly ILogger _logger;

    public MatchGate(ILogger logger)
    {
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    // ------------------------------------------------------------------ normalization

    /// <summary>Gate normalization: lowercase + punctuation→space, but bracket CONTENT is kept —
    /// hygiene must see "(Lyric Video)" and containment must see "(Official Video)".</summary>
    private static string GateNormalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var t = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", " ");
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    private static List<(string Text, bool Prefix)> LoadExcludePatterns(string raw)
    {
        var result = new List<(string, bool)>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        foreach (var line in raw.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            var isPrefix = trimmed.EndsWith("*", StringComparison.Ordinal);
            var text = GateNormalize(isPrefix ? trimmed.TrimEnd('*') : trimmed);
            if (text.Length > 0)
            {
                result.Add((text, isPrefix));
            }
        }

        return result;
    }

    /// <summary>Artist spelling variants worth matching: as-is, minus a leading "the".</summary>
    private static IEnumerable<string> ArtistVariants(string artist)
    {
        var norm = NeedsCheck.Normalize(artist);
        if (norm.Length == 0)
        {
            yield break;
        }

        yield return norm;

        if (norm.StartsWith("the ", StringComparison.Ordinal) && norm.Length > 4)
        {
            yield return norm.Substring(4);
        }
    }

    // ------------------------------------------------------------------ individual gates (log + reject, null = pass)

    private GateDecision CheckHygiene(string videoNorm, string channelNorm)
    {
        // Patterns are checked against the title AND the channel: "Artist – Topic" auto-uploads
        // (REQUIREMENTS §2's named noise class) carry "topic" in the channel name, not the title.
        foreach (var (pattern, isPrefix) in LoadExcludePatterns(Config.ExcludeTitlePatterns))
        {
            // trailing * in the config value = prefix wildcard (remix* matches remixes)
            foreach (var haystack in new[] { videoNorm, channelNorm })
            {
                if (string.IsNullOrEmpty(haystack))
                {
                    continue;
                }

                var hit = isPrefix
                    ? (" " + haystack + " ").Contains(" " + pattern, StringComparison.Ordinal)
                    : NeedsCheck.ContainsNormalized(haystack, pattern);

                if (hit)
                {
                    _logger.Debug("Reel: gate: reject (hygiene: matches exclude pattern \"{0}\")", pattern);
                    return GateDecision.Reject("hygiene", $"matches exclude pattern \"{pattern}\"");
                }
            }
        }

        return null;
    }

    private GateDecision CheckArtist(string videoNorm, string channelNorm, IReadOnlyList<string> artists)
    {
        var artistNorms = new List<string>();
        if (artists != null)
        {
            foreach (var artist in artists)
            {
                artistNorms.AddRange(ArtistVariants(artist));
            }
        }

        if (artistNorms.Count == 0)
        {
            _logger.Debug("Reel: gate: reject (artist: song has no artist tag)");
            return GateDecision.Reject("artist", "song has no artist tag");
        }

        var seen = artistNorms.Any(v =>
            NeedsCheck.ContainsNormalized(videoNorm, v) || NeedsCheck.ContainsNormalized(channelNorm, v));

        if (!seen)
        {
            _logger.Debug("Reel: gate: reject (artist: \"{0}\" not in title or channel)",
                string.Join("/", artistNorms.Distinct()));
            return GateDecision.Reject("artist", "artist not found in title or channel");
        }

        return null;
    }

    private GateDecision CheckTitle(string songNorm, string videoNorm)
    {
        if (songNorm.Length == 0)
        {
            _logger.Debug("Reel: gate: reject (title: song has no title)");
            return GateDecision.Reject("title", "song has no title");
        }

        if (!NeedsCheck.ContainsNormalized(videoNorm, songNorm))
        {
            _logger.Debug("Reel: gate: reject (title: \"{0}\" not in video title \"{1}\")", songNorm, videoNorm);
            return GateDecision.Reject("title", $"song title \"{songNorm}\" not in video title");
        }

        return null;
    }

    private GateDecision CheckDuration(long songDurationSeconds, long videoDurationSeconds)
    {
        if (songDurationSeconds <= 0)
        {
            _logger.Debug("Reel: gate: reject (duration: audio track has no duration)");
            return GateDecision.Reject("duration", "audio track has no duration");
        }

        if (videoDurationSeconds <= 0)
        {
            _logger.Debug("Reel: gate: reject (duration: video duration unknown)");
            return GateDecision.Reject("duration", "video duration unknown");
        }

        var tolerance = Math.Max(1, Config.DurationTolerancePercent);
        var delta = Math.Abs(videoDurationSeconds - songDurationSeconds);
        var pct = delta * 100.0 / songDurationSeconds;
        if (pct > tolerance)
        {
            _logger.Debug("Reel: gate: reject (duration: {0}s vs audio {1}s = {2:0.#}% > {3}%)",
                videoDurationSeconds, songDurationSeconds, pct, tolerance);
            return GateDecision.Reject("duration",
                $"video {videoDurationSeconds}s vs audio {songDurationSeconds}s = {pct:0.#}% > {tolerance}%",
                videoDurationSeconds);
        }

        return null;
    }

    // ------------------------------------------------------------------ public API

    /// <summary>
    /// Cheap pre-check against a search result's own metadata (title + shown duration) BEFORE
    /// fetching the player response. Runs hygiene, song-title containment and — when the result
    /// shows a duration — the duration gate. The artist gate is intentionally not run here:
    /// the channel name is only known from the player response.
    /// Returns a passing decision when the candidate deserves the full <see cref="Evaluate"/>.
    /// </summary>
    public GateDecision PreCheck(string songTitle, long songDurationSeconds, string searchTitle, long searchDurationSeconds)
    {
        var videoNorm = GateNormalize(searchTitle);
        if (videoNorm.Length == 0)
        {
            _logger.Debug("Reel: gate: reject (metadata: empty search title)");
            return GateDecision.Reject("metadata", "empty search title");
        }

        var decision = CheckHygiene(videoNorm, null)
                       ?? CheckTitle(NeedsCheck.Normalize(songTitle), videoNorm)
                       ?? (searchDurationSeconds > 0
                           ? CheckDuration(songDurationSeconds, searchDurationSeconds)
                           : null);

        return decision ?? GateDecision.Pass(searchDurationSeconds);
    }

    /// <summary>
    /// Full gate against authoritative player metadata. <paramref name="artists"/> are the
    /// song's artists, <paramref name="songTitle"/> the song's title, <paramref name="songDurationSeconds"/>
    /// the audio item's runtime; <paramref name="details"/> the candidate's YouTube metadata
    /// (null when it could not be fetched → reject, F-22).
    /// <paramref name="officialSource"/> = true for IMVDb candidates (F-20b): IMVDb's entry IS
    /// the official music video for this song, so the duration gate is skipped — the library
    /// audio may be a promo/remix edit of a different length while the official video is still
    /// the right music video. All other gates still run.
    /// </summary>
    public GateDecision Evaluate(IReadOnlyList<string> artists, string songTitle, long songDurationSeconds, VideoDetails details, bool officialSource = false)
    {
        if (details == null)
        {
            _logger.Debug("Reel: gate: reject (metadata: no title/duration available)");
            return GateDecision.Reject("metadata", "no title/duration available");
        }

        var videoNorm = GateNormalize(details.Title);
        if (videoNorm.Length == 0)
        {
            _logger.Debug("Reel: gate: reject (metadata: empty video title)");
            return GateDecision.Reject("metadata", "empty video title");
        }

        var channelNorm = GateNormalize(details.Channel);
        var songNorm = NeedsCheck.Normalize(songTitle);

        var decision = CheckHygiene(videoNorm, channelNorm)
                       ?? CheckArtist(videoNorm, channelNorm, artists)
                       ?? CheckTitle(songNorm, videoNorm)
                       ?? (officialSource
                           ? NullDurationGate(songNorm, details)
                           : CheckDuration(songDurationSeconds, details.DurationSeconds));

        if (decision != null)
        {
            // caller logs this with the artist/title/videoId (N-04)
            return decision;
        }

        _logger.Debug("Reel: gate: passed \"{0}\" ({1}s vs audio {2}s{3}) [{4}]",
            details.Title, details.DurationSeconds, songDurationSeconds,
            officialSource ? ", duration gate skipped: IMVDb official entry" : string.Empty,
            details.VideoId);
        return GateDecision.Pass(details.DurationSeconds);
    }

    /// <summary>F-20b: IMVDb official entry — duration gate skipped, but still require a known duration.</summary>
    private GateDecision NullDurationGate(string songNorm, VideoDetails details)
    {
        if (details.DurationSeconds <= 0)
        {
            _logger.Debug("Reel: gate: reject (duration: video duration unknown)");
            return GateDecision.Reject("duration", "video duration unknown");
        }

        _logger.Debug("Reel: gate: duration check skipped for IMVDb official entry ({0}s vs audio — official video wins)", details.DurationSeconds);
        return null;
    }
}

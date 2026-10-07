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
/// Gates, in evaluation order:
///   1. metadata  — video title/duration could not be determined at all
///   2. hygiene   — YouTube title matches an exclude pattern (lyric/karaoke/cover/…, F-21)
///   3. artist    — the artist's name appears neither in the title nor the channel name
///   4. title     — the song's title does not appear in the video title (wrong-song guard)
///   5. duration  — video length outside DurationTolerancePercent of the audio track (F-20)
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

    // ------------------------------------------------------------------ helpers

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

    // ------------------------------------------------------------------ evaluation

    /// <summary>
    /// Evaluate one candidate. <paramref name="artists"/> are the song's artists,
    /// <paramref name="songTitle"/> the song's title, <paramref name="songDurationSeconds"/>
    /// the audio item's runtime; <paramref name="details"/> the candidate's YouTube metadata
    /// (null when it could not be fetched).
    /// </summary>
    public GateDecision Evaluate(IReadOnlyList<string> artists, string songTitle, long songDurationSeconds, VideoDetails details)
    {
        // -- gate 0: metadata -------------------------------------------------
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

        // -- gate 1: title hygiene (F-21) -------------------------------------
        foreach (var (pattern, isPrefix) in LoadExcludePatterns(Config.ExcludeTitlePatterns))
        {
            // trailing * in the config value = prefix wildcard (remix* matches remixes)
            var hit = isPrefix
                ? (" " + videoNorm + " ").Contains(" " + pattern, StringComparison.Ordinal)
                : NeedsCheck.ContainsNormalized(videoNorm, pattern);

            if (hit)
            {
                _logger.Debug("Reel: gate: reject (hygiene: title matches exclude pattern \"{0}\") — \"{1}\"", pattern, details.Title);
                return GateDecision.Reject("hygiene", $"title matches exclude pattern \"{pattern}\"");
            }
        }

        // -- gate 2: artist containment (approved extension) -------------------
        var channelNorm = GateNormalize(details.Channel);
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

        var artistSeen = artistNorms.Any(v =>
            NeedsCheck.ContainsNormalized(videoNorm, v) || NeedsCheck.ContainsNormalized(channelNorm, v));

        if (!artistSeen)
        {
            _logger.Debug("Reel: gate: reject (artist: \"{0}\" not in title or channel) — title \"{1}\", channel \"{2}\"",
                string.Join("/", artistNorms.Distinct()), details.Title, details.Channel);
            return GateDecision.Reject("artist", "artist not found in title or channel");
        }

        // -- gate 3: song-title containment (wrong-song guard) ------------------
        var songNorm = NeedsCheck.Normalize(songTitle);
        if (songNorm.Length == 0)
        {
            _logger.Debug("Reel: gate: reject (title: song has no title)");
            return GateDecision.Reject("title", "song has no title");
        }

        if (!NeedsCheck.ContainsNormalized(videoNorm, songNorm))
        {
            _logger.Debug("Reel: gate: reject (title: song title not in video title) — looking for \"{0}\" in \"{1}\"",
                songNorm, videoNorm);
            return GateDecision.Reject("title", $"song title \"{songNorm}\" not in video title");
        }

        // -- gate 4: duration (F-20) -------------------------------------------
        if (songDurationSeconds <= 0)
        {
            _logger.Debug("Reel: gate: reject (duration: audio track has no duration)");
            return GateDecision.Reject("duration", "audio track has no duration");
        }

        if (details.DurationSeconds <= 0)
        {
            _logger.Debug("Reel: gate: reject (duration: video duration unknown)");
            return GateDecision.Reject("duration", "video duration unknown");
        }

        var tolerance = Math.Max(1, Config.DurationTolerancePercent);
        var delta = Math.Abs(details.DurationSeconds - songDurationSeconds);
        var pct = delta * 100.0 / songDurationSeconds;
        if (pct > tolerance)
        {
            _logger.Debug("Reel: gate: reject (duration: {0}s vs audio {1}s = {2:0.#}% > {3}%)",
                details.DurationSeconds, songDurationSeconds, pct, tolerance);
            return GateDecision.Reject("duration",
                $"video {details.DurationSeconds}s vs audio {songDurationSeconds}s = {pct:0.#}% > {tolerance}%",
                details.DurationSeconds);
        }

        _logger.Debug("Reel: gate: passed for \"{0}\" ({1}s vs audio {2}s, {3:0.#}%)",
            details.Title, details.DurationSeconds, songDurationSeconds, pct);
        return GateDecision.Pass(details.DurationSeconds);
    }
}

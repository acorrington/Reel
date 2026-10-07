using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Logging;
using Reel.Configuration;

namespace Reel.Services;

/// <summary>Outcome of processing one song.</summary>
public sealed class PipelineResult
{
    public bool Success { get; private set; }
    public bool Skipped { get; private set; }
    public string Detail { get; private set; }

    public static PipelineResult Ok(string detail) => new PipelineResult { Success = true, Detail = detail };
    public static PipelineResult Skip(string detail) => new PipelineResult { Skipped = true, Detail = detail };
    public static PipelineResult Fail(string detail) => new PipelineResult { Detail = detail };
}

/// <summary>
/// Orchestrates the full per-song pipeline (REQUIREMENTS.md §4):
///
///   NeedsCheck (F-30) → satisfied? skip
///   Candidates: IMVDb (F-10) → InnerTube search (F-11), iterated in order (F-12)
///   Confidence gate per candidate (F-20/21/22) — reject logs reason + videoId, next candidate
///   Resolve stream (Trawler chain: ANDROID progressive → IOS → ANDROID_VR → HTML, F-23)
///   Download to local temp → ffmpeg merge only if adaptive → move to TargetFolder (F-43/40)
///   ILibraryMonitor report → RefreshMetadata → best-effort artist link (F-41/42)
///
/// Never throws except on cancellation (E-02/E-05); bounded to 2 concurrent downloads (N-03).
/// One instance per run (the ImvdbClient keeps its warn-once state and cache per run).
/// </summary>
public sealed class ReelPipeline
{
    private const int MaxCandidateAttempts = 8;

    /// <summary>Bounded concurrency across all triggers (N-03): at most 2 downloads at once.</summary>
    private static readonly SemaphoreSlim Gate = new(2, 2);

    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IFfmpegManager _ffmpegManager;
    private readonly ILogger _logger;
    private readonly YouTubeService _youtube;
    private readonly DownloadMerger _merger;
    private readonly ImvdbClient _imvdb;
    private readonly MatchGate _matchGate;
    private readonly Linker _linker;

    /// <summary>Shared needs-check snapshot — the task refreshes it once per run and the
    /// pipeline extends it after each install (E-08 intra-run dedup).</summary>
    public NeedsCheck NeedsCheck { get; }

    public ReelPipeline(ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, IFfmpegManager ffmpegManager, ILogger logger)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _ffmpegManager = ffmpegManager;
        _logger = logger;
        _youtube = new YouTubeService(logger);
        _merger = new DownloadMerger(logger);
        _imvdb = new ImvdbClient(logger);
        _matchGate = new MatchGate(logger);
        _linker = new Linker(libraryManager, logger);
        NeedsCheck = new NeedsCheck(libraryManager, logger);
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    // ------------------------------------------------------------------ one candidate

    private sealed class Candidate
    {
        public string VideoId;
        public string Source;         // "imvdb" | "search"
        public string SearchTitle;    // null for IMVDb candidates (no title known yet)
        public long SearchDuration;   // seconds; 0 = not shown
    }

    // ------------------------------------------------------------------ main pipeline

    /// <summary>Run the full pipeline for one song. Never throws except on cancellation.</summary>
    public async Task<PipelineResult> ProcessAsync(Audio song, CancellationToken ct)
    {
        var title = song?.Name;
        if (string.IsNullOrWhiteSpace(title))
        {
            return PipelineResult.Skip("song has no title");
        }

        var artists = song.Artists != null && song.Artists.Length > 0
            ? song.Artists
            : song.AlbumArtists;
        var artistList = (artists ?? Array.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToList();
        if (artistList.Count == 0)
        {
            // The artist gate can't verify anything without an artist (F-22: doubt → skip).
            return PipelineResult.Skip("song has no artist tag");
        }

        var config = Config;
        var targetFolder = (config.TargetFolder ?? string.Empty).Trim();
        if (targetFolder.Length == 0)
        {
            return PipelineResult.Fail("TargetFolder is not configured (dashboard → Plugins → Reel)");
        }

        var primaryArtist = artistList[0];
        var songSeconds = (song.RunTimeTicks ?? 0) / TimeSpan.TicksPerSecond;
        var outputFileName = NeedsCheck.BuildFileName(primaryArtist, title);
        var savePath = Path.Combine(targetFolder, outputFileName);

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate: another trigger may have won the race (E-06).
            if (NeedsCheck.IsSatisfied(artistList, title))
            {
                return PipelineResult.Skip("already has a music video");
            }

            if (File.Exists(savePath))
            {
                return PipelineResult.Skip("output file already exists: " + outputFileName);
            }

            var candidates = await CollectCandidatesAsync(primaryArtist, title, config, ct).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                return PipelineResult.Fail("no candidates (IMVDb had no entry and YouTube search returned nothing)");
            }

            var attempted = 0;
            foreach (var candidate in candidates)
            {
                if (attempted >= MaxCandidateAttempts)
                {
                    break;
                }

                attempted++;
                ct.ThrowIfCancellationRequested();

                // Cheap pre-gate on the search result's own metadata — saves a player request
                // for obviously-bad candidates (F-22: still logged with reason + videoId).
                if (candidate.SearchTitle != null)
                {
                    var pre = _matchGate.PreCheck(title, songSeconds, candidate.SearchTitle, candidate.SearchDuration);
                    if (!pre.Passed)
                    {
                        LogReject(artistList, title, candidate, pre);
                        continue;
                    }
                }

                // Authoritative metadata + full confidence gate.
                var details = await _youtube.GetVideoDetailsAsync(candidate.VideoId, ct).ConfigureAwait(false);
                var decision = _matchGate.Evaluate(artistList, title, songSeconds, details);
                if (!decision.Passed)
                {
                    LogReject(artistList, title, candidate, decision);
                    continue;
                }

                var result = await TryDownloadInstallAsync(artistList, primaryArtist, title, savePath, candidate, config, ct).ConfigureAwait(false);
                if (result != null)
                {
                    return result;
                }

                // candidate failed at download/merge stage — try the next one (E-05)
            }

            if (attempted == 0)
            {
                return PipelineResult.Fail("no candidates (IMVDb had no entry and YouTube search returned nothing)");
            }

            return PipelineResult.Fail($"all {attempted} candidate(s) rejected or failed to download — see debug log for per-candidate reasons");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.ErrorException($"Reel: unexpected error processing {primaryArtist} – {title}", ex, Array.Empty<object>());
            return PipelineResult.Fail("unexpected error: " + ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    private void LogReject(IReadOnlyList<string> artists, string title, Candidate candidate, GateDecision decision)
    {
        _logger.Debug("Reel: {0} – {1}: reject {2} (from {3}) — {4}: {5}",
            artists[0], title, candidate.VideoId, candidate.Source, decision.Gate, decision.Reason);
    }

    // ------------------------------------------------------------------ candidates (F-10, F-11, F-12)

    private async Task<List<Candidate>> CollectCandidatesAsync(string artist, string title, PluginConfiguration config, CancellationToken ct)
    {
        var candidates = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // F-10: IMVDb first — its referenced YouTube video is the primary candidate.
        foreach (var videoId in await _imvdb.SearchVideoIdsAsync(artist, title, config.ImvdbApiKey, ct).ConfigureAwait(false))
        {
            if (seen.Add(videoId))
            {
                candidates.Add(new Candidate { VideoId = videoId, Source = "imvdb" });
            }
        }

        // F-11: YouTube search — appended after IMVDb's candidates (IMVDb-first ordering per
        // F-12: first candidate passing the gate wins). Also covers "IMVDb had no entry".
        foreach (var hit in await _youtube.SearchMusicVideoIdsAsync(artist, title, config.MaxSearchResults, ct).ConfigureAwait(false))
        {
            if (seen.Add(hit.VideoId))
            {
                candidates.Add(new Candidate
                {
                    VideoId = hit.VideoId,
                    Source = "search",
                    SearchTitle = hit.Title,
                    SearchDuration = hit.DurationSeconds
                });
            }
        }

        return candidates;
    }

    // ------------------------------------------------------------------ download + install + link (F-23, F-40..F-43)

    /// <summary>Resolve → download → merge → install → refresh → link one gated candidate.
    /// Returns null to try the next candidate, Ok/Fail to finish the song.</summary>
    private async Task<PipelineResult> TryDownloadInstallAsync(
        IReadOnlyList<string> artists,
        string primaryArtist,
        string title,
        string savePath,
        Candidate candidate,
        PluginConfiguration config,
        CancellationToken ct)
    {
        var videoId = candidate.VideoId;

        StreamUrls streams;
        try
        {
            streams = await _youtube.ResolveAsync(videoId, config.MaxVideoHeight, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("Reel: stream resolution threw for {0} ({1} – {2}): {3}", videoId, primaryArtist, title, ex.Message);
            return null;
        }

        if (streams == null)
        {
            _logger.Warn("Reel: no downloadable stream for {0} ({1} – {2}) — trying next candidate", videoId, primaryArtist, title);
            return null;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "Reel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var videoFile = Path.Combine(tempDir, "video.part");
            if (!await DownloadWithRetryAsync(streams.VideoUrl, videoFile, streams.DownloadUserAgent, ct).ConfigureAwait(false))
            {
                _logger.Warn("Reel: video download failed for {0} ({1} – {2})", videoId, primaryArtist, title);
                return null;
            }

            string finalTempFile;
            if (streams.IsProgressive)
            {
                // F-23: progressive preferred — single file, no merge (no range-cap risk).
                finalTempFile = videoFile;
            }
            else
            {
                var audioFile = Path.Combine(tempDir, "audio.part");
                if (!await DownloadWithRetryAsync(streams.AudioUrl, audioFile, streams.DownloadUserAgent, ct).ConfigureAwait(false))
                {
                    _logger.Warn("Reel: audio download failed for {0} ({1} – {2})", videoId, primaryArtist, title);
                    return null;
                }

                var ffmpegPath = ResolveFfmpegPath(config);
                if (ffmpegPath == null)
                {
                    _logger.Error("Reel: ffmpeg not found — cannot merge adaptive streams for {0} – {1}. Set an ffmpeg path in the plugin config.", primaryArtist, title);
                    return PipelineResult.Fail("ffmpeg not found");
                }

                var mergedFile = Path.Combine(tempDir, "merged.mp4");
                if (!await _merger.MergeAsync(videoFile, audioFile, mergedFile, ffmpegPath, ct).ConfigureAwait(false))
                {
                    _logger.Warn("Reel: ffmpeg merge failed for {0} ({1} – {2})", videoId, primaryArtist, title);
                    return null;
                }

                finalTempFile = mergedFile;
            }

            // --- install (F-43: cross-volume safe move; F-41: monitor report) ---
            _libraryMonitor.ReportFileSystemChangeBeginning(savePath);
            try
            {
                try
                {
                    File.Move(finalTempFile, savePath, overwrite: true);
                }
                catch (IOException)
                {
                    // Cross-volume move (temp on C:, library on E:) — copy then delete.
                    File.Copy(finalTempFile, savePath, true);
                    try
                    {
                        File.Delete(finalTempFile);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug("Reel: could not delete temp file {0}: {1}", finalTempFile, ex.Message);
                    }
                }

                _logger.Info("Reel: installed {0} from {1} ({2}, {3}p{4})",
                    Path.GetFileName(savePath), videoId, streams.ResolvedBy, streams.Height,
                    streams.IsProgressive ? ", progressive" : ", merged");
            }
            finally
            {
                _libraryMonitor.ReportFileSystemChangeComplete(savePath, true);
            }

            // Intra-run dedup: later songs with the same (artist, title) now skip (E-08).
            NeedsCheck.MarkInstalled(primaryArtist, title);

            // --- refresh + best-effort artist link (F-41, F-42) ---
            var musicVideo = await WaitForMusicVideoAsync(savePath, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (musicVideo == null)
            {
                _logger.Info("Reel: {0} installed but Emby has not indexed the item yet — artist link skipped; the next run or a manual metadata refresh will link it", Path.GetFileName(savePath));
            }
            else
            {
                try
                {
                    await musicVideo.RefreshMetadata(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warn("Reel: metadata refresh failed for {0}: {1}", musicVideo.Name, ex.Message);
                }

                _linker.TryLink(musicVideo, artists, ct);
            }

            return PipelineResult.Ok($"downloaded music video for {primaryArtist} – {title} from {videoId} ({streams.ResolvedBy}, {streams.Height}p{(streams.IsProgressive ? ", progressive" : "")})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.ErrorException($"Reel: candidate {videoId} failed for {primaryArtist} – {title}", ex, Array.Empty<object>());
            return null;
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // best-effort cleanup (E-07 also sweeps stale dirs at run start)
            }
        }
    }

    /// <summary>E-05: one retry, then give up on this candidate.</summary>
    private async Task<bool> DownloadWithRetryAsync(string url, string destination, string userAgent, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                if (await _merger.DownloadToFileAsync(url, destination, userAgent, ct).ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn("Reel: download attempt {0} failed: {1}", attempt, ex.Message);
            }

            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }

        return false;
    }

    /// <summary>Poll ILibraryManager.FindByPath until Emby indexes the new file as a MusicVideo
    /// (the library monitor reacts asynchronously). Returns null after the timeout.</summary>
    private async Task<MusicVideo> WaitForMusicVideoAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var item = _libraryManager.FindByPath(path, false);
            if (item is MusicVideo musicVideo)
            {
                return musicVideo;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }

        return null;
    }

    // ------------------------------------------------------------------ ffmpeg (same resolution order as Trawler)

    private string ResolveFfmpegPath(PluginConfiguration config)
    {
        var overridePath = config?.FfmpegPathOverride;
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        try
        {
            var encoderPath = _ffmpegManager?.FfmpegConfiguration?.EncoderPath;
            if (!string.IsNullOrWhiteSpace(encoderPath) && File.Exists(encoderPath))
            {
                return encoderPath;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug("Reel: IFfmpegManager.EncoderPath unavailable: {0}", ex.Message);
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var exe = Path.Combine(dir.Trim(), "ffmpeg.exe");
            if (File.Exists(exe))
            {
                return exe;
            }

            exe = Path.Combine(dir.Trim(), "ffmpeg");
            if (File.Exists(exe))
            {
                return exe;
            }
        }

        return null;
    }
}

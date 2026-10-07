using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using Reel.Services;

namespace Reel.Tasks;

/// <summary>
/// Scheduled task (F-01): scans the music library for Audio items whose song has no matching
/// MusicVideo item and downloads videos through <see cref="ReelPipeline"/>. Manual "Run Now"
/// from the dashboard is the only trigger in v1 (F-02); a daily 04:00 trigger is the
/// self-heal safety net (E-07). <c>EnableScheduledScan</c> is the master switch (F-50) and
/// <c>MaxVideosPerRun</c> caps installs per run (F-03).
///
/// After the download loop, a post-pass waits for Emby to index the freshly installed files
/// (LibraryMonitorDelaySeconds = 90 s debounce) and then refreshes + artist-links them
/// (F-41/F-42); anything not indexed in time is linked by the self-heal pass on the next run.
/// </summary>
public class DownloadMissingMusicVideosTask : IScheduledTask
{
    /// <summary>How long the post-pass waits for Emby to index this run's installs (F-42).</summary>
    private static readonly TimeSpan LinkWaitTimeout = TimeSpan.FromSeconds(160);

    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IFfmpegManager _ffmpegManager;
    private readonly ILogger _logger;

    public DownloadMissingMusicVideosTask(
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        IFfmpegManager ffmpegManager,
        ILogger logger)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _ffmpegManager = ffmpegManager;
        _logger = logger;
    }

    public string Key => "DownloadMissingMusicVideos";

    public string Category => "Reel";

    public string Description =>
        "Scans your music library for songs without a music video and downloads matching videos from YouTube.";

    public string Name => "Download Missing Music Videos";

    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            _logger.Warn("Reel: plugin configuration unavailable — task aborted");
            return;
        }

        if (!config.EnableScheduledScan)
        {
            _logger.Info("Reel: task skipped — EnableScheduledScan is off (dashboard → Plugins → Reel)");
            await SaveSummaryAsync($"{DateTime.Now:g}: skipped (EnableScheduledScan is off)").ConfigureAwait(false);
            return;
        }

        var targetFolder = (config.TargetFolder ?? string.Empty).Trim();
        if (targetFolder.Length == 0)
        {
            _logger.Warn("Reel: task aborted — TargetFolder is not configured (dashboard → Plugins → Reel)");
            await SaveSummaryAsync($"{DateTime.Now:g}: aborted (TargetFolder not configured)").ConfigureAwait(false);
            return;
        }

        SweepStaleTemp();
        RelinkUnlinked(targetFolder, cancellationToken);

        var pipeline = new ReelPipeline(_libraryManager, _libraryMonitor, _ffmpegManager, _logger);
        pipeline.NeedsCheck.Refresh(targetFolder);

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IsVirtualItem = false,
            IncludeItemTypes = new[] { nameof(Audio) }
        });

        var total = items.Length;
        _logger.Info("Reel: scheduled task scanning {0} song(s); target folder {1}", total, targetFolder);

        var done = 0;
        var downloaded = 0;
        var skipped = 0;
        var failed = 0;
        var capped = false;

        // Install paths to link once Emby indexes them (F-42 post-pass).
        var pendingLinks = new List<(string Path, string[] Artists)>();

        // E-08: never process the same (artist, title) twice in one run, even when the same
        // song appears under multiple albums.
        var processedKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!(item is Audio song))
            {
                done++;
                continue;
            }

            // F-03: cap installs to the item — stop as soon as MaxVideosPerRun are installed.
            if (downloaded >= config.MaxVideosPerRun)
            {
                capped = true;
                _logger.Info("Reel: MaxVideosPerRun ({0}) reached — stopping the run", config.MaxVideosPerRun);
                break;
            }

            var songArtists = (song.Artists != null && song.Artists.Length > 0 ? song.Artists : song.AlbumArtists)
                              ?? Array.Empty<string>();
            var key = NeedsCheck.Normalize(string.Join(" ", songArtists))
                      + '\u001f' + NeedsCheck.Normalize(song.Name);

            if (!processedKeys.Add(key))
            {
                skipped++;
                _logger.Debug("Reel: {0}: duplicate (artist, title) already handled this run", song.Name);
                done++;
                progress.Report(done * 100.0 / Math.Max(1, total));
                continue;
            }

            try
            {
                var result = await pipeline.ProcessAsync(song, cancellationToken).ConfigureAwait(false);
                if (result.Success)
                {
                    downloaded++;
                    _logger.Info("Reel: {0}: {1}", song.Name, result.Detail);

                    var primary = songArtists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
                    if (!string.IsNullOrWhiteSpace(primary))
                    {
                        pendingLinks.Add((Path.Combine(targetFolder, NeedsCheck.BuildFileName(primary, song.Name)), songArtists));
                    }
                }
                else if (result.Skipped)
                {
                    skipped++;
                    _logger.Debug("Reel: {0}: skipped ({1})", song.Name, result.Detail);
                }
                else
                {
                    failed++;
                    _logger.Warn("Reel: {0}: {1}", song.Name, result.Detail);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The whole per-song pipeline is wrapped: Emby's task loop never sees an exception.
                failed++;
                _logger.ErrorException($"Reel: {song.Name}", ex, Array.Empty<object>());
            }

            done++;
            progress.Report(done * 100.0 / Math.Max(1, total));
        }

        // F-41/F-42: wait for indexing, then refresh + artist-link the new music videos.
        var unlinked = await LinkPendingAsync(pendingLinks, cancellationToken).ConfigureAwait(false);

        var summary = $"{DateTime.Now:g}: {total} songs, {downloaded} downloaded, {skipped} skipped, {failed} failed"
                      + (capped ? $" (stopped at MaxVideosPerRun={config.MaxVideosPerRun})" : string.Empty)
                      + (unlinked > 0 ? $" ({unlinked} awaiting artist link — self-heals next run)" : string.Empty);
        _logger.Info("Reel: scheduled task finished — {0}", summary);
        await SaveSummaryAsync(summary).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ artist linking (F-42)

    /// <summary>
    /// Post-pass: poll until Emby indexes this run's installs (90 s monitor debounce), then
    /// RefreshMetadata + best-effort artist link each one (F-41). Returns how many were never
    /// indexed within the timeout (left for the next run's self-heal pass).
    /// </summary>
    private async Task<int> LinkPendingAsync(List<(string Path, string[] Artists)> pending, CancellationToken ct)
    {
        if (pending.Count == 0)
        {
            return 0;
        }

        var linker = new Linker(_libraryManager, _logger);
        var deadline = DateTime.UtcNow + LinkWaitTimeout;

        while (pending.Count > 0 && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var entry = pending[i];
                var item = _libraryManager.FindByPath(entry.Path, false);
                if (!(item is MusicVideo musicVideo))
                {
                    continue;
                }

                pending.RemoveAt(i);

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

                linker.TryLink(musicVideo, entry.Artists, ct);
            }

            if (pending.Count > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }

        foreach (var entry in pending)
        {
            _logger.Info("Reel: {0} not indexed within {1}s — the next run's self-heal pass will link it",
                Path.GetFileName(entry.Path), (int)LinkWaitTimeout.TotalSeconds);
        }

        return pending.Count;
    }

    /// <summary>Self-heal (runs at task start): link music videos in the target folder that
    /// still have no artist link — covers files whose post-pass timed out on a previous run
    /// and lets F-42 degrade to a logged, self-healing state instead of silence.</summary>
    private void RelinkUnlinked(string targetFolder, CancellationToken ct)
    {
        try
        {
            var linker = new Linker(_libraryManager, _logger);
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IsVirtualItem = false,
                IncludeItemTypes = new[] { nameof(MusicVideo) }
            });

            var linked = 0;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();

                if (!(item is MusicVideo mv)
                    || (mv.ArtistItems != null && mv.ArtistItems.Length > 0)
                    || string.IsNullOrEmpty(mv.Path)
                    || !mv.Path.StartsWith(targetFolder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var baseName = Path.GetFileNameWithoutExtension(mv.Path);
                var idx = baseName.IndexOf(" - ", StringComparison.Ordinal);
                if (idx <= 0)
                {
                    continue;
                }

                if (linker.TryLink(mv, new[] { baseName.Substring(0, idx) }, ct))
                {
                    linked++;
                }
            }

            if (linked > 0)
            {
                _logger.Info("Reel: self-heal linked {0} music video(s) that had no artist", linked);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug("Reel: self-heal link pass failed: {0}", ex.Message);
        }
    }

    // ------------------------------------------------------------------ reporting & temp (F-57, E-07)

    /// <summary>F-57: persist the run summary for the config page (same as Trawler).</summary>
    private async Task SaveSummaryAsync(string summary)
    {
        try
        {
            var plugin = Plugin.Instance;
            if (plugin != null)
            {
                plugin.Configuration.LastRunSummary = summary;
                plugin.SaveConfiguration();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("Reel: could not persist last-run summary: {0}", ex);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>E-07: remove partial temp dirs from runs killed mid-download (older than 1 h).</summary>
    private void SweepStaleTemp()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "Reel");
            if (!Directory.Exists(root))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var dir in Directory.GetDirectories(root))
            {
                try
                {
                    if (Directory.GetCreationTimeUtc(dir) < cutoff)
                    {
                        Directory.Delete(dir, true);
                        _logger.Info("Reel: removed stale temp directory {0}", dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug("Reel: could not remove temp dir {0}: {1}", dir, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug("Reel: temp sweep failed: {0}", ex.Message);
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Daily self-heal at 04:00 (same pattern as Trawler): catches anything missed since
        // the last manual run. EnableScheduledScan gates execution inside Execute (F-50).
        return new List<TaskTriggerInfo>
        {
            new TaskTriggerInfo
            {
                Type = "DailyTrigger",
                TimeOfDayTicks = 4 * TimeSpan.TicksPerHour
            }
        };
    }
}

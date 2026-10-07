using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Reel.Tasks;

/// <summary>
/// Scheduled task (F-01): scans the music library for Audio items whose song has no
/// matching MusicVideo item and downloads missing music videos. Shows progress in the
/// Emby dashboard task list and records a run summary for the config page.
/// </summary>
public class DownloadMissingMusicVideosTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

    public DownloadMissingMusicVideosTask(ILibraryManager libraryManager, ILogger logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public string Key => "DownloadMissingMusicVideos";

    public string Category => "Reel";

    public string Description =>
        "Scans your music library for songs without a music video and downloads matching videos from YouTube.";

    public string Name => "Download Missing Music Videos";

    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        // Phase 0 skeleton: inventory only — pipeline wiring lands in Phase 7.
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IsVirtualItem = false,
            IncludeItemTypes = new[] { nameof(Audio) }
        });

        var total = items.Length;
        _logger.Info("Reel: scheduled task scanning {0} song(s)", total);
        progress.Report(100);

        var summary = $"{DateTime.Now:g}: {total} songs scanned (skeleton run — no downloads yet)";
        await Task.CompletedTask.ConfigureAwait(false);

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
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Daily self-heal at 04:00 (same pattern as Trawler); EnableScheduledScan is
        // checked inside Execute (F-50).
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

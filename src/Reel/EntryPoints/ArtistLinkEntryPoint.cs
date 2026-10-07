using System;
using System.IO;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using Reel.Services;

namespace Reel.EntryPoints;

/// <summary>
/// Auto-links artists on music video updates (F-42 hardening).
///
/// Emby's delayed LibraryMonitor refresh (~90 s) can overwrite the artist links Reel sets
/// right after a download: the refresh pipeline saves the item again with empty links. This
/// listener closes that race — whenever a MusicVideo under TargetFolder is saved without
/// artist links, the artist is parsed from Reel's "{Artist} - {Title}" filename convention
/// and re-linked immediately, instead of waiting for the next run's self-heal pass.
///
/// Loop safety: our own UpdateToRepository also fires ItemUpdated, but by then ArtistItems
/// is populated, so the handler returns without acting — one bounce maximum, then quiet.
/// The handler must never throw into Emby's event loop (fully wrapped, like Trawler's).
/// </summary>
public class ArtistLinkEntryPoint : IServerEntryPoint, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;
    private readonly Linker _linker;

    private volatile bool _disposed;

    public ArtistLinkEntryPoint(ILibraryManager libraryManager, ILogger logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _linker = new Linker(libraryManager, logger);
    }

    public void Run()
    {
        _libraryManager.ItemUpdated += OnItemUpdated;
        _logger.Info("Reel: artist-link listener started (auto-link on item updates)");
    }

    private void OnItemUpdated(object sender, ItemChangeEventArgs e)
    {
        try
        {
            if (_disposed)
            {
                return;
            }

            if (!(e.Item is MusicVideo musicVideo))
            {
                return;
            }

            // Already linked → nothing to do (also the loop guard for our own saves).
            if (musicVideo.ArtistItems != null && musicVideo.ArtistItems.Length > 0)
            {
                return;
            }

            var targetFolder = (Plugin.Instance?.Configuration?.TargetFolder ?? string.Empty).Trim();
            if (targetFolder.Length == 0
                || string.IsNullOrEmpty(musicVideo.Path)
                || !musicVideo.Path.StartsWith(targetFolder, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Reel's install convention: "{Artist} - {Title}.mp4"
            var baseName = Path.GetFileNameWithoutExtension(musicVideo.Path);
            var idx = baseName.IndexOf(" - ", StringComparison.Ordinal);
            if (idx <= 0)
            {
                return;
            }

            var artist = baseName.Substring(0, idx);
            if (_linker.TryLink(musicVideo, new[] { artist }, CancellationToken.None))
            {
                _logger.Info("Reel: linked artist(s) on music video \"{0}\" (via ItemUpdated)", musicVideo.Name);
            }
        }
        catch (Exception ex)
        {
            // The event handler itself must never throw into Emby's event loop.
            _logger.ErrorException("Reel: ItemUpdated handler failed", ex, Array.Empty<object>());
        }
    }

    public void Dispose()
    {
        _disposed = true;

        // Symmetric with Run(): exactly the subscription made there is removed here.
        _libraryManager.ItemUpdated -= OnItemUpdated;
    }
}

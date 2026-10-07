using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;

namespace Reel.Services;

/// <summary>
/// Best-effort artist linking (F-42): after a MusicVideo item exists, set its artist links
/// from the song's artists so the video appears on the artist's page.
///
/// Verified API path on Emby 4.10.1 (tools/EmbyLinkProbe):
///   BaseItem.UpdateToRepository(ItemUpdateType)  — persists property edits in-process
///   ILibraryManager.GetItemList(InternalItemsQuery { IncludeItemTypes=["Artist"], Name })
/// resolves artist names to library Artist items. When any step fails, we log and leave the
/// documented manual step — linking is "best effort" per spec (F-42, §8).
/// </summary>
public sealed class Linker
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

    public Linker(ILibraryManager libraryManager, ILogger logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Set Artists + ArtistItems on the MusicVideo and persist. Returns false on any
    /// failure (caller logs context; never throws except cancellation).</summary>
    public bool TryLink(MusicVideo musicVideo, IReadOnlyList<string> artists, System.Threading.CancellationToken ct)
    {
        if (musicVideo == null || artists == null || artists.Count == 0)
        {
            return false;
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            var names = artists
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (names.Count == 0)
            {
                return false;
            }

            var links = new List<LinkedItemInfo>();
            foreach (var name in names)
            {
                var artistItem = FindArtist(name);
                if (artistItem != null)
                {
                    // LinkedItemInfo.Id is the internal (long) item id
                    links.Add(new LinkedItemInfo { Id = artistItem.InternalId, Name = artistItem.Name ?? name });
                }
                else
                {
                    _logger.Debug("Reel: linker: no Artist item named \"{0}\" in the library (name-only link)", name);
                }
            }

            musicVideo.Artists = names.ToArray();
            if (links.Count > 0)
            {
                musicVideo.ArtistItems = links.ToArray();
            }

            musicVideo.UpdateToRepository(ItemUpdateType.MetadataDownload);

            _logger.Info("Reel: linked artist(s) on music video \"{0}\": {1}{2}",
                musicVideo.Name,
                string.Join(", ", names),
                links.Count > 0 ? " (resolved to artist items)" : " (names only — no matching artist items)");

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("Reel: could not link artists on \"{0}\": {1} — link manually on the artist page if needed (see README)",
                musicVideo.Name, ex.Message);
            return false;
        }
    }

    private BaseItem FindArtist(string name)
    {
        try
        {
            return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IsVirtualItem = false,
                IncludeItemTypes = new[] { "Artist" },
                Name = name
            }).FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.Debug("Reel: linker: artist lookup for \"{0}\" failed: {1}", name, ex.Message);
            return null;
        }
    }
}

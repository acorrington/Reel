using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace Reel.Services;

/// <summary>
/// Song ↔ MusicVideo matching (F-30..F-32, E-08).
///
/// A song counts as "satisfied" when a MusicVideo item exists whose normalized title equals
/// the song's normalized title AND that shares an artist (artist name or artist link), or
/// when the corresponding output file already exists in the target folder (file + item are
/// both checked, mirroring Trawler's F-02). Keys are (artist, title) so the same song under
/// multiple albums is downloaded once (E-08).
///
/// One instance is refreshed once per run and updated after each install, so intra-run
/// duplicates are caught without re-querying the library.
/// </summary>
public sealed class NeedsCheck
{
    private const char KeySep = '\u001f';

    private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".webm", ".m4v", ".mov" };

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

    /// <summary>(artist, title) pairs, both normalized.</summary>
    private readonly HashSet<string> _pairs = new(StringComparer.Ordinal);

    /// <summary>Flat "artist title" keys (normalization collapses " - " to a space, so this is
    /// how a parsed file name and a parsed song compare).</summary>
    private readonly HashSet<string> _flats = new(StringComparer.Ordinal);

    /// <summary>Titles of MVs/files whose artist could not be determined (used only when the
    /// song itself has no artist tag — never as a cross-artist match).</summary>
    private readonly HashSet<string> _titleOnly = new(StringComparer.Ordinal);

    public NeedsCheck(ILibraryManager libraryManager, ILogger logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    // ------------------------------------------------------------------ normalization (F-31)

    /// <summary>
    /// Case-, punctuation- and credit-insensitive normalization: drops bracketed suffixes
    /// ((Live)/(Remastered)/[2011 Remaster]…), feat./ft./with clauses, and all punctuation.
    /// Applied identically to both sides of every comparison.
    /// </summary>
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var t = value.Trim().ToLowerInvariant();

        // bracketed suffixes / markers / credits
        t = Regex.Replace(t, @"\([^)]*\)|\[[^\]]*\]", " ");

        // feat./ft./featuring clauses — drop the collaborator tail entirely
        t = Regex.Replace(t, @"\s*(?:feat\.?|ft\.?|featuring)\s+.*$", " ");

        // "with" clauses — only when preceded by something (keeps titles that *start* with With…)
        t = Regex.Replace(t, @"\s+with\s+.*$", " ");

        // Version/mix qualifiers after " - " (F-31): "… - From X Soundtrack", "… - Single Version",
        // "… - 2015 Remaster", "… - Radio Edit", "… - Promo 7 Edit", "… - 7 Version" name the same
        // song; stripping them lets the title gate and needs-check match the plain video title.
        // Applied before punctuation collapse so the dash separator is still visible.
        t = Regex.Replace(
            t,
            @"\s+[-–—]\s+(?:.*\bfrom\b.*|.*remaster.*|single\b.*|radio\s+edit\b.*|promo\b.*|.*\bversion\b.*)$",
            " ");

        // everything that isn't a letter or digit becomes a separator
        t = Regex.Replace(t, @"[^\p{L}\p{Nd}]+", " ");

        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    /// <summary>True when normalized <paramref name="haystack"/> contains normalized
    /// <paramref name="needle"/> as a whole-word phrase (used by the artist-containment gate).</summary>
    public static bool ContainsNormalized(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle) || string.IsNullOrEmpty(haystack))
        {
            return false;
        }

        return (" " + haystack + " ").Contains(" " + needle + " ", StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ snapshot

    /// <summary>Load every MusicVideo item and every video file in the target folder.
    /// Call once at the start of a run.</summary>
    public void Refresh(string targetFolder)
    {
        _pairs.Clear();
        _flats.Clear();
        _titleOnly.Clear();

        BaseItem[] musicVideos;
        try
        {
            musicVideos = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IsVirtualItem = false,
                IncludeItemTypes = new[] { nameof(MusicVideo) }
            });
        }
        catch (Exception ex)
        {
            _logger.Warn("Reel: needs-check could not query MusicVideo items: {0}", ex.Message);
            musicVideos = Array.Empty<BaseItem>();
        }

        foreach (var item in musicVideos)
        {
            if (item is MusicVideo mv)
            {
                AddVideo(mv.Name, GetArtistNames(mv), "library item " + mv.Id);
            }
        }

        ScanTargetFolder(targetFolder);

        _logger.Debug("Reel: needs-check snapshot: {0} artist/title pair(s), {1} flat key(s) from {2} music video(s)",
            _pairs.Count, _flats.Count, musicVideos.Length);
    }

    /// <summary>Add keys for a freshly installed video so later songs in the same run skip it.</summary>
    public void MarkInstalled(string artist, string title)
    {
        AddVideo(title, new[] { artist }, "newly installed file");
    }

    /// <summary>Does a music video for (artist(s), title) already exist?</summary>
    public bool IsSatisfied(IReadOnlyList<string> artists, string title)
    {
        var normTitle = Normalize(title);
        if (normTitle.Length == 0)
        {
            return false;
        }

        var hasArtist = false;
        if (artists != null)
        {
            foreach (var artist in artists)
            {
                var normArtist = Normalize(artist);
                if (normArtist.Length == 0)
                {
                    continue;
                }

                hasArtist = true;
                if (_pairs.Contains(normArtist + KeySep + normTitle))
                {
                    _logger.Debug("Reel: needs-check: \"{0}\" ({1}) already has a music video", title, artist);
                    return true;
                }

                if (_flats.Contains(normArtist + " " + normTitle))
                {
                    _logger.Debug("Reel: needs-check: \"{0}\" ({1}) matched an installed file", title, artist);
                    return true;
                }
            }
        }

        if (!hasArtist && _titleOnly.Contains(normTitle))
        {
            // Neither side has artist information — fall back to title only (F-30: the MV
            // itself must have been artist-less for this branch to ever be populated).
            _logger.Debug("Reel: needs-check: \"{0}\" matched an artist-less music video", title);
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------------ naming (F-40)

    /// <summary>Build the output file name "{Artist} - {Title}.mp4" with illegal characters
    /// replaced (movie-style naming per Emby's Music Videos docs).</summary>
    public static string BuildFileName(string artist, string title)
    {
        var safeArtist = Sanitize(artist);
        var safeTitle = Sanitize(title);

        var name = safeArtist.Length > 0 && safeTitle.Length > 0
            ? safeArtist + " - " + safeTitle
            : (safeTitle.Length > 0 ? safeTitle : safeArtist);

        return name + ".mp4";
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(c => invalid.Contains(c) ? ' ' : c).ToArray();
        var s = Regex.Replace(new string(chars), @"\s+", " ").Trim();

        // Windows dislikes trailing dots/spaces
        return s.TrimEnd('.', ' ');
    }

    // ------------------------------------------------------------------ internals

    private void AddVideo(string title, IEnumerable<string> artists, string source)
    {
        var normTitle = Normalize(title);
        if (normTitle.Length == 0)
        {
            return;
        }

        var any = false;
        foreach (var artist in artists ?? Enumerable.Empty<string>())
        {
            var normArtist = Normalize(artist);
            if (normArtist.Length == 0)
            {
                continue;
            }

            any = true;
            _pairs.Add(normArtist + KeySep + normTitle);
            _flats.Add(normArtist + " " + normTitle);
        }

        if (!any)
        {
            _titleOnly.Add(normTitle);
            _logger.Debug("Reel: needs-check: {0} \"{1}\" has no artist — title-only key", source, title);
        }
    }

    /// <summary>Artists of an MV: string list first, artist item links second, file name last.</summary>
    private static IEnumerable<string> GetArtistNames(MusicVideo mv)
    {
        var names = new List<string>();

        if (mv.Artists != null)
        {
            names.AddRange(mv.Artists.Where(a => !string.IsNullOrWhiteSpace(a)));
        }

        if (names.Count == 0 && mv.ArtistItems != null)
        {
            foreach (var link in mv.ArtistItems)
            {
                if (link == null || string.IsNullOrWhiteSpace(link.Name))
                {
                    continue;
                }

                names.Add(link.Name);
            }
        }

        if (names.Count == 0 && !string.IsNullOrEmpty(mv.Path))
        {
            // Our own convention: {Artist} - {Title}.mp4
            var baseName = Path.GetFileNameWithoutExtension(mv.Path);
            var idx = baseName.IndexOf(" - ", StringComparison.Ordinal);
            if (idx > 0)
            {
                names.Add(baseName.Substring(0, idx));
            }
        }

        return names;
    }

    /// <summary>Parse installed file names with our "{Artist} - {Title}" convention.</summary>
    private void ScanTargetFolder(string targetFolder)
    {
        if (string.IsNullOrWhiteSpace(targetFolder) || !Directory.Exists(targetFolder))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(targetFolder))
            {
                var ext = Path.GetExtension(file);
                if (!VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var baseName = Path.GetFileNameWithoutExtension(file);
                var idx = baseName.IndexOf(" - ", StringComparison.Ordinal);
                if (idx > 0)
                {
                    AddVideo(baseName.Substring(idx + 3), new[] { baseName.Substring(0, idx) }, "file " + baseName);
                }
                else if (baseName.Length > 0)
                {
                    // no artist convention — title-only key
                    var normTitle = Normalize(baseName);
                    if (normTitle.Length > 0)
                    {
                        _titleOnly.Add(normTitle);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("Reel: needs-check could not scan target folder {0}: {1}", targetFolder, ex.Message);
        }
    }
}

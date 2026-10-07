using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Reel.Services;

/// <summary>
/// IMVDb discovery (F-10, F-13, E-01): given artist + title, returns the YouTube videoIds
/// for the song, in IMVDb's result order.
///
/// Two discovery paths, both against the documented API (imvdb.com/developers/api):
///
///   A. SEARCH (spec's primary): GET /api/v1/search/videos?q=… → results[] { id, song_title,
///      artists[] } — but note IMVDb's nginx edge-blocks ALL /api/v1/search/* paths (403 for
///      every IP and UA tested 2026-10-07, key or no key). When that happens the client
///      sticks to path B for the rest of the run (one Info log, no spam).
///
///   B. ARTIST LOOKUP (works today): slugify(artist) → GET /n/{slug} (artist page HTML,
///      unique "<strong>ID:</strong> {n}" footer → numeric entity id) →
///      GET /api/v1/entity/{id}?include=artist_videos → artist_videos.videos[] { id, song_title }
///      → title match → GET /api/v1/video/{id}?include=sources → youtube source_data.
///      Bad slug = clean 404 → give up for that artist (search-only for the song).
///
/// Resilience (E-01): network/5xx failures warn once per run and fall back to YouTube search;
/// a 403 on the entity/video endpoints (the key-enforcing ones) disables IMVDb for the run.
/// Entity ids and artist video lists are cached per run so each artist costs at most 2 HTTP
/// calls (IMVDb asks clients to cache).
///
/// One instance per run; attribution: video data © IMVDb (config page + README, F-13).
/// </summary>
public sealed class ImvdbClient
{
    private const string BaseUrl = "https://imvdb.com/api/v1";

    /// <summary>At most this many IMVDb candidates per song (search fallback covers the rest).</summary>
    private const int MaxCandidatesPerSong = 3;

    private static readonly HttpClient Http = CreateClient();

    private readonly ILogger _logger;
    private readonly Dictionary<string, List<string>> _resultCache = new(StringComparer.Ordinal);

    /// <summary>artistNorm → entity id (null = artist page not found).</summary>
    private readonly Dictionary<string, long?> _entityIdCache = new(StringComparer.Ordinal);

    /// <summary>artistNorm → (videoId, songTitle) list from artist_videos.</summary>
    private readonly Dictionary<string, List<(long Id, string Title)>> _artistVideosCache = new(StringComparer.Ordinal);

    private bool _keyRejected;
    private bool _failureWarned;
    private bool _searchBlocked;
    private bool _searchBlockedLogged;

    public ImvdbClient(ILogger logger)
    {
        _logger = logger;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Reel/1.0 (Emby plugin; https://imvdb.com)");
        return client;
    }

    /// <summary>Is IMVDb discovery enabled (a key is configured)?</summary>
    public static bool IsConfigured(string apiKey) => !string.IsNullOrWhiteSpace(apiKey);

    // ------------------------------------------------------------------ public API

    /// <summary>
    /// YouTube videoIds for (artist, title) via IMVDb — ordered, distinct.
    /// Never throws except on cancellation (E-01).
    /// </summary>
    public async Task<List<string>> SearchVideoIdsAsync(string artist, string title, string apiKey, CancellationToken ct)
    {
        var key = (apiKey ?? string.Empty).Trim();
        if (key.Length == 0 || _keyRejected)
        {
            return new List<string>();
        }

        var normArtist = NeedsCheck.Normalize(artist);
        var normTitle = NeedsCheck.Normalize(title);
        var cacheKey = normArtist + '\u001f' + normTitle;
        if (_resultCache.TryGetValue(cacheKey, out var cached))
        {
            return new List<string>(cached);
        }

        var ids = new List<string>();
        try
        {
            // Path A: documented search API (F-10 primary).
            if (!_searchBlocked)
            {
                await QueryViaSearchAsync(artist, title, key, ids, ct).ConfigureAwait(false);
            }

            // Path B: artist page → entity → artist_videos (survives the search edge-block).
            if (ids.Count == 0)
            {
                await QueryViaEntityAsync(normArtist, normTitle, key, ids, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            WarnFailureOnce("unreachable: " + ex.Message);
        }

        _resultCache[cacheKey] = ids;
        return new List<string>(ids);
    }

    // ------------------------------------------------------------------ path A: search

    private async Task QueryViaSearchAsync(string artist, string title, string key, List<string> ids, CancellationToken ct)
    {
        var query = Uri.EscapeDataString($"{artist} {title}".Trim());
        using var resp = await SendAsync($"{BaseUrl}/search/videos?q={query}&per_page=10", key, null, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode;
            if (code == 403 || code == 404)
            {
                // 2026-10-07: IMVDb's nginx blocks /api/v1/search/* for everyone (tested with
                // and without key, multiple UAs/IPs) — not a key problem. Fall back to the
                // artist-lookup path for the rest of this run.
                _searchBlocked = true;
                if (!_searchBlockedLogged)
                {
                    _searchBlockedLogged = true;
                    _logger.Info("Reel: IMVDb search endpoint unavailable (HTTP {0}) — using artist-lookup discovery for this run", code);
                }

                return;
            }

            WarnFailureOnce($"HTTP {code} on search");
            return;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var normTitle = NeedsCheck.Normalize(title);
        var normArtist = NeedsCheck.Normalize(artist);

        foreach (var entry in results.EnumerateArray())
        {
            if (ids.Count >= MaxCandidatesPerSong)
            {
                break;
            }

            // F-12 safety: only adopt entries that are plausibly THIS song (the confidence
            // gate re-verifies the actual YouTube video afterwards anyway).
            var entryTitle = NeedsCheck.Normalize(GetString(entry, "song_title"));
            if (!TitleLooksSame(entryTitle, normTitle) || !EntryHasArtist(entry, normArtist))
            {
                continue;
            }

            var imvdbId = GetInt64(entry, "id");
            if (imvdbId <= 0)
            {
                continue;
            }

            var videoId = await GetYouTubeIdAsync(imvdbId, key, ct).ConfigureAwait(false);
            if (videoId != null && !ids.Contains(videoId))
            {
                ids.Add(videoId);
            }
        }
    }

    // ------------------------------------------------------------------ path B: artist lookup

    private async Task QueryViaEntityAsync(string normArtist, string normTitle, string key, List<string> ids, CancellationToken ct)
    {
        if (normArtist.Length == 0 || normTitle.Length == 0)
        {
            return;
        }

        // The artist page is fetched at most once per artist per run; the video list once
        // per artist — afterwards every song by that artist is a pure in-memory match.
        var videos = _artistVideosCache.TryGetValue(normArtist, out var cachedVideos)
            ? cachedVideos
            : await LoadArtistVideosAsync(normArtist, key, ct).ConfigureAwait(false);

        if (videos == null || videos.Count == 0)
        {
            return;
        }

        foreach (var (videoDbId, songTitle) in videos)
        {
            if (ids.Count >= MaxCandidatesPerSong)
            {
                break;
            }

            if (!TitleLooksSame(NeedsCheck.Normalize(songTitle), normTitle))
            {
                continue;
            }

            var videoId = await GetYouTubeIdAsync(videoDbId, key, ct).ConfigureAwait(false);
            if (videoId != null && !ids.Contains(videoId))
            {
                ids.Add(videoId);
                _logger.Info("Reel: IMVDb: {0} → {1}", songTitle, videoId);
            }
        }
    }

    /// <summary>slug → artist page → numeric entity id → artist_videos (cached per artist).</summary>
    private async Task<List<(long Id, string Title)>> LoadArtistVideosAsync(string normArtist, string key, CancellationToken ct)
    {
        var entityId = await ResolveEntityIdAsync(normArtist, ct).ConfigureAwait(false);
        if (entityId == null)
        {
            _artistVideosCache[normArtist] = new List<(long, string)>();
            return _artistVideosCache[normArtist];
        }

        using var resp = await SendAsync($"{BaseUrl}/entity/{entityId.Value}?include=artist_videos", key, null, ct).ConfigureAwait(false);
        if (!CheckStatus(resp))
        {
            _artistVideosCache[normArtist] = new List<(long, string)>();
            return _artistVideosCache[normArtist];
        }

        var list = new List<(long, string)>();
        using (var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)))
        {
            if (doc.RootElement.TryGetProperty("artist_videos", out var av)
                && av.ValueKind == JsonValueKind.Object
                && av.TryGetProperty("videos", out var vids)
                && vids.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in vids.EnumerateArray())
                {
                    var id = GetInt64(v, "id");
                    var songTitle = GetString(v, "song_title");
                    if (id > 0 && !string.IsNullOrEmpty(songTitle))
                    {
                        list.Add((id, songTitle));
                    }
                }
            }
        }

        _artistVideosCache[normArtist] = list;
        if (list.Count > 0)
        {
            _logger.Debug("Reel: IMVDb: artist lookup cached {0} video(s) for \"{1}\"", list.Count, normArtist);
        }

        return list;
    }

    /// <summary>
    /// artist name → slug candidates → /n/{slug} page → numeric entity id from the unique
    /// "&lt;strong&gt;ID:&lt;/strong&gt; {n}" footer. 404/absent → next variant; null = give up.
    /// </summary>
    private async Task<long?> ResolveEntityIdAsync(string normArtist, CancellationToken ct)
    {
        if (_entityIdCache.TryGetValue(normArtist, out var cached))
        {
            return cached;
        }

        long? result = null;
        foreach (var slug in Slugify(normArtist))
        {
            using var resp = await SendAsync($"https://imvdb.com/n/{slug}", null, "text/html", ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            if (!resp.IsSuccessStatusCode)
            {
                WarnFailureOnce($"HTTP {(int)resp.StatusCode} on artist page");
                break;
            }

            var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var match = Regex.Match(html, @"<strong>ID:</strong>\s*(\d+)", RegexOptions.CultureInvariant);
            if (match.Success && long.TryParse(match.Groups[1].Value, out var id))
            {
                result = id;
                break;
            }
        }

        _entityIdCache[normArtist] = result;
        return result;
    }

    /// <summary>Slug candidates for an artist name (IMVDb page URLs are hyphenated slugs).</summary>
    private static IEnumerable<string> Slugify(string normArtist)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var basic = ToSlug(normArtist);
        if (basic.Length > 0 && seen.Add(basic))
        {
            yield return basic;
        }

        // "&" expansions map to slugs like "bob-marley-and-the-wailers"
        var idx = basic.IndexOf("-the-", StringComparison.Ordinal);
        if (idx > 0)
        {
            var withAnd = basic.Insert(idx + 1, "and-");
            if (seen.Add(withAnd))
            {
                yield return withAnd;
            }
        }

        var noThe = basic.StartsWith("the-", StringComparison.Ordinal) ? basic.Substring(4) : null;
        if (noThe != null && seen.Add(noThe))
        {
            yield return noThe;
        }
    }

    private static string ToSlug(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", "-").Trim('-');

    // ------------------------------------------------------------------ shared: video sources

    private async Task<string> GetYouTubeIdAsync(long imvdbVideoId, string key, CancellationToken ct)
    {
        using var resp = await SendAsync($"{BaseUrl}/video/{imvdbVideoId}?include=sources", key, null, ct).ConfigureAwait(false);
        if (!CheckStatus(resp))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string primary = null;
        foreach (var source in sources.EnumerateArray())
        {
            if (!string.Equals(GetString(source, "source"), "youtube", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = GetString(source, "source_data");
            if (string.IsNullOrEmpty(data))
            {
                continue;
            }

            var isPrimary = source.TryGetProperty("is_primary", out var ip) && ip.ValueKind == JsonValueKind.True;
            if (isPrimary)
            {
                primary = data;
                break;
            }

            primary ??= data;
        }

        if (primary == null)
        {
            return null;
        }

        return YouTubeService.TryGetVideoId(primary, out var videoId) ? videoId : null;
    }

    // ------------------------------------------------------------------ transport & status

    private async Task<HttpResponseMessage> SendAsync(string url, string key, string accept, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(key))
        {
            req.Headers.TryAddWithoutValidation("IMVDB-APP-KEY", key);
        }

        if (!string.IsNullOrEmpty(accept))
        {
            req.Headers.Remove("Accept");
            req.Headers.TryAddWithoutValidation("Accept", accept);
        }

        return await Http.SendAsync(req, ct).ConfigureAwait(false);
    }

    /// <summary>true = usable. 403 on an API endpoint = key problem (disable for the run);
    /// 404 = normal miss; 5xx/network = warn once (E-01).</summary>
    private bool CheckStatus(HttpResponseMessage resp)
    {
        var code = (int)resp.StatusCode;
        if (resp.IsSuccessStatusCode)
        {
            return true;
        }

        if (code == 403)
        {
            KeyRejected();
            return false;
        }

        if (code == 404)
        {
            return false;
        }

        WarnFailureOnce($"HTTP {code}");
        return false;
    }

    private void KeyRejected()
    {
        _keyRejected = true;
        _logger.Warn("Reel: IMVDb rejected the API key (403) — fix the key in the plugin config, or leave it empty for search-only mode. Continuing with search fallback.");
    }

    private void WarnFailureOnce(string detail)
    {
        if (_failureWarned)
        {
            return;
        }

        _failureWarned = true;
        _logger.Warn("Reel: IMVDb {0} — falling back to YouTube search for the rest of this run", detail);
    }

    // ------------------------------------------------------------------ match helpers

    /// <summary>Entry title equals the song title, or either contains the other (normalized).</summary>
    private static bool TitleLooksSame(string entryTitle, string songTitle)
    {
        if (entryTitle.Length == 0 || songTitle.Length == 0)
        {
            return false;
        }

        return entryTitle == songTitle
               || NeedsCheck.ContainsNormalized(entryTitle, songTitle)
               || NeedsCheck.ContainsNormalized(songTitle, entryTitle);
    }

    private static bool EntryHasArtist(JsonElement entry, string normArtist)
    {
        if (normArtist.Length == 0)
        {
            return false;
        }

        if (!entry.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var a in artists.EnumerateArray())
        {
            var name = NeedsCheck.Normalize(GetString(a, "name"));
            if (name.Length == 0)
            {
                continue;
            }

            if (name == normArtist
                || NeedsCheck.ContainsNormalized(name, normArtist)
                || NeedsCheck.ContainsNormalized(normArtist, name))
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ json helpers

    private static string GetString(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String)
        {
            return v.GetString();
        }

        return null;
    }

    private static long GetInt64(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number)
            {
                return v.GetInt64();
            }

            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }
}

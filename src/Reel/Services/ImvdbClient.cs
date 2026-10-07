using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Reel.Services;

/// <summary>
/// IMVDb discovery (F-10, F-13, E-01): given artist + title, returns the YouTube videoIds
/// referenced by IMVDb's entry for the song, in IMVDb's result order.
///
/// API contract (imvdb.com/developers/api):
///   GET /api/v1/search/videos?q={artist title}       → results[] { id, song_title, artists[] }
///   GET /api/v1/video/{id}?include=sources          → sources[] { source, source_data, is_primary }
///   Header IMVDB-APP-KEY is required (403 without it).
///
/// Behavior (F-55, E-01):
///   - no key configured → silently returns empty (search-only mode)
///   - 403 (invalid key) → warn once, disable for the rest of the run
///   - network/5xx failure → warn once per run, continue with search fallback
///   - per-run result cache (IMVDb asks that clients cache)
///
/// One instance per run; attribution: video data © IMVDb (shown on the config page, F-13).
/// </summary>
public sealed class ImvdbClient
{
    private const string BaseUrl = "https://imvdb.com/api/v1";

    /// <summary>At most this many IMVDb candidates per song (search fallback covers the rest).</summary>
    private const int MaxCandidatesPerSong = 3;

    private static readonly HttpClient Http = CreateClient();

    private readonly ILogger _logger;
    private readonly Dictionary<string, List<string>> _cache = new(StringComparer.Ordinal);

    private bool _keyRejected;
    private bool _failureWarned;

    public ImvdbClient(ILogger logger)
    {
        _logger = logger;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
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

        var cacheKey = NeedsCheck.Normalize(artist) + '\u001f' + NeedsCheck.Normalize(title);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return new List<string>(cached);
        }

        var ids = new List<string>();
        try
        {
            ids = await QueryAsync(artist, title, key, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            WarnFailureOnce("unreachable: " + ex.Message);
        }

        _cache[cacheKey] = ids;
        return new List<string>(ids);
    }

    // ------------------------------------------------------------------ internals

    private async Task<List<string>> QueryAsync(string artist, string title, string key, CancellationToken ct)
    {
        var query = Uri.EscapeDataString($"{artist} {title}".Trim());
        using var resp = await SendAsync($"{BaseUrl}/search/videos?q={query}&per_page=10", key, ct).ConfigureAwait(false);

        if (!CheckStatus(resp))
        {
            return new List<string>();
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        var normTitle = NeedsCheck.Normalize(title);
        var normArtist = NeedsCheck.Normalize(artist);
        var ids = new List<string>();

        foreach (var entry in results.EnumerateArray())
        {
            if (ids.Count >= MaxCandidatesPerSong)
            {
                break;
            }

            // F-12 safety: only adopt entries that are plausibly THIS song (the confidence
            // gate re-verifies the actual YouTube video afterwards anyway).
            var entryTitle = NeedsCheck.Normalize(GetString(entry, "song_title"));
            if (!TitleLooksSame(entryTitle, normTitle))
            {
                continue;
            }

            if (!EntryHasArtist(entry, normArtist))
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
                _logger.Debug("Reel: IMVDb: {0} – {1} -> {2}", artist, title, videoId);
            }
        }

        if (ids.Count == 0)
        {
            _logger.Debug("Reel: IMVDb: no usable entry for {0} – {1}", artist, title);
        }

        return ids;
    }

    private async Task<string> GetYouTubeIdAsync(long imvdbVideoId, string key, CancellationToken ct)
    {
        using var resp = await SendAsync($"{BaseUrl}/video/{imvdbVideoId}?include=sources", key, ct).ConfigureAwait(false);
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

    private async Task<HttpResponseMessage> SendAsync(string url, string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("IMVDB-APP-KEY", key);
        return await Http.SendAsync(req, ct).ConfigureAwait(false);
    }

    /// <summary>Returns true when the response is usable. 403 = bad key (disable for the run),
    /// 404 = not found (normal miss), 5xx/network = warn once (E-01).</summary>
    private bool CheckStatus(HttpResponseMessage resp)
    {
        var code = (int)resp.StatusCode;
        if (resp.IsSuccessStatusCode)
        {
            return true;
        }

        if (code == 403)
        {
            _keyRejected = true;
            _logger.Warn("Reel: IMVDb rejected the API key (403) — fix the key in the plugin config, or leave it empty for search-only mode. Continuing with search fallback.");
            return false;
        }

        if (code == 404)
        {
            return false;
        }

        WarnFailureOnce($"HTTP {code}");
        return false;
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

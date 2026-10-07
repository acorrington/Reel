using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Reel.Services;

/// <summary>Directly downloadable stream URLs for one YouTube video.</summary>
public sealed class StreamUrls
{
    public string VideoUrl { get; set; }
    /// <summary>Audio-only stream URL; null when <see cref="IsProgressive"/> (combined video+audio stream).</summary>
    public string AudioUrl { get; set; }
    public bool IsProgressive { get; set; }
    /// <summary>Which resolution path produced these URLs (IOS / ANDROID_VR / HTML).</summary>
    public string ResolvedBy { get; set; }
    /// <summary>User-Agent that should be used when downloading from googlevideo.</summary>
    public string DownloadUserAgent { get; set; }
    public int Height { get; set; }
}

/// <summary>
/// All YouTube interaction: visitorData token, InnerTube player API (IOS first — ANDROID_VR is
/// currently bot-blocked on many residential IPs), watch-page HTML fallback, stream selection
/// and InnerTube title search. No signature ciphering is required on the paths that work.
/// </summary>
public sealed class YouTubeService
{
    // User-Agents verified working against InnerTube and googlevideo from this machine.
    public const string DefaultAndroidVersion = "20.10.3";
    public const string DefaultIosVersion = "20.10.4";
    public const string DefaultWebSearchVersion = "2.20251006.01.00";
    public const string UaIos = "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)";
    public const string UaAndroid = "com.google.android.youtube/20.10.3 (Linux; U; Android 14) gzip";
    public const string UaAndroidVr = "com.google.android.apps.youtube.vr.oculus/1.60.19 (Linux; U; Android 12L; Quest 3 Build/SQ3A.220605.009.A1) gzip";

    internal static string AndroidUaFor(string version) => $"com.google.android.youtube/{version} (Linux; U; Android 14) gzip";
    internal static string IosUaFor(string version) => $"com.google.ios.youtube/{version} (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)";
    public const string UaChrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";
    private const string ConsentCookie = "CONSENT=YES+cb.20210328-17-p0.en+FX+678";

    private static readonly HttpClient ApiHttp = CreateClient(TimeSpan.FromSeconds(30));

    private static readonly object VisitorLock = new();
    private static readonly Dictionary<string, VisitorEntry> VisitorCache = new();

    private sealed class VisitorEntry
    {
        public string Value;
        public DateTime FetchedUtc;
    }

    private readonly ILogger _logger;

    public YouTubeService(ILogger logger)
    {
        _logger = logger;
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
        var client = new HttpClient(handler) { Timeout = timeout, MaxResponseContentBufferSize = 16 * 1024 * 1024 };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        return client;
    }

    // ------------------------------------------------------------------ visitorData

    private async Task<string> GetVisitorDataAsync(string userAgent, CancellationToken ct)
    {
        lock (VisitorLock)
        {
            if (VisitorCache.TryGetValue(userAgent, out var entry) && DateTime.UtcNow - entry.FetchedUtc < TimeSpan.FromHours(4))
            {
                return entry.Value;
            }
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/sw.js_data");
            req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("Cookie", ConsentCookie);
            using var resp = await ApiHttp.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.Warn("Reel: sw.js_data returned HTTP {0}", (int)resp.StatusCode);
                return null;
            }

            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var jsonStart = text.IndexOf("[[", StringComparison.Ordinal);
            if (jsonStart < 0)
            {
                _logger.Warn("Reel: sw.js_data did not contain expected JSON");
                return null;
            }

            using var doc = JsonDocument.Parse(text.Substring(jsonStart));
            var visitor = doc.RootElement[0][2][0][0][13].GetString();
            if (string.IsNullOrEmpty(visitor))
            {
                return null;
            }

            lock (VisitorLock)
            {
                VisitorCache[userAgent] = new VisitorEntry { Value = visitor, FetchedUtc = DateTime.UtcNow };
            }

            _logger.Debug("Reel: visitorData token acquired ({0} chars)", visitor.Length);
            return visitor;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("Reel: failed to fetch visitorData from sw.js_data: {0}", ex);
            return null;
        }
    }

    // ------------------------------------------------------------------ InnerTube player

    private sealed class ClientSpec
    {
        public string Name;
        public string Version;
        public string UserAgent;
        public Dictionary<string, object> Extra;
        public Dictionary<string, string> ExtraHeaders;
    }

    /// <summary>
    /// Builds the ordered InnerTube client chain from plugin configuration.
    /// Versions/order are configurable so a YouTube deprecation can be fixed
    /// from the dashboard without rebuilding the plugin.
    /// </summary>
    private static List<ClientSpec> BuildPlayerClients()
    {
        var cfg = Plugin.Instance?.Configuration;
        var androidVersion = string.IsNullOrWhiteSpace(cfg?.AndroidClientVersion)
            ? DefaultAndroidVersion
            : cfg.AndroidClientVersion.Trim();
        var iosVersion = string.IsNullOrWhiteSpace(cfg?.IosClientVersion)
            ? DefaultIosVersion
            : cfg.IosClientVersion.Trim();
        var androidUa = AndroidUaFor(androidVersion);
        var iosUa = IosUaFor(iosVersion);

        var android = new ClientSpec
        {
            Name = "ANDROID",
            Version = androidVersion,
            UserAgent = androidUa,
            Extra = new Dictionary<string, object>
            {
                ["androidSdkVersion"] = 35,
                ["osName"] = "Android",
                ["osVersion"] = "14",
                ["userAgent"] = androidUa
            },
            ExtraHeaders = new Dictionary<string, string>
            {
                ["X-Goog-Api-Format-Version"] = "2"
            }
        };

        var ios = new ClientSpec
        {
            Name = "IOS",
            Version = iosVersion,
            UserAgent = iosUa,
            Extra = new Dictionary<string, object>
            {
                ["deviceMake"] = "Apple",
                ["deviceModel"] = "iPhone16,2",
                ["osName"] = "iOS",
                ["osVersion"] = "17.5.1.21F90",
                ["platform"] = "MOBILE"
            }
        };

        var vr = new ClientSpec
        {
            Name = "ANDROID_VR",
            Version = "1.60.19",
            UserAgent = UaAndroidVr,
            Extra = new Dictionary<string, object>
            {
                ["deviceMake"] = "Oculus",
                ["deviceModel"] = "Quest 3",
                ["osName"] = "Android",
                ["osVersion"] = "12L",
                ["platform"] = "MOBILE"
            }
        };

        // ANDROID first: provides direct progressive (video+audio) URLs that are exempt from
        // the range cap googlevideo applies to adaptive streams on this network.
        var chain = new List<ClientSpec>();
        if (cfg?.PreferIosClient == true)
        {
            chain.Add(ios);
            chain.Add(android);
        }
        else
        {
            chain.Add(android);
            chain.Add(ios);
        }

        chain.Add(vr);
        return chain;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = factory();
            var resp = await ApiHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            if ((code == 429 || code == 503) && attempt < 2)
            {
                var delay = attempt == 0 ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(5);
                _logger.Warn("Reel: YouTube returned {0}, retrying in {1}s (attempt {2})", code, delay.TotalSeconds, attempt + 1);
                resp.Dispose();
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            return resp;
        }
    }

    private async Task<string> RequestPlayerAsync(string videoId, ClientSpec spec, CancellationToken ct)
    {
        var visitor = await GetVisitorDataAsync(spec.UserAgent, ct).ConfigureAwait(false);
        var client = new Dictionary<string, object>
        {
            ["clientName"] = spec.Name,
            ["clientVersion"] = spec.Version,
            ["hl"] = "en",
            ["gl"] = "US",
            ["utcOffsetMinutes"] = 0
        };
        foreach (var kv in spec.Extra)
        {
            client[kv.Key] = kv.Value;
        }

        if (visitor != null)
        {
            client["visitorData"] = visitor;
        }

        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["videoId"] = videoId,
            ["contentCheckOk"] = true,
            ["translationLanguage"] = "en",
            ["context"] = new Dictionary<string, object> { ["client"] = client }
        });

        using var resp = await SendWithRetryAsync(
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player");
                req.Headers.TryAddWithoutValidation("User-Agent", spec.UserAgent);
                req.Headers.TryAddWithoutValidation("Cookie", ConsentCookie);
                if (spec.ExtraHeaders != null)
                {
                    foreach (var kv in spec.ExtraHeaders)
                    {
                        req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                    }
                }

                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                return req;
            },
            ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.Warn("Reel: InnerTube {0} player request failed for {1}: HTTP {2}", spec.Name, videoId, (int)resp.StatusCode);
            return null;
        }

        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static string GetPlayability(JsonElement root, out bool ok)
    {
        ok = false;
        if (!root.TryGetProperty("playabilityStatus", out var ps))
        {
            return "no playabilityStatus";
        }

        var status = ps.TryGetProperty("status", out var st) ? st.GetString() : null;
        string reason = null;
        if (ps.TryGetProperty("reason", out var re) && re.ValueKind == JsonValueKind.String)
        {
            reason = re.GetString();
        }
        else if (ps.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array && msgs.GetArrayLength() > 0)
        {
            reason = msgs[0].GetString();
        }

        ok = string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase);
        return ok ? "OK" : $"{status}: {reason}";
    }

    // ------------------------------------------------------------------ HTML fallback

    /// <summary>Extract a balanced JSON object that follows <paramref name="anchor"/> in an HTML page.</summary>
    internal static string ExtractJsonObject(string text, string anchor)
    {
        if (text == null)
        {
            return null;
        }

        var idx = text.IndexOf(anchor, StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        var brace = text.IndexOf('{', idx);
        if (brace < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = brace; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(brace, i - brace + 1);
                }
            }
        }

        return null;
    }

    private async Task<string> FetchWatchPagePlayerJsonAsync(string videoId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/watch?v=" + videoId);
        req.Headers.TryAddWithoutValidation("User-Agent", UaChrome);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Cookie", ConsentCookie);

        using var resp = await SendWithRetryAsync(() => CloneRequest(req), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.Warn("Reel: watch page for {0} returned HTTP {1}", videoId, (int)resp.StatusCode);
            return null;
        }

        var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var json = ExtractJsonObject(html, "ytInitialPlayerResponse");
        if (json == null)
        {
            _logger.Warn("Reel: ytInitialPlayerResponse not found in watch page for {0}", videoId);
        }

        return json;
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage req)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri);
        foreach (var h in req.Headers)
        {
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        return clone;
    }

    // ------------------------------------------------------------------ stream selection

    private static bool TryGetDirectUrl(JsonElement format, out string url)
    {
        url = null;
        if (!format.TryGetProperty("url", out var urlProp) || urlProp.ValueKind != JsonValueKind.String)
        {
            return false; // signatureCipher only — unusable without JS deciphering
        }

        url = urlProp.GetString();
        return !string.IsNullOrEmpty(url);
    }

    private static int? GetInt(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var p))
        {
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v))
            {
                return v;
            }

            if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var s))
            {
                return s;
            }
        }

        return null;
    }

    private static string GetString(JsonElement el, string name)
    {
        return el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    /// <summary>
    /// Pick the best streams: adaptive video (mp4, prefer H.264/avc1, highest height under cap)
    /// + adaptive audio (prefer AAC, highest bitrate). Falls back to the best progressive
    /// (combined) mp4 when separate streams are unavailable.
    /// </summary>
    private static StreamUrls SelectStreams(JsonElement root, string resolvedBy, string userAgent, int maxHeight)
    {
        if (!root.TryGetProperty("streamingData", out var sd))
        {
            return null;
        }

        var progressive = new List<JsonElement>();
        if (sd.TryGetProperty("formats", out var fmts))
        {
            progressive.AddRange(fmts.EnumerateArray());
        }

        var adaptive = new List<JsonElement>();
        if (sd.TryGetProperty("adaptiveFormats", out var afmts))
        {
            adaptive.AddRange(afmts.EnumerateArray());
        }

        JsonElement bestVideo = default;
        var hasVideo = false;
        var videoHeight = -1;
        var videoIsAvc = false;

        foreach (var s in adaptive)
        {
            var mime = GetString(s, "mimeType") ?? string.Empty;
            if (!mime.StartsWith("video/mp4", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryGetDirectUrl(s, out var url))
            {
                continue;
            }

            var height = GetInt(s, "height") ?? 0;
            if (maxHeight > 0 && height > maxHeight && height > 0)
            {
                continue;
            }

            var isAvc = mime.Contains("avc1", StringComparison.OrdinalIgnoreCase);
            var better = !hasVideo
                          || height > videoHeight
                          || (height == videoHeight && isAvc && !videoIsAvc);
            if (better)
            {
                bestVideo = s;
                hasVideo = true;
                videoHeight = height;
                videoIsAvc = isAvc;
            }
        }

        JsonElement bestAudio = default;
        var hasAudio = false;
        var audioIsAac = false;
        var audioBitrate = -1;
        foreach (var s in adaptive)
        {
            var mime = GetString(s, "mimeType") ?? string.Empty;
            if (!mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryGetDirectUrl(s, out _))
            {
                continue;
            }

            var isAac = mime.StartsWith("audio/mp4", StringComparison.OrdinalIgnoreCase);
            var bitrate = GetInt(s, "bitrate") ?? GetInt(s, "averageBitrate") ?? 0;
            var better = !hasAudio
                         || (isAac && !audioIsAac)
                         || (isAac == audioIsAac && bitrate > audioBitrate);
            if (better)
            {
                bestAudio = s;
                hasAudio = true;
                audioIsAac = isAac;
                audioBitrate = bitrate;
            }
        }

        JsonElement bestProg = default;
        var hasProg = false;
        var progHeight = -1;
        var progIsAvc = false;
        foreach (var s in progressive)
        {
            var mime = GetString(s, "mimeType") ?? string.Empty;
            if (!mime.StartsWith("video/mp4", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryGetDirectUrl(s, out _))
            {
                continue;
            }

            var height = GetInt(s, "height") ?? 0;
            if (maxHeight > 0 && height > maxHeight && height > 0)
            {
                continue;
            }

            var isAvc = mime.Contains("avc1", StringComparison.OrdinalIgnoreCase);
            var better = !hasProg
                         || height > progHeight
                         || (height == progHeight && isAvc && !progIsAvc);
            if (better)
            {
                bestProg = s;
                hasProg = true;
                progHeight = height;
                progIsAvc = isAvc;
            }
        }

        // Prefer progressive (combined video+audio) streams: googlevideo applies a per-URL range
        // cap to adaptive streams (full-file requests get 403), but progressive URLs serve the
        // entire file in one request. Adaptive pairs remain as a fallback for videos without a
        // direct progressive URL.
        if (hasProg)
        {
            return new StreamUrls
            {
                VideoUrl = bestProg.GetProperty("url").GetString(),
                AudioUrl = null,
                IsProgressive = true,
                ResolvedBy = resolvedBy,
                DownloadUserAgent = userAgent,
                Height = progHeight
            };
        }

        if (hasVideo && hasAudio)
        {
            return new StreamUrls
            {
                VideoUrl = bestVideo.GetProperty("url").GetString(),
                AudioUrl = bestAudio.GetProperty("url").GetString(),
                IsProgressive = false,
                ResolvedBy = resolvedBy,
                DownloadUserAgent = userAgent,
                Height = videoHeight
            };
        }

        return null;
    }

    // ------------------------------------------------------------------ public API

    /// <summary>
    /// Resolve downloadable streams for a videoId: ANDROID InnerTube first (direct progressive
    /// URLs, no range cap), then IOS, then ANDROID_VR, then the watch-page HTML player response.
    /// Returns null when every path fails.
    /// </summary>
    public async Task<StreamUrls> ResolveAsync(string videoId, int maxHeight, CancellationToken ct)
    {
        foreach (var spec in BuildPlayerClients())
        {
            try
            {
                var json = await RequestPlayerAsync(videoId, spec, ct).ConfigureAwait(false);
                if (json == null)
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(json);
                var playability = GetPlayability(doc.RootElement, out var ok);
                if (!ok)
                {
                    _logger.Debug("Reel: {0} client for {1}: {2}", spec.Name, videoId, playability);
                    continue;
                }

                var result = SelectStreams(doc.RootElement, spec.Name, spec.UserAgent, maxHeight);
                if (result != null)
                {
                    _logger.Debug("Reel: {0} resolved {1} ({2}p, progressive={3})", spec.Name, videoId, result.Height, result.IsProgressive);
                    return result;
                }

                _logger.Debug("Reel: {0} client for {1}: OK but no usable direct streams", spec.Name, videoId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Reel: {spec.Name} player request failed for {videoId}: {{0}}", ex);
            }
        }

        // HTML fallback: parse ytInitialPlayerResponse from the watch page.
        try
        {
            var json = await FetchWatchPagePlayerJsonAsync(videoId, ct).ConfigureAwait(false);
            if (json != null)
            {
                using var doc = JsonDocument.Parse(json);
                var playability = GetPlayability(doc.RootElement, out var ok);
                if (ok)
                {
                    var result = SelectStreams(doc.RootElement, "HTML", UaChrome, maxHeight);
                    if (result != null)
                    {
                        _logger.Debug("Reel: HTML resolved {0} ({1}p)", videoId, result.Height);
                        return result;
                    }

                    _logger.Warn("Reel: watch page for {0} only offered signature-ciphered streams (not supported)", videoId);
                }
                else
                {
                    _logger.Debug("Reel: HTML player for {0}: {1}", videoId, playability);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Reel: HTML fallback failed for {videoId}: {{0}}", ex);
        }

        return null;
    }

    // ------------------------------------------------------------------ URL helpers

    /// <summary>Extract an 11-char video id from a watch/youtu.be/shorts URL (or a bare id).</summary>
    public static bool TryGetVideoId(string urlOrId, out string videoId)
    {
        videoId = null;
        if (string.IsNullOrWhiteSpace(urlOrId))
        {
            return false;
        }

        var s = urlOrId.Trim();
        if (Regex.IsMatch(s, @"^[A-Za-z0-9_\-]{11}$"))
        {
            videoId = s;
            return true;
        }

        var m = Regex.Match(s, @"(?:[?&]v=|/shorts/|/embed/|/live/|/v/)([A-Za-z0-9_\-]{11})");
        if (!m.Success)
        {
            m = Regex.Match(s, @"youtu\.be/([A-Za-z0-9_\-]{11})");
        }

        if (m.Success)
        {
            videoId = m.Groups[1].Value;
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------------ InnerTube search (F-11)

    /// <summary>One InnerTube search result: id, title and (when shown) duration.</summary>
    public sealed class SearchHit
    {
        public string VideoId;
        public string Title;

        /// <summary>Duration from the result's lengthText ("3:45"); 0 = not shown.</summary>
        public long DurationSeconds;
    }

    /// <summary>
    /// InnerTube search via the **WEB** client.
    /// 2026-10 finding: the IOS client no longer receives search results at all (payload is
    /// dialogs/chrome only, zero videoRenderers) while WEB returns full results — so search
    /// uses WEB and its version is config-editable (F-56/N-05 zero-rebuild proofing). The
    /// player/stream chain (ANDROID → IOS → ANDROID_VR → HTML) is unaffected and unchanged.
    /// </summary>
    private async Task<List<SearchHit>> SearchInnerTubeAsync(string query, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        var webVersion = string.IsNullOrWhiteSpace(cfg?.WebSearchClientVersion)
            ? DefaultWebSearchVersion
            : cfg.WebSearchClientVersion.Trim();
        var ua = UaChrome;

        var visitor = await GetVisitorDataAsync(ua, ct).ConfigureAwait(false);
        var client = new Dictionary<string, object>
        {
            ["clientName"] = "WEB",
            ["clientVersion"] = webVersion,
            ["hl"] = "en",
            ["gl"] = "US",
            ["utcOffsetMinutes"] = 0
        };
        if (visitor != null)
        {
            client["visitorData"] = visitor;
        }

        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["context"] = new Dictionary<string, object> { ["client"] = client },
            ["query"] = query
        });

        using var resp = await SendWithRetryAsync(
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/search?prettyPrint=false");
                req.Headers.TryAddWithoutValidation("User-Agent", ua);
                req.Headers.TryAddWithoutValidation("Cookie", ConsentCookie);
                req.Headers.TryAddWithoutValidation("X-Youtube-Client-Name", "1");
                req.Headers.TryAddWithoutValidation("X-Youtube-Client-Version", webVersion);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                return req;
            },
            ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            // E-03: name the failing client + version so it can be fixed from the config page.
            _logger.Warn("Reel: InnerTube WEB search (clientVersion {0}) failed: HTTP {1} — update WebSearchClientVersion in the plugin config if this persists",
                webVersion, (int)resp.StatusCode);
            return new List<SearchHit>();
        }

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var hits = new List<SearchHit>();
        CollectVideoRenderers(doc.RootElement, hits);
        return hits;
    }

    private static void CollectVideoRenderers(JsonElement element, List<SearchHit> hits)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("videoRenderer", out var vr))
                {
                    var id = GetString(vr, "videoId");
                    string title = null;
                    if (vr.TryGetProperty("title", out var t))
                    {
                        if (t.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array && runs.GetArrayLength() > 0)
                        {
                            title = GetString(runs[0], "text");
                        }
                        else
                        {
                            title = GetString(t, "simpleText");
                        }
                    }

                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(title))
                    {
                        long duration = 0;
                        if (vr.TryGetProperty("lengthText", out var lt) && lt.ValueKind == JsonValueKind.Object)
                        {
                            string durationText = null;
                            if (lt.TryGetProperty("simpleText", out var stx) && stx.ValueKind == JsonValueKind.String)
                            {
                                durationText = stx.GetString();
                            }
                            else if (lt.TryGetProperty("runs", out var lruns) && lruns.ValueKind == JsonValueKind.Array && lruns.GetArrayLength() > 0)
                            {
                                durationText = GetString(lruns[0], "text");
                            }

                            duration = ParseDurationText(durationText);
                        }

                        hits.Add(new SearchHit { VideoId = id, Title = title, DurationSeconds = duration });
                    }

                    return;
                }

                foreach (var prop in element.EnumerateObject())
                {
                    CollectVideoRenderers(prop.Value, hits);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectVideoRenderers(item, hits);
                }

                break;
        }
    }

    /// <summary>"3:45" / "1:02:03" → seconds; unparsable → 0.</summary>
    public static long ParseDurationText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        long seconds = 0;
        foreach (var part in text.Split(':'))
        {
            if (!long.TryParse(part.Trim(), out var value))
            {
                return 0;
            }

            seconds = seconds * 60 + value;
        }

        return seconds;
    }

    /// <summary>
    /// Fetch title/channel/duration for a video without downloading it (F-20's duration
    /// source: InnerTube player response `videoDetails.lengthSeconds`). Tries the configured
    /// client chain, then the watch-page HTML — same order as ResolveAsync. Null when every
    /// path fails (the gate treats that as doubt and skips, F-22).
    /// </summary>
    public async Task<VideoDetails> GetVideoDetailsAsync(string videoId, CancellationToken ct)
    {
        foreach (var spec in BuildPlayerClients())
        {
            try
            {
                var json = await RequestPlayerAsync(videoId, spec, ct).ConfigureAwait(false);
                if (json == null)
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(json);
                var playMsg = GetPlayability(doc.RootElement, out var ok);
                if (!ok)
                {
                    _logger.Debug("Reel: {0} client metadata for {1}: {2}", spec.Name, videoId, playMsg);
                    continue;
                }

                var details = ParseVideoDetails(doc.RootElement, videoId);
                if (details != null)
                {
                    return details;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn("Reel: {0} metadata request failed for {1}: {2}", spec.Name, videoId, ex.Message);
            }
        }

        try
        {
            var json = await FetchWatchPagePlayerJsonAsync(videoId, ct).ConfigureAwait(false);
            if (json != null)
            {
                using var doc = JsonDocument.Parse(json);
                GetPlayability(doc.RootElement, out var ok);
                if (ok)
                {
                    var details = ParseVideoDetails(doc.RootElement, videoId);
                    if (details != null)
                    {
                        return details;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("Reel: HTML metadata fallback failed for {0}: {1}", videoId, ex.Message);
        }

        return null;
    }

    private static VideoDetails ParseVideoDetails(JsonElement root, string videoId)
    {
        if (!root.TryGetProperty("videoDetails", out var vd) || vd.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var title = GetString(vd, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        long seconds = 0;
        if (vd.TryGetProperty("lengthSeconds", out var ls))
        {
            if (ls.ValueKind == JsonValueKind.String)
            {
                long.TryParse(ls.GetString(), out seconds);
            }
            else if (ls.ValueKind == JsonValueKind.Number)
            {
                seconds = ls.GetInt64();
            }
        }

        return new VideoDetails
        {
            VideoId = videoId,
            Title = title,
            Channel = GetString(vd, "author"),
            DurationSeconds = seconds
        };
    }

    /// <summary>
    /// Search YouTube for "{artist} {title} official video" (F-11) using the same InnerTube
    /// search as Trawler. Returns hits (id, title, duration) — "official video" titled results
    /// first — up to <paramref name="maxResults"/>.
    /// </summary>
    public async Task<List<SearchHit>> SearchMusicVideoIdsAsync(string artist, string title, int maxResults, CancellationToken ct)
    {
        if (maxResults <= 0 || (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title)))
        {
            return new List<SearchHit>();
        }

        var query = $"{artist} {title} official video";
        _logger.Info("Reel: searching YouTube for \"{0}\"", query);

        try
        {
            var hits = await SearchInnerTubeAsync(query, ct).ConfigureAwait(false);
            if (hits.Count == 0)
            {
                return new List<SearchHit>();
            }

            var ordered = hits
                .Where(h => h.Title.Contains("official", StringComparison.OrdinalIgnoreCase))
                .Concat(hits)
                .GroupBy(h => h.VideoId)
                .Select(g => g.First())
                .Take(maxResults)
                .ToList();

            foreach (var hit in ordered)
            {
                _logger.Debug("Reel: search candidate: {0} -> {1} ({2}s)", hit.Title, hit.VideoId, hit.DurationSeconds);
            }

            return ordered;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("Reel: YouTube search failed: {0}", ex);
            return new List<SearchHit>();
        }
    }
}

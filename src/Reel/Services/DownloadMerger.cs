using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Reel.Services;

/// <summary>
/// Downloads stream files to local temp storage and merges them with ffmpeg.
/// All temp files live under Path.GetTempPath() (never on the library/network path),
/// and are cleaned up by the caller's finally block.
/// </summary>
public sealed class DownloadMerger
{
    private static readonly HttpClient DownloadHttp = CreateClient();

    private readonly ILogger _logger;

    public DownloadMerger(ILogger logger)
    {
        _logger = logger;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 4
        };
        // Streams are large; overall timeout is enforced per-download via a linked CTS.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Download a stream to <paramref name="destPath"/>. One retry on transient failure (E-05).
    /// googlevideo rejects plain GETs and open-ended ranges with 403 — downloads use a closed
    /// Range probe (bytes=0-65535), learn the total size from Content-Range, then fetch the rest.
    /// Returns true when the file was written and is non-empty.
    /// </summary>
    public async Task<bool> DownloadToFileAsync(string url, string destPath, string userAgent, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // Second attempt falls back to the UA proven working in manual tests.
            var ua = attempt == 0 ? userAgent : YouTubeService.UaIos;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));

                await using (var target = File.Create(destPath))
                {
                    var ok = await DownloadRangeAsync(url, ua, target, timeoutCts.Token).ConfigureAwait(false);
                    await target.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
                    if (!ok)
                    {
                        if (attempt == 0)
                        {
                            _logger.Warn("Reel: stream download failed (attempt 1), retrying with fallback strategy");
                            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                            continue;
                        }

                        return false;
                    }
                }

                if (new FileInfo(destPath).Length == 0)
                {
                    _logger.Warn("Reel: stream download produced a 0-byte file (attempt {0})", attempt + 1);
                    if (attempt == 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                        continue;
                    }

                    return false;
                }

                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Reel: stream download failed (attempt {attempt + 1}): {{0}}", ex);
                if (attempt == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                    continue;
                }

                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Range-based download: probe a small closed range, read the total size from Content-Range,
    /// then pull the remaining bytes in bounded chunks. All bytes are appended to <paramref name="target"/>.
    /// </summary>
    private async Task<bool> DownloadRangeAsync(string url, string userAgent, Stream target, CancellationToken ct)
    {
        const long probeSize = 65536;      // googlevideo 403s ranges that exceed the file — start tiny
        const long chunkSize = 4L * 1024 * 1024; // googlevideo 403s ranges longer than 4 MB (8 MB rejected in tests)

        long offset = 0;
        long? total = null;
        var requests = 0;

        while (total == null || offset < total.Value)
        {
            if (++requests > 64)
            {
                _logger.Warn("Reel: too many range requests while downloading stream");
                return false;
            }

            var end = total == null
                ? offset + probeSize - 1
                : Math.Min(offset + chunkSize - 1, total.Value - 1);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            req.Headers.Range = new RangeHeaderValue(offset, end);

            using var resp = await DownloadHttp
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.OK)
            {
                // Server ignored the Range header and is sending the entire body.
                await using var whole = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await whole.CopyToAsync(target, 128 * 1024, ct).ConfigureAwait(false);
                return true;
            }

            if (resp.StatusCode != HttpStatusCode.PartialContent)
            {
                _logger.Warn("Reel: stream chunk [{0}-{1}] returned HTTP {2}", offset, end, (int)resp.StatusCode);
                return false;
            }

            var range = resp.Content.Headers.ContentRange;
            if (range?.Length == null)
            {
                _logger.Warn("Reel: stream chunk [{0}-{1}] came back without a usable Content-Range", offset, end);
                return false;
            }

            if (range.From != offset)
            {
                _logger.Warn("Reel: stream chunk started at {0} instead of {1}", range.From, offset);
                return false;
            }

            total = range.Length;
            var rangeEnd = range.To ?? range.Length.Value - 1;

            await using var source = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await source.CopyToAsync(target, 128 * 1024, ct).ConfigureAwait(false);
            offset = rangeEnd + 1;
        }

        return true;
    }

    /// <summary>
    /// Merge separate video + audio files into one mp4 with ffmpeg (F-32):
    /// <c>ffmpeg -y -i video -i audio -c:v copy -c:a aac -movflags +faststart out</c>.
    /// Returns true on success; on failure logs ffmpeg's stderr tail (E-06).
    /// </summary>
    public async Task<bool> MergeAsync(string videoPath, string audioPath, string outputPath, string ffmpegPath, CancellationToken ct)
    {
        var args = $"-y -i \"{videoPath}\" -i \"{audioPath}\" -c:v copy -c:a aac -movflags +faststart \"{outputPath}\"";
        _logger.Debug("Reel: ffmpeg {0} {1}", ffmpegPath, args);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                _logger.Error("Reel: failed to start ffmpeg at {0}", ffmpegPath);
                return false;
            }

            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // ignored — process may have exited between the check and the kill
                }

                _logger.Error("Reel: ffmpeg {0}", ct.IsCancellationRequested ? "cancelled" : "timed out after 3 minutes");
                return false;
            }

            var stderr = await stderrTask.ConfigureAwait(false);
            _ = await stdoutTask.ConfigureAwait(false);

            if (process.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                var tail = stderr;
                if (tail != null && tail.Length > 1500)
                {
                    tail = tail.Substring(tail.Length - 1500);
                }

                _logger.Error("Reel: ffmpeg exited with code {0}. stderr tail: {1}", process.ExitCode, tail);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.ErrorException("Reel: ffmpeg invocation failed", ex, Array.Empty<object>());
            return false;
        }
    }
}

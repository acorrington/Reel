# Trawler

A from-scratch Emby Server plugin that downloads YouTube trailers for movies in your library and saves
them next to your media as `{MovieFileName}-trailer.mp4`, so Emby plays them as native
local trailers.

Built with an API-first, resilience-obsessed pipeline.
Targets **net8.0** (built with the .NET 10 SDK) — your Emby 4.10.1 server is a
self-contained .NET 8.0.31 app, so net8.0 is the correct target framework.

## Status: DONE (2026-10-06)

| Acceptance test | Result |
|---|---|
| Add movie → trailer appears automatically | ✅ ~127s (90s Emby `LibraryMonitorDelaySeconds` + 30s plugin delay F-01 + ~7s download) |
| Trailer plays natively in Emby (`LocalTrailerCount`) | ✅ 10/10 movies `LocalTrailerCount=1`; ffprobe: h264 + aac in mp4 |
| Delete trailer + run scheduled task → re-downloads | ✅ exact byte-identical re-download |
| Network/API failures never crash Emby | ✅ entire pipeline try/catch, silent skip + detailed logs |
| Config page + settings via dashboard API | ✅ `GET/POST /Plugins/{id}/Configuration` |
| Detailed logs (status codes, videoIds) | ✅ embyserver.txt, "Trawler:" prefixed |

## How it works

```
ItemAdded (Movie, EnableAutoDownload) ──delay 30s──┐
Scheduled task "Download Missing Trailers" ────────┤
                                                   ▼
                                    TrailerPipeline (max 2 concurrent)
                                       1. needs check (file + LocalTrailerIds)
                                       2. candidate IDs: RemoteTrailers, else
                                          InnerTube search "{Title} {Year} official trailer"
                                       3. resolve streams, per client chain:
                                          ANDROID (direct progressive!) → IOS → ANDROID_VR → HTML
                                       4. ranged download to %TEMP%\Trawler\{guid}
                                       5. if adaptive: ffmpeg merge (-c:v copy -c:a aac)
                                          if progressive: skip merge entirely
                                       6. move to {movie dir}\{base}-trailer.mp4
                                       7. ILibraryMonitor report + RefreshMetadata
```

## Key discoveries (why it works when others don't)

1. **ANDROID_VR is bot-blocked** on residential IPs (LOGIN_REQUIRED). The **IOS** client
   returns direct, unciphered stream URLs.
2. **The `ANDROID` client works** with: `clientVersion 20.10.3`, `androidSdkVersion 35`,
   a **`userAgent` field inside `context.client`**, header `X-Goog-Api-Format-Version: 2`
   (otherwise: HTTP 400 / "Precondition check failed").
3. **googlevideo range cap (the big one):** adaptive (video-only/audio-only) streams reject
   any closed range whose end exceeds a per-URL cap (~2–30 MB, always < file size) with
   HTTP 403. Plain GETs and open-ended ranges (`bytes=0-`) are also always 403.
   → **Progressive streams (itag 18/22, video+audio muxed) have NO cap** — full file in one
   request. Trawler therefore prefers progressive streams: single download, no ffmpeg needed.
4. **Trailer naming** verified against Emby 4.10.1's own `Emby.Naming.dll`:
   `^(?:.*[._ -]+)?trailer[0-9]*([\.\]_ -][^\\/\(\)]*)?$` → `{file base}-trailer.mp4` works
   in flat libraries (confirmed: all 10 movies linked).
5. **`EnableRealtimeMonitor` was `false`** on the Movies library — Emby itself could not see
   new files (no ItemAdded events, no watcher). Fixed via
   `POST /Library/VirtualFolders/LibraryOptions` (now `true`). Without this, no plugin can
   react to new movies until a manual library scan.

## Project layout

```
src/Trawler/
├── Trawler.csproj                     # net8.0, Emby refs from system\, embedded resources
├── Plugin.cs                          # BasePlugin<PluginConfiguration>, pages, thumb (new GUID)
├── thumb.jpg                          # generated original artwork
├── Configuration/PluginConfiguration.cs
├── EntryPoints/TrailerEntryPoint.cs   # ItemAdded + delay + per-item dedup + symmetric Dispose
├── Services/YouTubeService.cs         # visitorData cache, InnerTube clients, stream selection, search
├── Services/DownloadMerger.cs         # ranged chunk downloads (4MB max/range), ffmpeg merge
├── Services/TrailerPipeline.cs        # orchestrator: needs-check → resolve → download → install
├── Tasks/DownloadMissingTrailersTask.cs
└── Web/configPage.html + configPage.js
```

Build — point the build at your Emby install's `system` folder (contains the
MediaBrowser.*.dll reference assemblies), either via property or environment variable:

```
dotnet build src/Trawler/Trawler.csproj -c Release -p:EmbySystemPath="C:\Path\To\Emby-Server\system"
# or: setx EMBY_SYSTEM_PATH "C:\Path\To\Emby-Server\system"
```

Deploy: copy `bin\Release\Trawler.dll` into your Emby server's `programdata\plugins\`
folder and restart Emby.

Plugin GUID: `910C9CE1-C355-48FA-93D5-411EE319D392` · Config XML: `plugins/configurations/Trawler.xml`

## Configuration (dashboard → Plugins → Trawler)

| Setting | Default | Notes |
|---|---|---|
| EnableAutoDownload | true | reacts to ItemAdded |
| TriggerDelaySeconds | 30 | wait for metadata providers (F-01) |
| MaxVideoHeight | 0 | 0 = best available |
| EnableYouTubeSearchFallback | true | InnerTube search when RemoteTrailers empty |
| MaxSearchResults | 3 | search candidates |
| FfmpegPathOverride | (empty) | falls back to Emby's bundled ffmpeg |
| LastRunSummary | | written by the scheduled task |

The old trailer plugin was removed from the plugins folder (it reacted to the same events).

## Test tools

`tools/` contains the throwaway diagnostic harnesses used to crack YouTube behavior
(h2test = HTTP/1 vs h2 range tester; test-*.ps1 = InnerTube client/range experiments).

## Deployments

| Server | Emby | Result |
|---|---|---|
| Test server | 4.10.1.0 (.NET 8) | ✅ deployed, 10/10 movies |
| Production server | 4.10.1.0 (.NET 8) | ✅ first full run **900 movies → 775 downloaded / 1 skipped / 124 not found** in 40 min, 0 errors, 10/10 linking spot-check |

First-run failure anatomy (all graceful, all retried by the daily 04:00 trigger):
30 movies had no usable YouTube URL anywhere (RemoteTrailers empty + search empty);
94 had candidates that never resolved (region-blocked/ciphered-only videos — per-videoId in the log);
4 transient stream-download failures; 0 crashes, 0 error-level logs.

Deployment steps (reusable):
1. Copy `Trawler.dll` into your Emby server's `programdata\plugins\` folder
   (plugin DLLs are replaceable even while the server runs — .NET shares delete handles)
2. `POST /System/Restart` (Emby relaunches itself) — verify via `/Plugins` + `programdata\logs\embyserver.txt`
3. Ensure the Movies library has `EnableRealtimeMonitor: true` (`GET /Library/VirtualFolders`,
   fix via `POST /Library/VirtualFolders/LibraryOptions` with `{Name, Guid, Id, ItemId, LibraryOptions}` —
   note the four identity fields are required or you get "Unrecognized Guid format")
4. Run `Download Missing Trailers` once; daily 04:00 trigger is the safety net after that.

YouTube-proofing on a live server: if downloads start failing, the log names the failing client
(`Trawler: InnerTube ANDROID player request failed ... HTTP 400`). Bump
`AndroidClientVersion` / `IosClientVersion` on the config page (or check `PreferIosClient`)
and restart — no rebuild needed.


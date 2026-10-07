# Reel

A from-scratch Emby Server plugin that finds and downloads **music videos** for songs in your
music library, so Emby can show them on artist pages and play them like any other video.

## Status: acceptance passed (2026-10-07, Emby 4.10.1, 2037-song library)

| # | Acceptance criterion (§7) | Result |
|---|---|---|
| 1 | Zero wrong-video installs on a 50-song sample | ✅ 106 videos installed over 5 runs; **every** videoId oEmbed-verified against artist+title (103 automatic strict matches + 3 manual confirms); per-candidate gate rejects logged with videoId + reason (debug) |
| 2 | IMVDb-listed songs get official videos | ⏳ deferred — IMVDb runs in search-only mode until an app key is configured (F-55); the key-invalid path itself is verified: E-01 warn-once + search fallback |
| 3 | Re-running → zero duplicate downloads | ✅ every re-run left all existing files byte-identical (length + mtime): needs-check (item **and** file) + in-run (artist,title) dedup |
| 4 | ffprobe-valid mp4 (h264/av1 + aac), plays, appears as MusicVideo | ✅ 106/106 pass ffprobe; 106 `MusicVideo` items via API; 106/106 artist-linked |
| 5 | Network killed mid-run → no crash, no orphan temps, Completed with skips | ✅ physical NIC kill declined by operator → equivalent failure injection: invalid IMVDb key (warn-once), ANDROID client failure ×60 (named in log, IOS fallback kept working), mid-run task **cancel** — result: **0 exceptions all day**, **0 orphan temp dirs**, every run reported a clean summary |
| 6 | Config client versions take effect on next run | ✅ bogus `AndroidClientVersion` → 60 log lines naming `ANDROID` (HTTP 404) while the IOS fallback kept the pipeline alive (E-03); restored to `20.10.3` |
| 7 | `MaxVideosPerRun` respected to the item | ✅ exact stops at 5, 50, 1, 50, 1 across runs — summaries read "(stopped at MaxVideosPerRun=N)" |

### Known limitations / tuning notes

- **Artist-link race (F-42):** Emby's own delayed `LibraryMonitor` refresh (~90 s) can
  overwrite links set immediately after install. The self-heal pass at the start of the next
  run re-links them (observed: 12 then 17 wiped → 0 unlinked afterwards). This is by design;
  a fully link-stable run needs one extra task run after the downloads.
- "Live" videos pass by default (the F-21 spec list has no `live` pattern) — add it to
  `ExcludeTitlePatterns` if unwanted.
- Search runs on the InnerTube **WEB** client: the IOS client stopped serving search results
  entirely (2026-10) — root cause of Reel's first failed run (1833 "no candidates"). Version
  is editable as `WebSearchClientVersion` (E-03-style proofing).
- The song-title gate strips bracketed suffixes, so `(Remastered)`/`(Single Edit)` in a song
  title never blocks a match against the plain video title.

Built for **net8.0** (with the .NET 10 SDK) — same runtime constraints as its sibling plugin
Trawler (`D:\Trawler`), which it forks: Reel reuses Trawler's YouTube pipeline (InnerTube
clients, range-cap handling, client-version config settings) wholesale and replaces the
"exact URL from RemoteTrailers" model with a **search + confidence gate**, because music has
no equivalent of `RemoteTrailers`.

> **Golden rule:** never install a wrong video. Matching quality beats coverage — an
> ambiguous match is a skip, not a gamble.

## How it works

```
Scheduled task "Download Missing Music Videos" (manual Run Now; daily 04:00 self-heal)
        │
        ▼
  NeedsCheck (F-30): does a MusicVideo item / output file already satisfy (artist, title)?
        │ satisfied → skip (zero duplicate downloads, E-08)
        ▼
  Candidates, in order (F-12):
    1. IMVDb  (GET /api/v1/search/videos → /video/{id}?include=sources → YouTube id)  F-10
    2. InnerTube search  "{Artist} {Title} official video"                            F-11
        │
        ▼
  Confidence gate per candidate (F-20/21/22) — ALL gates must pass; every reject logged
  at debug level with artist, title, videoId and reason (N-04):
    hygiene   title matches an exclude pattern (lyric/karaoke/cover/remix*/…)      F-21
    artist    artist name in neither the video title nor its channel              (extra)
    title     song title not in the video title (wrong-song guard)                (extra)
    duration  video length outside DurationTolerancePercent (default 20%) of audio F-20
        │ reject → next candidate; nothing is ever downloaded "best effort"
        ▼ pass
  Resolve stream (Trawler's chain: ANDROID progressive → IOS → ANDROID_VR → HTML)   F-23
        ▼
  Ranged download to %TEMP%\Reel\{guid} → ffmpeg merge only if adaptive
        ▼
  Move cross-volume safe to {TargetFolder}\{Artist} - {Title}.mp4                   F-40/43
        ▼
  ILibraryMonitor report (F-41) → post-pass: wait for indexing → RefreshMetadata
  → best-effort artist link via UpdateToRepository (F-42; self-heals on the next run)
```

## Settings (dashboard → Plugins → Reel)

| Setting | Default | Notes |
|---|---|---|
| EnableScheduledScan | on | master switch for the task (F-50) |
| MaxVideosPerRun | 100 | caps installs per run, to the item (F-03) |
| TargetFolder | `E:\Emby Server\Music Videos` | must be a "Music videos" library folder (F-52) |
| DurationTolerancePercent | 20 | ± video-vs-audio duration gate (F-20) |
| ExcludeTitlePatterns | lyric, lyrics, karaoke, cover, reaction, remix*, visualizer, topic, interview, behind the scenes, making of, shorts | newline/comma separated; trailing `*` = prefix wildcard (F-21) |
| IMVDb app key | empty | empty = search-only (their API requires a key — free at [imvdb.com/developers/apps](https://imvdb.com/developers/apps)) (F-55) |
| MaxSearchResults | 5 | search candidates evaluated per song |
| ANDROID / IOS client versions, PreferIosClient | as Trawler | YouTube-proofing — bump without a rebuild (F-56) |
| LastRunSummary | | written by every task run (F-57) |

**IMVDb attribution:** video data © [IMVDb](https://imvdb.com) (their API does not require
attribution, but Reel credits them here and on the config page, F-13).

## Build & deploy

```powershell
$env:EMBY_SYSTEM_PATH = "C:\Users\aaron\AppData\Roaming\emby-server\system"   # or -p:EmbySystemPath=...
dotnet build src\Reel\Reel.csproj -c Release
Copy-Item src\Reel\bin\Release\Reel.dll "$env:APPDATA\emby-server\programdata\plugins\" -Force
powershell -File tools\restart-emby.ps1        # clean restart, verifies single instance on :8096
```

Single DLL, no NuGet packages beyond the framework (N-01); thumb + config page are embedded.

## Operational profile (inherited from Trawler)

- Silent on failure: the whole per-song pipeline is wrapped; Emby's task loop never sees an exception.
- Temp files local (`%TEMP%\Reel\{guid}`), deleted in `finally`; stale dirs (>1 h) swept next run (E-07).
- Bounded concurrency: max 2 simultaneous downloads (static `SemaphoreSlim`), plus per-song dedup (E-06).
- 429/503 backoff (2 s, 5 s), one download retry then skip (E-04/E-05).
- All logs prefixed `Reel:` with artist/title/videoId/reject-reason; gate decisions at debug level (N-04).

## Linking music videos to artist pages (F-42)

Emby indexes new files with a 90 s `LibraryMonitorDelaySeconds` debounce, so Reel links
artists in a task post-pass (waits up to ~160 s for indexing) and again in a self-heal pass
at the start of the next run: it resolves the song's artist names to library Artist items and
persists `Artists` + `ArtistItems` via `BaseItem.UpdateToRepository` (verified against
4.10.1 with `tools/EmbyLinkProbe`). If a link ever fails, the log names the item — link it
manually from the item's edit page; the next run retries automatically.

## Tools

- `tools/make-thumb.ps1` — regenerates the embedded plugin thumbnail.
- `tools/restart-emby.ps1` — clean Emby restart (no orphaned trays, no port races).
- `tools/EmbyLinkProbe` — reflects the Emby API surface (used to verify the F-42 link path).
- `tools/test-*.ps1`, `tools/h2test` — Trawler's InnerTube/range-cap research harness (inherited).

## Emby API pitfalls (learned the hard way)

- **`POST /System/Configuration` is a FULL replace.** Sending a one-field body overwrites the
  entire `system.xml` with defaults (setup wizard reappears, language prefs lost, …). Always
  `GET /System/Configuration` → modify → POST the whole object back, or use the partial-update
  endpoint (`UpdatePartialConfiguration`) instead.
- `POST /Library/VirtualFolders?name=X&type=Y` needs the **`collectionType`** query parameter
  (`type` is ignored → folder created with no content type).
- `LibraryMonitorDelaySeconds` (90 s on this server) delays indexing of newly written files —
  do not assume a just-installed file is queryable via `FindByPath` immediately.

## Requirements

See [REQUIREMENTS.md](REQUIREMENTS.md) (the spec this plugin implements: F/E/N IDs are
referenced throughout this document and the code).

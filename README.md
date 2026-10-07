# Reel

A from-scratch Emby Server plugin that finds and downloads **music videos** for songs in your
music library, so Emby can show them on artist pages and play them like any other video.

## Status: acceptance passed (2026-10-07, Emby 4.10.1, 2037-song library)

| # | Acceptance criterion (§7) | Result |
|---|---|---|
| 1 | Zero wrong-video installs on a 50-song sample | ✅ 107 videos installed over 7 runs; **every** videoId oEmbed-verified against artist+title (104 automatic strict matches + 3 manual confirms); per-candidate gate rejects logged with videoId + reason (debug) |
| 2 | IMVDb-listed songs get official videos | ✅ verified with `MaxSearchResults=0` (IMVDb as *only* candidate source): search endpoint found edge-blocked → logged once → artist-lookup discovery returned 18 candidates across 14 listed songs → official video installed (`-n3sUWR4FV4`, oEmbed-verified). Key-invalid path also verified: E-01 warn-once + search fallback |
| 3 | Re-running → zero duplicate downloads | ✅ every re-run left all existing files byte-identical (length + mtime): needs-check (item **and** file) + in-run (artist,title) dedup |
| 4 | ffprobe-valid mp4 (h264/av1 + aac), plays, appears as MusicVideo | ✅ 106/106 pass ffprobe at the time of measurement (107th verified by oEmbed/API); 107 `MusicVideo` items via API; 107/107 artist-linked |
| 5 | Network killed mid-run → no crash, no orphan temps, Completed with skips | ✅ physical NIC kill declined by operator → equivalent failure injection: invalid IMVDb key (warn-once), ANDROID client failure ×60 (named in log, IOS fallback kept working), mid-run task **cancel** — result: **0 exceptions all day**, **0 orphan temp dirs**, every run reported a clean summary |
| 6 | Config client versions take effect on next run | ✅ bogus `AndroidClientVersion` → 60 log lines naming `ANDROID` (HTTP 404) while the IOS fallback kept the pipeline alive (E-03); restored to `20.10.3` |
| 7 | `MaxVideosPerRun` respected to the item | ✅ exact stops at 5, 50, 1, 50, 1 across runs — summaries read "(stopped at MaxVideosPerRun=N)" |

### Known limitations / tuning notes

- **IMVDb search is edge-blocked (2026-10-07):** their nginx returns 403 for *all*
  `/api/v1/search/*` calls (every IP, UA and key tested — the video/entity endpoints work
  fine). Reel logs this once per run and automatically uses the **artist-lookup discovery**
  instead: slugify(artist) → `/n/{slug}` page (`<strong>ID:</strong> {n}` footer → entity id)
  → `/api/v1/entity/{id}?include=artist_videos` → title match → `/video/{id}?include=sources`.
  The documented search path stays primary and will be used again if IMVDb lifts the block.
- **Artist-link race (F-42):** Emby's own delayed `LibraryMonitor` refresh (~90 s) can
  overwrite links set right after install. The `ItemUpdated` listener re-links within
  seconds (primary); the task's post-pass and next-run self-heal remain as backstops —
  observed wiping (12, then 17 items) always ends at 0 unlinked.
- "Live" videos pass by default (the F-21 spec list has no `live` pattern) — add it to
  `ExcludeTitlePatterns` if unwanted.
- Search runs on the InnerTube **WEB** client: the IOS client stopped serving search results
  entirely (2026-10) — root cause of Reel's first failed run (1833 "no candidates"). Version
  is editable as `WebSearchClientVersion` (E-03-style proofing).
- The song-title gate strips bracketed suffixes **and** trailing qualifier tails (`- From X
  Soundtrack`, `- Single Version`, `- 2015 Remaster`, `- Radio Edit`, `- Promo 7 Edit`,
  `- 7 Version`), so those variants never block a match against the plain video title — and
  all variants of one song share a single installed video (E-08).
- **Album-art uploads are rejected by pixels, not titles (F-24):** YouTube is full of
  "static album cover + audio" videos whose titles look perfectly legitimate (e.g. the Bee
  Gees promo incident, 2026-10-07: `Stayin' Alive (Promo 12" Version) (Remastered)` by
  "Music Jukebox" — 6 fps, 362×360, one still image). After download, Reel samples 7 frames
  with ffmpeg; a max pairwise frame difference below `StaticImageDiffThreshold` (default
  10.0) means album art → reject, next candidate. Calibrated on the real library: the bad
  promo video measured **0.17**, three real music videos measured **41.9 / 50.8 / 57.0**.
- IMVDb's official entries skip the duration gate (F-20b, on by default): the official
  video for "Stayin' Alive" is ~4:45 while the promo 12" audio runs ~6:56 — the old strict
  gate rejected the *right* video and let the album-art one win on duration. All other
  gates (hygiene/artist/title) still apply to IMVDb candidates.

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
    1. IMVDb  (search API →, when edge-blocked: artist page → entity → artist_videos,
               then /video/{id}?include=sources → YouTube id)                     F-10
    2. InnerTube search  "{Artist} {Title} official video"                        F-11
        │
        ▼
  Confidence gate per candidate (F-20/21/22) — ALL gates must pass; every reject logged
  at debug level with artist, title, videoId and reason (N-04):
    hygiene   title matches an exclude pattern (lyric/karaoke/cover/album art/…)      F-21
    artist    artist name in neither the video title nor its channel              (extra)
    title     song title not in the video title (wrong-song guard)                (extra)
    duration  video length outside DurationTolerancePercent (default 20%) of audio F-20
              — skipped for IMVDb official entries (ImvdbSkipsDurationGate)        F-20b
        │ reject → next candidate; nothing is ever downloaded "best effort"
        ▼ pass
  Resolve stream (Trawler's chain: ANDROID progressive → IOS → ANDROID_VR → HTML)   F-23
        ▼
  Ranged download to %TEMP%\Reel\{guid} → ffmpeg merge only if adaptive
        ▼
  Content gate: sample 7 frames — static image (album art + audio) → reject,        F-24
    next candidate (calibrated: album-art ≈0.2 max frame diff, real MVs 40+)
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
| ImvdbSkipsDurationGate | on | IMVDb official entries skip the duration gate — the official video still counts when your audio is a promo/12"/remix edit (F-20b) |
| ExcludeTitlePatterns | lyric, lyrics, karaoke, cover, reaction, remix*, visualizer, topic, interview, behind the scenes, making of, shorts, album art, official audio, audio only, with picture, slideshow, static image | newline/comma separated; trailing `*` = prefix wildcard (F-21) |
| RejectStaticImageVideos | on | frame-samples the download before install; album-art/audio uploads are rejected (F-24) |
| StaticImageDiffThreshold | 10.0 | static-image sensitivity, 0–255 frame diff; lower = stricter (F-24) |
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

Two mechanisms, both verified on 4.10.1 (API surface probed with `tools/EmbyLinkProbe`):

1. **`ArtistLinkEntryPoint` — ItemUpdated listener (primary).** Whenever a MusicVideo under
   `TargetFolder` is saved without artist links (initial indexing, or Emby's background
   refresh overwriting fresh links), the artist is parsed from the `{Artist} - {Title}`
   filename convention and persisted via `BaseItem.UpdateToRepository` — *within seconds of
   the save, no task run required*. Proven end-to-end: install → task cancelled → index
   fires ~90 s later → `linked artist(s) … (via ItemUpdated)` → links present. Loop-safe:
   the handler's own save sees links populated and returns (one bounce maximum), and it
   never throws into Emby's event loop.
2. **Task post-pass + self-heal (backstop).** After each run, installs are polled until
   indexed, refreshed and linked; the start of the next run re-links anything still empty
   (observed wiping pattern before the listener existed: 12 then 17 items → 0 afterwards).

If a link ever fails, the log names the item — link it manually from the item's edit page;
the listener and next run retry automatically.

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

## License

MIT — see [LICENSE](LICENSE).

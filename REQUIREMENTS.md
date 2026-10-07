# Reel — Requirements Document

**Date**: 2026-10-06
**Target**: Emby Server plugin (net8.0, built with the .NET 10 SDK — same runtime constraints as Trawler)
**Relationship to Trawler**: sibling plugin. Fork Trawler's repo as the starting point and reuse its
YouTube pipeline (InnerTube clients, range-cap handling, client-version config settings) wholesale.
This document only specifies where Reel **deliberately differs**.

---

## 1. Overview

A plugin that finds and downloads **music videos** for songs in the music library, so Emby can
show them on artist pages and play them like any other video.

### Goals
- **Never install a wrong video.** Matching quality beats coverage: an ambiguous match is a skip, not a gamble.
- Same operational profile as Trawler: silent on failure, temp files local, bounded concurrency, nightly self-heal.
- Zero-rebuild resilience: YouTube client versions stay config-page editable (inherited from Trawler).

### Non-goals (v1)
See §8.

---

## 2. Context: what makes this harder than Trawler

Trawler had `RemoteTrailers` — Emby already knew the exact YouTube URL for each movie.
Music has **no equivalent**: every video must be *found*, and search results are noisy
(covers, lyric videos, karaoke, "Artist – Topic" auto-uploads, reactions, live jams,
wrong song with the same title). The core requirement of this plugin is therefore a
**confidence gate**, not a downloader.

---

## 3. Functional Requirements

### 3.1 Trigger conditions

| ID | Requirement | Priority |
|----|-------------|----------|
| F-01 | Scheduled task **"Download Missing Music Videos"** scans the music library for Audio items whose song has no matching MusicVideo item | Must |
| F-02 | Manual "Run Now" from the dashboard is the **only** trigger in v1 (no ItemAdded auto-download until matching is proven) | Must |
| F-03 | `MaxVideosPerRun` setting (default 100) caps work per run — first run on a 2,000-song library must not hammer YouTube/IMVDB | Must |
| F-04 | ItemAdded auto-download: deferred to v2; design must not preclude it | Could |

### 3.2 Source chain (discovery)

| ID | Requirement | Priority |
|----|-------------|----------|
| F-10 | **IMVDB first**: query the IMVDB API by artist + title; when an entry is found, use its referenced video URL (YouTube) as the primary candidate | Must |
| F-11 | **YouTube search fallback** when IMVDB has no entry: query `"{Artist} {Title} official video"` via the same InnerTube search used by Trawler | Must |
| F-12 | Iterate candidates in order; first one passing the confidence gate wins (F-22); otherwise skip (never "best effort") | Must |
| F-13 | IMVDB API key / attribution setting: config field; IMVDB use must comply with their attribution requirements (credite in config page or README) | Must |

### 3.3 Confidence gate (the heart of the plugin)

| ID | Requirement | Priority |
|----|-------------|----------|
| F-20 | **Duration check**: video length must be within `DurationTolerancePercent` (default 20%) of the audio track's duration; otherwise reject | Must |
| F-21 | **Title hygiene**: reject candidates whose titles match exclude patterns — default: `lyric`, `lyrics`, `karaoke`, `cover`, `reaction`, `remix`*, `visualizer`, `topic`, `interview`, `behind the scenes`, `making of`, `shorts` (*configurable; `remix` exclusion editable for dance libraries) | Must |
| F-22 | A candidate passes only if **all** enabled gates pass; on any doubt → skip and log the reason with the videoId | Must |
| F-23 | Prefer higher resolution progressive streams (same selection logic as Trawler: progressive > adaptive because of the googlevideo range cap) | Must |

### 3.4 Needs-check (does this song already have its video?)

| ID | Requirement | Priority |
|----|-------------|----------|
| F-30 | Before downloading for song X, query existing MusicVideo items: consider it satisfied if an MV exists whose normalized title ≈ song title **and** shares an artist link (or artist name) | Must |
| F-31 | Normalization: case, punctuation, `feat.`/`ft.`/`with` clauses, bracketed suffixes, `(Live)`/`(Remastered)` markers | Must |
| F-32 | A song failing F-30 counts as "missing"; already-downloaded songs are never re-fetched (file + item both checked, like Trawler's F-02) | Must |

### 3.5 Install, naming and linking

| ID | Requirement | Priority |
|----|-------------|----------|
| F-40 | Output: `{Artist} - {Title}.mp4` (movie-style naming per Emby's Music Videos docs) in the configured target folder — default: a dedicated **Music Videos** library folder; config field `TargetFolder` | Must |
| F-41 | After install: report file change to `ILibraryMonitor` + refresh, exactly like Trawler | Must |
| F-42 | **Auto-link (best effort)**: after the MusicVideo item exists, set its Artist link from the song's artist (metadata-manager equivalent via API/plugin). If the API path is not reliable, log instructions and leave manual linking documented — verify during development which path works on 4.10.1 | Should |
| F-43 | Download into local temp (`Path.GetTempPath()\Reel\{guid}`), move cross-volume safe, cleanup in `finally` | Must |

### 3.6 Configuration (config page)

| ID | Setting | Default | Notes |
|----|---------|---------|-------|
| F-50 | EnableScheduledScan | true | master switch for F-01 |
| F-51 | MaxVideosPerRun | 100 | F-03 |
| F-52 | TargetFolder | (required) | where music videos are installed |
| F-53 | DurationTolerancePercent | 20 | F-20 |
| F-54 | ExcludeTitlePatterns | (defaults of F-21) | newline/comma separated, editable |
| F-55 | ImvdbApiKey | (empty) | optional — falls back to search-only when empty |
| F-56 | PreferIosClient / AndroidClientVersion / IosClientVersion | as Trawler | YouTube-proofing, carried over unchanged |
| F-57 | LastRunSummary | | written by the task (same as Trawler) |

---

## 4. Technical Architecture

```
Scheduled task / (v2: ItemAdded)
        ▼
  NeedsCheck (F-30) ── satisfied? ──► skip
        ▼
  Candidates: IMVDB (F-10) → InnerTube search (F-11)
        ▼
  Confidence gate (F-20/21/22) ── reject ──► log reason, next candidate
        ▼ pass
  Resolve stream (Trawler's chain: ANDROID progressive → IOS → ANDROID_VR → HTML)
        ▼
  Download to local temp → (ffmpeg merge only if adaptive) → move to TargetFolder
        ▼
  ILibraryMonitor report → RefreshMetadata → best-effort artist link (F-42)
```

### Reused from Trawler (fork, don't rewrite)
- `YouTubeService` (visitorData cache, InnerTube client chain, stream selection, search)
- `DownloadMerger` (4 MB max closed-range chunks, ffmpeg merge path)
- `Plugin` scaffolding, config-page pattern, daily-trigger pattern, `Gate` concurrency, temp-dir handling, all logging conventions

### New in Reel
- `NeedsCheck` (song ↔ MusicVideo matching, §3.4)
- `ImvdbClient` (artist/title query → video URL; graceful 4xx/timeout → search fallback)
- `MatchGate` (duration + title-hygiene evaluation, §3.3)
- `Linker` (best-effort artist linking, F-42)

### Duration source
Audio duration: from the `Audio` item's `RunTimeTicks`. Video duration: from the InnerTube
player response `lengthSeconds` (no extra download needed) or IMVDB metadata when available.

---

## 5. Error Handling & Resilience

| ID | Scenario | Behavior |
|----|----------|----------|
| E-01 | IMVDB unreachable / key invalid | log warn once per run, continue with search fallback |
| E-02 | No candidate passes the gate | log skip with per-candidate reasons + videoIds; never download |
| E-03 | YouTube client deprecation (HTTP 400) | log names the failing client; version bump via config page (inherited mechanism) |
| E-04 | 429/503 | exponential backoff (2 s, 5 s), then next candidate (inherited) |
| E-05 | Download/merge failure | one retry, then skip; temps deleted in `finally` |
| E-06 | Two runs overlap | single global `SemaphoreSlim` + per-song dedup (inherited pattern) |
| E-07 | Shutdown mid-run | token propagated from Emby; partial files removed next run (scan temp dir age > 1 h) |
| E-08 | Same song under multiple albums | needs-check keys on (artist, title), not album — download once |

**General principle**: identical to Trawler — the whole per-song pipeline is wrapped; a failure
logs and moves on; Emby's event/task loop is never exposed to an exception.

---

## 6. Non-Functional

| ID | Requirement |
|----|-------------|
| N-01 | net8.0, no NuGet packages beyond the framework; single-DLL deployment (fork approach), < 5 MB |
| N-02 | Peak memory < 50 MB (stream to disk) |
| N-03 | Bounded concurrency: max 2 simultaneous downloads (inherited `Gate`) |
| N-04 | All logs prefixed `Reel:` with artist/title/videoId/reject-reason — debug-level for gate decisions (tuning match quality requires visibility) |
| N-05 | Config-page YouTube-proofing fields must behave exactly as Trawler's |

---

## 7. Acceptance Criteria

1. Run the task on a library with known music videos → **zero wrong-video installs** on a 50-song sample (spot-check titles/durations manually)
2. IMVDB-listed songs get official videos; non-listed songs either match via search gates or are skipped with logged reasons
3. Re-running the task performs **zero** duplicate downloads (needs-check works)
4. Every installed file passes `ffprobe` (valid mp4, h264/av1 + aac), plays in Emby, and appears as a MusicVideo item after refresh
5. Kill network mid-run → no crash, no orphan temp files, task reports Completed with skips
6. Reel's config page independently toggles YouTube client versions and they take effect on the next run
7. `MaxVideosPerRun` respected to the item

---

## 8. Out of Scope (v1)

- ItemAdded auto-download (v2, gated on proven matching quality)
- Automatic artist-page linking **if** F-42 proves unreliable — documented manual step instead
- Downloading from non-YouTube sources; audio/video ripping, re-encoding
- Grouping MV rows by "album" on artist pages (open Emby feature request)
- Embedding videos into audio files / video-during-audio playback (Emby doesn't support it)
- Lyrics (Trawler's sibling domain, already served natively by Emby)

---

## 9. Suggested Project Structure (fork of Trawler)

```
Reel/
├── Reel.csproj                        # net8.0, EmbySystemPath property (as Trawler)
├── Plugin.cs                          # new GUID, name "Reel"
├── thumb.jpg                          # new original artwork
├── Configuration/PluginConfiguration.cs   # §3.6 settings
├── EntryPoints/                       # empty in v1 (no auto trigger) — kept for v2
├── Services/
│   ├── YouTubeService.cs              # inherited unchanged
│   ├── DownloadMerger.cs              # inherited unchanged
│   ├── ImvdbClient.cs                 # NEW
│   ├── MatchGate.cs                   # NEW (duration + hygiene)
│   ├── NeedsCheck.cs                  # NEW (song ↔ MV matching)
│   └── ReelPipeline.cs                # orchestrator (replaces TrailerPipeline)
├── Tasks/DownloadMissingMusicVideosTask.cs
└── Web/configPage.html + configPage.js
```

---

## 10. Risks & Open Questions

| # | Question | Resolution needed before/during build |
|---|----------|--------------------------------------|
| 1 | IMVDB API auth model (key? attribution-only?) | Verify endpoint contract during spike; make key optional (F-55) |
| 2 | Reliable auto-linking of artist on MusicVideo items via plugin API on 4.10.1 | Spike in F-42; manual fallback acceptable for v1 |
| 3 | IMVDB coverage of the library's era/genres | Measure on sample of 100 songs before tuning fallback expectations |
| 4 | Search-only match quality | Acceptance test #1 is the gate for enabling F-04 (auto trigger) in v2 |

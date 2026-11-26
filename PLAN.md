# Spotify → Lidarr → Navidrome playlist plugin — initial plan

This is the original design. See the [README](README.md) for the v0.1.0 implementation and its tested compatibility target.

A Lidarr plugin that:

1. Reads Spotify playlists (including editorial ones) by **parsing public HTML pages**, with no Spotify API key and no internal GraphQL API.
2. Sends the artists/albums to Lidarr through Lidarr's own **Import List** pipeline.
3. Keeps one **public Navidrome playlist** per Spotify playlist up to date through the **Subsonic API**, containing the tracks that already exist in the library.

---

## 1. Architecture

```
            Lidarr (plugins branch)
 ┌───────────────────────────────────────────────────────┐
 │  Import List: "Spotify Playlist (Scraped)"            │
 │   settings: playlist URLs, monitor mode, Navidrome    │
 │                                                       │
 │   Fetch()  ──► SpotifyScraper ──► PlaylistSnapshot    │
 │      │                               │ (cached JSON)  │
 │      ▼                               ▼                │
 │  ImportListItemInfo[]          NavidromeSyncService   │
 │  (artist + album)  ──► Lidarr       │                 │
 │                        adds/monitors │                │
 │                                      │                │
 │  Notification: "Navidrome Playlist Sync" (optional)   │
 │   OnReleaseImport ──────────────────►┘                │
 └──────────────────────────────────────┬────────────────┘
                                        │ Subsonic API
                                        ▼
                                    Navidrome
```

### Components

| Component | Type | Responsibility |
|---|---|---|
| `SpotifyPlaylistImportList` | `ImportListBase<TSettings>` | Lidarr calls `Fetch()` on its schedule. Scrapes every configured playlist, returns `ImportListItemInfo` (artist + album), then triggers Navidrome sync. |
| `SpotifyScraper` | service | Fetches the embed page, extracts `__NEXT_DATA__`, maps it to `PlaylistSnapshot`. Optionally fetches track pages to get the album name. |
| `SnapshotStore` | service | Stores the last *good* snapshot per playlist as JSON in the Lidarr AppData folder. Also caches track → album lookups and Navidrome song matches. |
| `NavidromeClient` | service | A thin Subsonic API client: `ping`, `search3`, `getPlaylists`, `getPlaylist`, `createPlaylist`, `updatePlaylist`. |
| `NavidromeSyncService` | service | Matches snapshot tracks to Navidrome songs and creates or replaces the playlists. |
| `MissingTracksHealthCheck` | `HealthCheckBase` | Shows a Lidarr health warning (System → Status) when tracks are still missing from Navidrome, or when a playlist couldn't be read. |
| `NavidromeNotification` (phase 3) | `NotificationBase<TSettings>` | Runs a sync after Lidarr imports new music, so you don't wait for the next list refresh. |

### Why Import List (and not something else)

- **Settings UI comes for free.** Lidarr renders the settings form, so the playlist list is just a field (`FieldType.Tag`: one URL per tag).
- **Lidarr handles the rest of the artist work.** It looks up artists and albums by name in its metadata server, deduplicates, and applies the root folder, quality profile, metadata profile, tags and monitor options.
- **Lidarr already schedules it.** The provider sets `MinRefreshInterval` (for example 6h).
- Navidrome sync is a side effect of `Fetch()`, which is not quite how import lists are meant to work. The notification provider in phase 3 adds a second, cleaner trigger.

---

## 2. Spotify scraping

### Source: the embed page

```
GET https://open.spotify.com/embed/playlist/{id}
```

- It's a Next.js page. Track data sits in `<script id="__NEXT_DATA__" type="application/json">`.
- Each track entry is expected to have its title, artist string, duration and URI. **Check this in phase 0.**
- The track list is probably capped at around 100. **Check this in phase 0.**
- Send a normal browser User-Agent and `Accept-Language`. Keep at most one request per playlist per refresh.

### How artists are added (decided)

The default is **full discographies**: add the artist and monitor everything allowed by the artist's **metadata profile** (for example albums + singles).

This doesn't need custom settings. Every Lidarr import list already has these fields in its form:

- **Monitor** dropdown: `Entire Artist` / `Specific Album` / `None`. The default for this list is `Entire Artist`.
- **Metadata Profile** dropdown: which release types count (albums, singles, EPs…).
- **Quality Profile** and **Root Folder**.

The plugin follows whichever Monitor option is chosen:

| Monitor | Items the plugin returns | Album lookup needed |
|---|---|---|
| Entire Artist (default) | one item per unique artist | no |
| Specific Album | artist + album per track | yes, see below |
| None | one item per artist (added unmonitored) | no |

With `Entire Artist`, the plugin can skip album lookups entirely, which means fewer requests to Spotify.

### Album information (only for "Specific Album")

The embed data probably has no album name. Options, in order of preference:

1. Use the album if `__NEXT_DATA__` has it (verify).
2. Fetch `https://open.spotify.com/track/{id}` and read its `<meta>` tags (`og:description`, `music:album`). Cache this per track ID forever, so each track costs one extra request only once.
3. If the album still isn't found, add only the artist, unmonitored, and log it.

### Multiple artists on a track

A track like "A, B feat. C" has several artists. Setting: **Add featured artists** (checkbox, default off). Off means only the first (primary) artist is added, which avoids pulling full discographies for every guest feature.

### Robustness rules

- If a parse returns 0 tracks or fails validation, **keep the previous snapshot**, log a warning, and never empty a Navidrome playlist.
- Treat the parser as a swappable strategy (`IPlaylistSource`) so it can be fixed quickly when Spotify changes the page.
- Personalized playlists (Discover Weekly, Daily Mix, Release Radar) are **not supported**. A logged-out request only sees the generic version.

### Data model

```csharp
record PlaylistSnapshot(
    string SpotifyId, string Name, string? Description, string? ImageUrl,
    DateTime FetchedAt, IReadOnlyList<SpotifyTrack> Tracks);

record SpotifyTrack(
    string SpotifyId, string Title, IReadOnlyList<string> Artists,
    string? Album, int DurationMs, int Position);
```

---

## 3. Navidrome sync (Subsonic API)

### Auth

Use token auth on every request: `u`, `t = md5(password + salt)`, `s = salt`, `v=1.16.1`, `c=lidarr-spotify`, `f=json`. Use a dedicated Navidrome user, which owns the playlists.

### Matching a Spotify track to a Navidrome song

1. Check the match cache (`spotifyTrackId → navidromeSongId`) and confirm the song still exists.
2. Call `search3?query="{artist} {title}"&songCount=20&artistCount=0&albumCount=0`.
3. Score each candidate:
   - Normalized title similarity: lowercase, strip accents, remove `(feat. …)`, `- Remastered 2011`, `- Radio Edit`, and punctuation.
   - Artist similarity: any artist from the Spotify artist list matches the song's artist or album artist.
   - Duration within ±3s is a bonus; a difference over 10s is a penalty.
   - Album match is a small bonus.
4. Accept the best score if it's at or above a threshold (setting, default 0.85). Store accepted matches in the cache. Re-try unmatched tracks on every sync, because the library grows as Lidarr downloads.

### Writing playlists

- Name the playlist `{prefix}{Spotify name}`, for example `[Spotify] Today's Top Hits`. Store the Navidrome playlist ID per Spotify ID in the store, so renaming on Spotify doesn't create duplicates.
- **Create:** call `createPlaylist?name=…&songId=…&songId=…`.
- **Update:** call `createPlaylist?playlistId=…&songId=…`. This replaces the songs in Spotify order. Only send it if the matched list changed.
- Then call `updatePlaylist?playlistId=…&public=true&comment=…`. Put the Spotify URL, last sync time and the `x / y tracks available` count in the comment.
- Removing a playlist URL in Lidarr settings: optionally delete the Navidrome playlist (setting, default off).

Public playlists are visible to all Navidrome users, which gives you the shared playlist you wanted.

### Reporting missing tracks (decided)

- **Playlist comment, always:** `Synced from Spotify · 87/100 tracks available · last sync 2026-11-26 14:00`.
- **Lidarr health warning, to start with:** shown under System → Status, for example:
  `Spotify playlists: 23 tracks not in Navidrome yet (Today's Top Hits: 13, RapCaviar: 10)`.
  It has a link to the import list. It goes away on its own once everything matches.
- A separate health **error** appears when a playlist can't be read (the page changed, or the playlist was removed). This one is more important, since it means the plugin is broken.
- Setting: **Warn about missing tracks** (checkbox, default on), so it can be turned off if it gets annoying without changing code.
- A track that has been missing for a long time might still deserve a warning (for example, Lidarr can't find a release). Consider later: only warn about tracks missing for more than N days.

---

## 4. Plugin settings (Lidarr UI)

**Import list "Spotify Playlist (Scraped)":**

| Field | Type | Notes |
|---|---|---|
| Playlist URLs | Tag | `https://open.spotify.com/playlist/…` or a raw ID |
| Add featured artists | Checkbox | Default off: only the primary artist per track |
| Navidrome URL | Textbox | `http://navidrome:4533` |
| Navidrome username / password | Textbox / Password | |
| Playlist name prefix | Textbox | Default `[Spotify] ` |
| Make playlists public | Checkbox | Default on |
| Match threshold | Number | Default 0.85 |
| Delete removed playlists | Checkbox | Default off |
| Warn about missing tracks | Checkbox | Default on; Lidarr health warning |

Lidarr's standard import list fields (Monitor, Root Folder, Quality Profile, Metadata Profile, Tags) still apply, and decide how artists are added (see section 2).

### One list, several playlists (decided)

One import list holds **several playlist URLs** and **one Navidrome connection**. Reasons:

- The Navidrome settings are entered once.
- Several playlists can be fetched together, and one artist appearing in several playlists is sent to Lidarr once.
- If some playlists need a different root folder or profile, just add a **second instance** of the import list. The plugin keys its stored data by import list ID + Spotify playlist ID, so instances don't interfere.

Lidarr shows a **Test** button. `Test()` should check that the first playlist parses and that Navidrome `ping` succeeds.

---

## 5. Tech stack and project layout

- C# / .NET, using whatever target framework the Lidarr `plugins` branch currently uses (check, probably `net8.0`).
- Reference Lidarr as a **git submodule** (`ext/Lidarr`, `plugins` branch) and build against `Lidarr.Core` / `Lidarr.Common`. This is how existing plugins do it (for example the Tidal, Deezer and Tubifarry plugins). Use one of them as the template.
- Use Lidarr's `IHttpClient` for HTTP (proxy settings, logging), and `AngleSharp` or a regex for pulling the `<script>` JSON. AngleSharp must be bundled, or check whether Lidarr already ships an HTML parser.
- Use `System.Text.Json` for parsing.

```
spotify-to-navidrome/
├─ ext/Lidarr/                      (submodule, plugins branch)
├─ src/Lidarr.Plugin.SpotifyNavidrome/
│  ├─ ImportList/SpotifyPlaylistImportList.cs
│  ├─ ImportList/SpotifyPlaylistSettings.cs
│  ├─ Spotify/IPlaylistSource.cs
│  ├─ Spotify/EmbedPageSource.cs
│  ├─ Spotify/TrackPageAlbumResolver.cs
│  ├─ Navidrome/SubsonicClient.cs
│  ├─ Navidrome/NavidromeSyncService.cs
│  ├─ Navidrome/TrackMatcher.cs
│  ├─ Storage/SnapshotStore.cs
│  └─ Notifications/NavidromeNotification.cs   (phase 3)
├─ tests/                            (NUnit, HTML fixtures saved from real pages)
├─ docker-compose.dev.yml           (Lidarr plugins image + Navidrome for local testing)
└─ .github/workflows/release.yml    (build zip, create GitHub release)
```

Install for users: Lidarr → System → Plugins → paste the GitHub repo URL. The plugin is installed from the latest GitHub release.

---

## 6. Phases

### Phase 0 — spikes (do first, ~1 evening)

- [ ] Fetch 3–4 editorial playlists' embed pages. Save them as test fixtures and document the `__NEXT_DATA__` path to the track list, available fields, and the track cap.
- [ ] Check whether the album name is in the embed data. If not, confirm that a track page's `<meta>` tags have it. This only matters for "Specific Album" mode, so it can wait if it's awkward.
- [ ] Check how a plugin registers a health check, and how to trigger it after a sync.
- [ ] Check the current state of Lidarr plugin support: which branch, which Docker image tag (for example hotio `pr-plugins` or linuxserver nightly), and which target framework.
- [ ] Clone an existing plugin, build it, and load it in a local Lidarr container to confirm the dev loop works.
- [ ] Confirm that `createPlaylist` with `playlistId` replaces songs in Navidrome (the expected behaviour).

### Phase 1 — Spotify → Lidarr

- [ ] Plugin skeleton and settings class.
- [ ] `EmbedPageSource` and its parsing tests.
- [ ] `Fetch()` returns one item per unique primary artist. Verify that with "Entire Artist", Lidarr adds the artists and monitors their full discography according to the metadata profile.
- [ ] "Add featured artists" setting.
- [ ] Album resolver with cache, for "Specific Album" mode (can move to phase 3).

### Phase 2 — Navidrome sync

- [ ] Subsonic client with token auth.
- [ ] Track matcher with normalization and tests (feat., remaster and live variants).
- [ ] Create, update and publish playlists. Keep the "last good snapshot" safeguard.
- [ ] Track the `x / y available` count in the playlist comment.
- [ ] Health check: a warning for missing tracks (with a setting to turn it off), and an error for playlists that can't be read.

### Phase 3 — polish

- [ ] Notification provider that re-syncs on `OnReleaseImport`.
- [ ] Optional deletion of removed playlists.
- [ ] Use MusicBrainz recording IDs for exact matching where the Navidrome song has a `musicBrainzId` (OpenSubsonic) and Lidarr knows the MBID.
- [ ] GitHub Actions release and README.

---

## 7. Risks and open questions

| Risk | Impact | Mitigation |
|---|---|---|
| Spotify changes the embed page | Scraping stops | Swappable `IPlaylistSource`, last-good snapshot, loud warnings in the Lidarr health check |
| Embed track cap (~100?) | Long playlists get cut off | Most editorial playlists are ≤100 tracks; document the limit |
| No ISRC from HTML | Wrong or missed matches | Normalization, duration check, match cache, MBIDs in phase 3 |
| Lidarr name lookup picks the wrong artist or album | Wrong downloads | Use "specific album" monitoring, rely on Lidarr's normal manual review, test with tricky names |
| Lidarr plugin API changes between versions | Plugin breaks after a Lidarr update | Pin the submodule, follow the plugins branch |
| Spotify terms of service | Scraping isn't allowed | Personal use, low request rate; the user's call |
| Plugin dependency injection and shared state between the import list and the notification | Design friction | Verify in phase 0 how plugin services are registered; fall back to doing everything in the import list |

## 8. Decisions log

| # | Question | Decision |
|---|---|---|
| 1 | One list with many playlists, or one list per playlist? | One list holds many playlists and one Navidrome connection. Use a second list instance for different profiles. |
| 2 | How are artists added? | Default: entire artist, following the metadata profile (full discography). This uses Lidarr's existing Monitor and Metadata Profile dropdowns. "Specific Album" and "None" are also supported. |
| 3 | How are missing tracks reported? | Always in the playlist comment, plus a Lidarr health warning that can be turned off. |

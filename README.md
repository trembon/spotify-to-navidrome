# Spotify to Navidrome

A Lidarr import list that reads public Spotify playlist pages, sends artists or albums to Lidarr, and mirrors the tracks already indexed by Navidrome. The plugin uses public Spotify HTML and the Navidrome Subsonic API. It needs no Spotify account or API key.

## Install

1. Use Lidarr **4.0.0.5236** with plugin support. The reference Docker package is `linuxserver/lidarr:nightly-4.0.0.5236-ls228` with .NET `10.0.15` and SQLite `3.53.4`. The reference Navidrome release is `0.64.2`.
2. In Lidarr, open **System → Plugins** and install `https://github.com/trembon/spotify-to-navidrome`. Lidarr downloads the `Lidarr.Plugin.SpotifyNavidrome.net10.0.zip` asset from [GitHub Releases](https://github.com/trembon/spotify-to-navidrome/releases).
3. After Lidarr restarts, open **Settings → Import Lists** and add **Spotify Playlist (Scraped)**. Enter a public playlist URL or 22-character Spotify playlist ID per tag, then enter the Navidrome base URL and the credentials for a dedicated playlist owner. Use a URL reachable from Lidarr, such as `http://navidrome:4533` on a shared Docker network. Include the base path if Navidrome is served under one.
4. Choose Lidarr's root folder, quality profile, metadata profile, and monitoring settings. Enable **Automatic Add** for scheduled refreshes. **Test** checks the first Spotify playlist and Navidrome authentication without creating playlists. Save and let the import-list task run, or trigger **Import List Sync** manually.

| Monitor mode | What the plugin sends to Lidarr |
| --- | --- |
| Entire Artist (default) | One entry per primary artist. Lidarr applies the metadata profile when monitoring the discography. |
| Specific Album | One entry per resolved artist and album. Tracks with unknown albums remain eligible for Navidrome matching and show a health warning. |
| None | One entry per primary artist. New artists are added unmonitored. |

**Add Featured Artists** adds guest credits in Entire Artist and None modes. Use another import-list instance when playlists need different Lidarr profiles or a different Navidrome destination. The plugin follows Lidarr's import-list schedule with a six-hour minimum between automatic runs.

## Playlists and troubleshooting

- Synced playlists are public to Navidrome users by default and start with `[Spotify] `. Each playlist contains confident matches in Spotify order, including repeated tracks. Its comment links to Spotify and reports availability and the last successful sync time. Newly downloaded tracks can match after Navidrome indexes them and Lidarr refreshes the list.
- **System → Status** shows Spotify read failures, Navidrome failures, unresolved albums, and missing tracks. Turn off **Warn About Missing Tracks** on a list to suppress its missing-track warning. Source failures and zero matches preserve any existing Navidrome tracks. Removing a source from the import list leaves its playlist in Navidrome.
- The embeds sampled returned at most 100 rows. When an embed includes 100 or more rows, the plugin checks the total from the public playlist page. It skips the refresh if the counts disagree or the total is unavailable. Personalized mixes and playlists without a readable logged-out page are outside this release's scope. Spotify's published user guidelines prohibit scraping. [Spotify User Guidelines](https://www.spotify.com/se/legal/user-guidelines/)
- Preserve `/config/spotify-to-navidrome` with your Lidarr configuration. It contains the plugin's snapshots and Navidrome playlist IDs. The Navidrome password is stored in Lidarr's provider settings. Use HTTPS for connections outside a trusted local network.

## Build and test

The build uses the pinned LinuxServer image and the .NET 10 SDK. Lidarr's upstream revision for the image is `beafd022d5c7023576f00c7b45756d5d539b7a36`. The integration tests use Docker, Python 3, and OpenSSL. They generate silent audio and run with fresh Lidarr and Navidrome data.

```powershell
pwsh -File scripts/build.ps1
dotnet run --project tests/Core.Tests/SpotifyNavidrome.Tests.csproj -c Release
python tests/integration.py
```

The ZIP is written to `.artifacts/`. Run `python tests/integration.py --from-github` to exercise Lidarr's repository install path against the published release.

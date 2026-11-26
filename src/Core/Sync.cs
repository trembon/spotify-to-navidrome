using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpotifyNavidrome;

public enum MonitorMode { None, SpecificAlbum, EntireArtist }

public sealed record ImportEntry(string Artist, string? Album = null);
public sealed record PlaylistHealth(string Name, int? Available, int? Total, bool SpotifyFailed, bool NavidromeFailed, int UnresolvedAlbums);

public sealed class ImportListState
{
    public Dictionary<string, PlaylistState> Playlists { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Albums { get; init; } = new(StringComparer.Ordinal);
}

public sealed class PlaylistState
{
    public PlaylistSnapshot? Snapshot { get; set; }
    public PlaylistHealth? Health { get; set; }
    public Dictionary<string, DestinationState> Destinations { get; init; } = new(StringComparer.Ordinal);
}

public sealed class DestinationState
{
    public string? PlaylistId { get; set; }
    public string? PendingName { get; set; }
    public Dictionary<string, string> Matches { get; set; } = new(StringComparer.Ordinal);
}

public sealed class SnapshotStore(string directory, int listId)
{
    private readonly string _path = Path.Combine(directory, $"{listId}.json");

    public ImportListState Read()
    {
        try
        {
            // On Windows readers must allow the writer's atomic rename.
            using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<ImportListState>(file) ?? throw new InvalidDataException("The sync state is empty.");
        }
        catch (FileNotFoundException) { return new ImportListState(); }
        catch (DirectoryNotFoundException) { return new ImportListState(); }
    }

    public void Write(ImportListState state)
    {
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, state);
                file.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static string DestinationKey(Uri uri, string username) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri + "\n" + username)));
}

public sealed class PlaylistRefresh(IPlaylistSource spotify, INavidrome navidrome, SnapshotStore store, Action<string> report)
{
    public ImportEntry[] Run(IEnumerable<string> ids, MonitorMode monitor, bool featured, string destinationKey,
        string owner, string prefix, bool isPublic, double threshold)
    {
        var state = store.Read();
        var entries = new List<ImportEntry>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            if (!state.Playlists.TryGetValue(id, out var playlist))
            {
                playlist = new PlaylistState();
                state.Playlists.Add(id, playlist);
            }

            PlaylistSnapshot snapshot;
            try
            {
                snapshot = spotify.ReadPlaylist(id);
            }
            catch (Exception ex) when (IsSourceFailure(ex))
            {
                report($"Could not read Spotify playlist {id} ({ex.GetType().Name}).");
                var previous = playlist.Health ?? new PlaylistHealth(playlist.Snapshot?.Name ?? id, null, null, false, false, 0);
                playlist.Health = previous with { SpotifyFailed = true };
                store.Write(state);
                continue;
            }

            playlist.Snapshot = snapshot;
            var unresolved = 0;
            if (monitor == MonitorMode.SpecificAlbum)
            {
                var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var track in snapshot.Tracks)
                {
                    if (!resolved.TryGetValue(track.Id, out var album))
                    {
                        album = track.Album ?? state.Albums.GetValueOrDefault(track.Id);
                        if (string.IsNullOrWhiteSpace(album))
                        {
                            try { album = spotify.ReadAlbum(track.Id); }
                            catch (Exception ex) when (IsSourceFailure(ex))
                            {
                                report($"Could not read Spotify track {track.Id} ({ex.GetType().Name}).");
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(album))
                        {
                            state.Albums[track.Id] = album;
                        }
                        resolved.Add(track.Id, album);
                    }

                    if (string.IsNullOrWhiteSpace(album))
                    {
                        unresolved++;
                    }
                    else
                    {
                        entries.Add(new ImportEntry(track.Artists[0], album));
                    }
                }

                snapshot = snapshot with { Tracks = snapshot.Tracks.Select(track => track with { Album = resolved[track.Id] }).ToArray() };
                playlist.Snapshot = snapshot;
            }
            else
            {
                entries.AddRange(snapshot.Tracks.SelectMany(track => featured ? track.Artists : track.Artists.Take(1))
                    .Select(artist => new ImportEntry(artist)));
            }

            playlist.Health = new PlaylistHealth(snapshot.Name, null, snapshot.Tracks.Length, false, false, unresolved);
            store.Write(state);
            if (!playlist.Destinations.TryGetValue(destinationKey, out var destination))
            {
                destination = new DestinationState();
                playlist.Destinations.Add(destinationKey, destination);
            }

            try
            {
                var matched = Synchronize(snapshot, destination, owner, prefix, isPublic, threshold, () => store.Write(state));
                playlist.Health = playlist.Health with { Available = matched };
            }
            catch (Exception ex) when (IsSourceFailure(ex))
            {
                report($"Could not sync Spotify playlist {id} to Navidrome ({ex.GetType().Name}).");
                playlist.Health = playlist.Health with { NavidromeFailed = true };
            }

            store.Write(state);
        }

        return entries.DistinctBy(entry => (entry.Artist.ToUpperInvariant(), entry.Album?.ToUpperInvariant())).ToArray();
    }

    private int Synchronize(PlaylistSnapshot snapshot, DestinationState destination, string owner, string prefix, bool isPublic,
        double threshold, Action save)
    {
        var matches = new Dictionary<string, string?>(StringComparer.Ordinal);
        var songs = new List<string>();
        foreach (var track in snapshot.Tracks)
        {
            if (!matches.TryGetValue(track.Id, out var id))
            {
                var cachedId = destination.Matches.GetValueOrDefault(track.Id);
                var cached = cachedId is null ? null : navidrome.GetSong(cachedId);
                var song = cached is null ? null : TrackMatcher.Match(track, [cached], threshold);
                song ??= TrackMatcher.Match(track, navidrome.Search(TrackMatcher.Query(track)), threshold);
                id = song?.Id;
                matches.Add(track.Id, id);
            }

            if (id is not null)
            {
                songs.Add(id);
            }
        }

        destination.Matches = matches.Where(match => match.Value is not null)
            .ToDictionary(match => match.Key, match => match.Value!, StringComparer.Ordinal);
        var existing = destination.PlaylistId is { } knownId ? navidrome.GetPlaylist(knownId) : null;
        if (existing is null && destination.PendingName is { } pending)
        {
            var staged = navidrome.GetPlaylists().SingleOrDefault(list => list.Name == pending && list.Owner == owner);
            if (staged is not null)
            {
                existing = navidrome.GetPlaylist(staged.Id) ?? throw new InvalidDataException("Navidrome staging playlist disappeared.");
                destination.PlaylistId = existing.Id;
                destination.PendingName = null;
                save();
            }
        }

        if (existing is null)
        {
            destination.PendingName ??= $"[Spotify sync pending] {Guid.NewGuid():N}";
            save();
            destination.PlaylistId = navidrome.CreatePlaylist(destination.PendingName, songs);
            destination.PendingName = null;
            save();
        }
        else
        {
            if (existing.Owner != owner)
            {
                throw new InvalidDataException("The mapped playlist belongs to another Navidrome user.");
            }

            if (songs.Count > 0 && !(existing.Entries ?? []).Select(song => song.Id).SequenceEqual(songs))
            {
                navidrome.ReplaceSongs(existing.Id, songs);
            }
        }

        var comment = $"{SpotifyHtmlSource.PlaylistUrl(snapshot.Id)} · {songs.Count}/{snapshot.Tracks.Length} tracks available · last sync {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC";
        if (songs.Count == 0 && existing?.Entries is { Length: > 0 })
        {
            comment += " · Previous tracks retained";
        }

        navidrome.UpdateMetadata(destination.PlaylistId!, prefix + snapshot.Name, comment, isPublic);
        return songs.Count;
    }

    public static bool IsSourceFailure(Exception ex) => ex is IOException or InvalidDataException or HttpRequestException or JsonException or
        OperationCanceledException or System.Text.RegularExpressions.RegexMatchTimeoutException;
}

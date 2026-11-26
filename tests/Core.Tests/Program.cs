using System.Text.Json;
using SpotifyNavidrome;

const string listId = "AAAAAAAAAAAAAAAAAAAAAA";
const string trackId = "BBBBBBBBBBBBBBBBBBBBBB";
const string missingId = "CCCCCCCCCCCCCCCCCCCCCC";
var track = new SpotifyTrack(trackId, "Une chanson (feat. Guest) - 2011 Remaster", ["Élodie", "Guest"], 210000);
var song = new NavSong("local", "Une chanson", "Elodie", null, "First Album", 210);
var missing = new SpotifyTrack(missingId, "Other Song", ["Someone"], 180000);
var fixture = new PlaylistSnapshot(listId, "Source", [track, missing, track]);

Equal(listId, SpotifyHtmlSource.PlaylistId($" https://open.spotify.com/playlist/{listId}?si=test "));
Equal(listId, SpotifyHtmlSource.PlaylistId($"https://open.spotify.com/intl-sv/playlist/{listId}"));
Equal(listId, SpotifyHtmlSource.PlaylistId(listId));
Equal(null, SpotifyHtmlSource.PlaylistId($"https://open.spotify.com.example.org/playlist/{listId}"));
Equal(null, SpotifyHtmlSource.PlaylistId($"https://other@open.spotify.com/playlist/{listId}"));

var row = new { uri = $"spotify:track:{trackId}", title = "First Track", subtitle = "Earth, Wind & Fire,\u00a0Guest", duration = 210000 };
string Html(int count) => "<script type='application/json' id='__NEXT_DATA__'>" + JsonSerializer.Serialize(new
{
    props = new { pageProps = new { state = new { data = new
    {
        entity = new { uri = $"spotify:playlist:{listId}", title = "Playlist", trackList = Enumerable.Repeat(row, count) }
    } } } }
}) + "</script>";

var parsed = SpotifyHtmlSource.ParsePlaylist(listId, Html(2));
Equal(2, parsed.Tracks.Length);
Sequence(["Earth, Wind & Fire", "Guest"], parsed.Tracks[0].Artists);
Equal(trackId, parsed.Tracks[1].Id);
Equal(100, SpotifyHtmlSource.ParsePlaylist(listId, Html(100), () => 100).Tracks.Length);
Throws<InvalidDataException>(() => SpotifyHtmlSource.ParsePlaylist(listId, Html(100), () => 109));
Throws<InvalidDataException>(() => SpotifyHtmlSource.ParsePlaylist(listId, Html(100)));
Throws<InvalidDataException>(() => SpotifyHtmlSource.ParsePlaylist(listId, Html(0)));
Throws<InvalidDataException>(() => SpotifyHtmlSource.ParsePlaylist(listId, "blocked"));
Equal("Rock & Roll", SpotifyHtmlSource.ParseTrackAlbum("""
    <meta content="Artist · Rock &amp; Roll · Song · 2026" property="og:description">
    <meta name="music:album" content="https://open.spotify.com/album/DDDDDDDDDDDDDDDDDDDDDD">
    """));

Equal(song, TrackMatcher.Match(track, [song], .85));
Equal(null, TrackMatcher.Match(track, [song, song with { Id = "another" }], .85));
Equal(null, TrackMatcher.Match(track, [song with { Title = "Une chanson (Live)" }], .85));
Equal(null, TrackMatcher.Match(track, [song with { Duration = 230 }], .85));
Equal(null, TrackMatcher.Match(track with { DurationMs = 215000 }, [song], .9));
Equal(song, TrackMatcher.Match(track with { Album = "First Album" }, [song, song with { Id = "another", Album = "Other" }], .85));

var directory = Directory.CreateTempSubdirectory("spotify-navidrome-tests-");
try
{
    var store = new SnapshotStore(directory.FullName, 1);
    var source = new FakeSpotify(fixture);
    var nav = new FakeNavidrome { Song = song };
    var changes = new PlaylistRefresh(source, nav, store, _ => { });
    ImportEntry[] Run(MonitorMode mode, bool featured = false) => changes.Run([listId, listId], mode, featured,
        "destination", "owner", "[Spotify] ", true, .85);

    nav.FailAfterCreate = true;
    Sequence([new("Élodie"), new("Someone")], Run(MonitorMode.EntireArtist));
    Equal(1, source.PlaylistReads);
    Equal(true, store.Read().Playlists[listId].Health!.NavidromeFailed);
    Sequence([new("Élodie"), new("Guest"), new("Someone")], Run(MonitorMode.None, true));
    Equal(1, nav.Creates);
    Equal(0, nav.Replacements);
    Sequence(["local", "local"], nav.Playlist!.Entries!.Select(song => song.Id));
    Equal("[Spotify] Source", nav.Playlist.Name);
    Equal(true, nav.Playlist.Public);
    Equal(2, store.Read().Playlists[listId].Health!.Available);

    source.Fail = true;
    Equal(0, Run(MonitorMode.EntireArtist).Length);
    Equal(true, store.Read().Playlists[listId].Health!.SpotifyFailed);
    Equal(3, store.Read().Playlists[listId].Snapshot!.Tracks.Length);
    Equal(0, nav.Replacements);

    source.Fail = false;
    nav.Song = null;
    Run(MonitorMode.EntireArtist);
    Equal(0, nav.Replacements);
    Equal(0, store.Read().Playlists[listId].Health!.Available);
    Equal(0, store.Read().Playlists[listId].Destinations["destination"].Matches.Count);

    nav.Song = song with { Id = "replacement" };
    nav.Other = new NavSong("added", "Other Song", "Someone", null, null, 180);
    Sequence([new("Élodie", "First Album")], Run(MonitorMode.SpecificAlbum));
    Equal(2, source.AlbumReads);
    Equal(1, store.Read().Playlists[listId].Health!.UnresolvedAlbums);
    Equal(3, store.Read().Playlists[listId].Health!.Available);
    Sequence(["replacement", "added", "replacement"], nav.Playlist.Entries!.Select(song => song.Id));
    Run(MonitorMode.SpecificAlbum);
    Equal(3, source.AlbumReads);
    Equal(1, nav.Replacements);
}
finally { directory.Delete(recursive: true); }

Console.WriteLine("Passed parser, matcher, monitoring, refresh, and recovery checks.");

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}

static void Sequence<T>(T[] expected, IEnumerable<T> actual)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException($"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }
}

static void Throws<T>(Action run) where T : Exception
{
    try { run(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class FakeSpotify(PlaylistSnapshot snapshot) : IPlaylistSource
{
    public int PlaylistReads { get; private set; }
    public int AlbumReads { get; private set; }
    public bool Fail { get; set; }

    public PlaylistSnapshot ReadPlaylist(string id)
    {
        PlaylistReads++;
        return Fail ? throw new InvalidDataException("Source unavailable") : snapshot;
    }

    public string? ReadAlbum(string id)
    {
        AlbumReads++;
        return id == snapshot.Tracks[0].Id ? "First Album" : null;
    }
}

sealed class FakeNavidrome : INavidrome
{
    public NavPlaylist? Playlist { get; private set; }
    public NavSong? Song { get; set; }
    public NavSong? Other { get; set; }
    public bool FailAfterCreate { get; set; }
    public int Creates { get; private set; }
    public int Replacements { get; private set; }

    public void Ping() { }
    public NavSong[] Search(string query) => (query.StartsWith("Someone ") ? Other : Song) is { } song ? [song] : [];
    public NavSong? GetSong(string id) => new[] { Song, Other }.FirstOrDefault(song => song?.Id == id);
    public NavPlaylist? GetPlaylist(string id) => Playlist?.Id == id ? Playlist : null;
    public NavPlaylist[] GetPlaylists() => Playlist is null ? [] : [Playlist];

    public string CreatePlaylist(string name, IReadOnlyList<string> songs)
    {
        Creates++;
        Playlist = new NavPlaylist("playlist", name, null, "owner", false, songs.Select(id => GetSong(id)!).ToArray());
        if (FailAfterCreate)
        {
            FailAfterCreate = false;
            throw new HttpRequestException("Connection dropped after create");
        }

        return Playlist.Id;
    }

    public void ReplaceSongs(string id, IReadOnlyList<string> songs)
    {
        Replacements++;
        Playlist = Playlist! with { Entries = songs.Select(id => GetSong(id)!).ToArray() };
    }

    public void UpdateMetadata(string id, string name, string comment, bool isPublic) =>
        Playlist = Playlist! with { Name = name, Comment = comment, Public = isPublic };
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SpotifyNavidrome;

public sealed record NavSong(string Id, string? Title, string? Artist, string? AlbumArtist, string? Album, int Duration);
public sealed record NavPlaylist(string Id, string? Name, string? Comment, string? Owner, bool Public,
    [property: JsonPropertyName("entry")] NavSong[]? Entries);

public interface INavidrome
{
    void Ping();
    NavSong[] Search(string query);
    NavSong? GetSong(string id);
    NavPlaylist? GetPlaylist(string id);
    NavPlaylist[] GetPlaylists();
    string CreatePlaylist(string name, IReadOnlyList<string> songs);
    void ReplaceSongs(string id, IReadOnlyList<string> songs);
    void UpdateMetadata(string id, string name, string comment, bool isPublic);
}

public sealed class SubsonicClient(HttpClient http, Uri baseUri, string user, string password) : INavidrome
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Uri? ServerUri(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Host.Length == 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            return null;
        }

        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    public void Ping() => Call("ping");

    public NavSong[] Search(string query) => Read<NavSong[]>(Call("search3",
        [("query", query), ("artistCount", "0"), ("albumCount", "0"), ("songCount", "20")]), "searchResult3", "song") ?? [];

    public NavSong? GetSong(string id) => Read<NavSong>(Call("getSong", [("id", id)], allowMissing: true), "song");

    public NavPlaylist? GetPlaylist(string id) => Read<NavPlaylist>(Call("getPlaylist", [("id", id)], allowMissing: true), "playlist");

    public NavPlaylist[] GetPlaylists() => Read<NavPlaylist[]>(Call("getPlaylists"), "playlists", "playlist") ?? [];

    public string CreatePlaylist(string name, IReadOnlyList<string> songs) =>
        Read<NavPlaylist>(Call("createPlaylist", SongParameters("name", name, songs)), "playlist")?.Id
        ?? throw new InvalidDataException("Navidrome did not return the created playlist ID.");

    public void ReplaceSongs(string id, IReadOnlyList<string> songs) => Call("createPlaylist", SongParameters("playlistId", id, songs));

    public void UpdateMetadata(string id, string name, string comment, bool isPublic) => Call("updatePlaylist",
        [("playlistId", id), ("name", name), ("comment", comment), ("public", isPublic ? "true" : "false")]);

    private static IEnumerable<(string, string)> SongParameters(string key, string value, IReadOnlyList<string> songs) =>
        new[] { (key, value) }.Concat(songs.Select(id => ("songId", id)));

    private JsonElement Call(string endpoint, IEnumerable<(string Name, string Value)>? parameters = null, bool allowMissing = false)
    {
        var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var token = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(password + salt)));
        (string Name, string Value)[] auth = [("u", user), ("t", token), ("s", salt), ("v", "1.16.1"), ("c", "lidarr-spotify"), ("f", "json")];
        using var body = new FormUrlEncodedContent(auth.Concat(parameters ?? []).Select(pair =>
            new KeyValuePair<string, string>(pair.Name, pair.Value)));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, $"rest/{endpoint}.view")) { Content = body };
        using var reply = http.Send(request);
        if (!reply.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Navidrome {endpoint} returned HTTP {(int)reply.StatusCode}.");
        }

        using var stream = reply.Content.ReadAsStream();
        using var json = JsonDocument.Parse(stream);
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("subsonic-response", out var response) || response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Navidrome {endpoint} returned an invalid response.");
        }

        if (status.GetString() == "ok")
        {
            return response.Clone();
        }

        var code = response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;
        if (allowMissing && code == 70)
        {
            return default;
        }

        throw new InvalidDataException($"Navidrome {endpoint} failed (Subsonic error {code}).");
    }

    private static T? Read<T>(JsonElement value, params string[] path)
    {
        foreach (var property in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out value))
            {
                return default;
            }
        }

        return value.Deserialize<T>(JsonOptions);
    }
}

public static partial class TrackMatcher
{
    public static NavSong? Match(SpotifyTrack track, IEnumerable<NavSong> candidates, double threshold)
    {
        var ranked = candidates.Where(song => !string.IsNullOrWhiteSpace(song.Id)).DistinctBy(song => song.Id)
            .Select(song => (Song: song, Score: Score(track, song))).OrderByDescending(item => item.Score).Take(2).ToArray();
        return ranked.Length > 0 && ranked[0].Score >= threshold &&
            (ranked.Length == 1 || ranked[0].Score - ranked[1].Score >= 0.04999) ? ranked[0].Song : null;
    }

    public static string Query(SpotifyTrack track) => $"{track.Artists[0]} {Decorations().Replace(track.Title, "").Trim()}";

    private static double Score(SpotifyTrack track, NavSong song)
    {
        if (string.IsNullOrWhiteSpace(song.Title) || VersionKey(track.Title) != VersionKey(song.Title) ||
            !track.Artists.Any(artist => Normalize(artist) == Normalize(song.Artist) || Normalize(artist) == Normalize(song.AlbumArtist)))
        {
            return -1;
        }

        var sourceTitle = Normalize(Decorations().Replace(track.Title, ""));
        var candidateTitle = Normalize(Decorations().Replace(song.Title, ""));
        if (sourceTitle.Length == 0 || sourceTitle != candidateTitle)
        {
            return -1;
        }

        var durationMs = Math.Abs((long)track.DurationMs - song.Duration * 1000L);
        var durationsKnown = track.DurationMs > 0 && song.Duration > 0;
        if (durationsKnown && durationMs > 10000)
        {
            return -1;
        }

        var album = Normalize(track.Album);
        return 0.85 + (durationsKnown && durationMs <= 3000 ? 0.10 : 0) +
            (album.Length > 0 && album == Normalize(song.Album) ? 0.05 : 0);
    }

    private static string VersionKey(string title) => string.Join(' ', VersionSuffix().Matches(title)
        .Select(match => Normalize(match.Groups["tag"].Value)).Distinct().Order(StringComparer.Ordinal));

    private static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "";
        }

        var builder = new StringBuilder();
        foreach (var rune in input.Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                builder.Append(Rune.ToLowerInvariant(rune));
            }
            else if (Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.DashPunctuation)
            {
                builder.Append(' ');
            }
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [GeneratedRegex(@"\s*[\[(](?:feat\.?|ft\.?|featuring)\s+[^)\]]+[)\]]|\s+[-–—]\s+(?:feat\.?|ft\.?|featuring)\s+.+$|\s+(?:[-–—]\s+|[\[(])?(?:(?:\d{4}\s+)?remaster(?:ed)?(?:\s+\d{4})?)[)\]]?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Decorations();

    [GeneratedRegex(@"(?:[\[(]|\s[-–—]\s)\s*(?<tag>[^)\]]*\b(?:live|acoustic|remix|edit|demo|instrumental|karaoke|version)\b[^)\]]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionSuffix();
}

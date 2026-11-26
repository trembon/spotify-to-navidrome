using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpotifyNavidrome;

public sealed record SpotifyTrack(string Id, string Title, string[] Artists, int DurationMs, string? Album = null);
public sealed record PlaylistSnapshot(string Id, string Name, SpotifyTrack[] Tracks);

public interface IPlaylistSource
{
    PlaylistSnapshot ReadPlaylist(string id);
    string? ReadAlbum(string trackId);
}

public sealed class SpotifyHtmlSource(Func<Uri, string> download) : IPlaylistSource
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex IdPattern = new(@"\A[A-Za-z0-9]{22}\z", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex PlaylistPath = new(@"\A/(?:intl-[a-z-]+/)?playlist/(?<id>[A-Za-z0-9]{22})/?\z",
        RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Scripts = new("<script\\b(?<attributes>[^>]*)>(?<body>.*?)</script\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
    private static readonly Regex MetaTags = new("<meta\\b(?<attributes>[^>]*)>", RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex Attributes = new("(?<name>[\\w:-]+)\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)')", RegexOptions.CultureInvariant, RegexTimeout);

    public static string? PlaylistId(string input)
    {
        input = input.Trim();
        if (IdPattern.IsMatch(input))
        {
            return input;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != "open.spotify.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
        {
            return null;
        }

        var path = PlaylistPath.Match(uri.AbsolutePath);
        return path.Success ? path.Groups["id"].Value : null;
    }

    public static string PlaylistUrl(string id) => $"https://open.spotify.com/playlist/{id}";

    public PlaylistSnapshot ReadPlaylist(string id)
    {
        if (!IdPattern.IsMatch(id))
        {
            throw new InvalidDataException("Invalid Spotify playlist ID.");
        }

        return ParsePlaylist(id, download(new Uri($"https://open.spotify.com/embed/playlist/{id}")), () =>
            ReadPageTrackCount(download(new Uri(PlaylistUrl(id)))));
    }

    public string? ReadAlbum(string trackId) => IdPattern.IsMatch(trackId)
        ? ParseTrackAlbum(download(new Uri($"https://open.spotify.com/track/{trackId}"))) : null;

    public static PlaylistSnapshot ParsePlaylist(string id, string html, Func<int?>? readPageTrackCount = null)
    {
        var script = Scripts.Matches(html).FirstOrDefault(tag => Attribute(tag, "id") == "__NEXT_DATA__");
        if (script is null)
        {
            throw new InvalidDataException("Spotify did not return its playlist data.");
        }

        using var json = JsonDocument.Parse(script.Groups["body"].Value);
        var entity = json.RootElement;
        foreach (var property in new[] { "props", "pageProps", "state", "data", "entity" })
        {
            entity = Property(entity, property);
        }

        var name = ReadText(entity, "title") ?? ReadText(entity, "name");
        var tracks = Property(entity, "trackList");
        if (ReadText(entity, "uri") != $"spotify:playlist:{id}" || string.IsNullOrWhiteSpace(name) ||
            tracks.ValueKind != JsonValueKind.Array || tracks.GetArrayLength() == 0)
        {
            throw new InvalidDataException("Spotify returned an empty or unexpected playlist.");
        }

        foreach (var countName in new[] { "total", "totalCount", "trackCount", "totalTracks" })
        {
            if (ReadNumber(entity, countName) > tracks.GetArrayLength())
            {
                throw new InvalidDataException("Spotify returned only part of the playlist.");
            }
        }

        if (Property(entity, "hasMore").ValueKind == JsonValueKind.True)
        {
            throw new InvalidDataException("Spotify returned only part of the playlist.");
        }

        // The embed returns at most 100 rows in the public pages sampled for v0.1.0.
        if (tracks.GetArrayLength() >= 100 && readPageTrackCount?.Invoke() != tracks.GetArrayLength())
        {
            throw new InvalidDataException("Could not verify the full Spotify playlist at the embed track limit.");
        }

        var result = new List<SpotifyTrack>();
        foreach (var track in tracks.EnumerateArray())
        {
            var uri = ReadText(track, "uri") ?? "";
            if (uri.StartsWith("spotify:episode:", StringComparison.Ordinal))
            {
                continue;
            }

            const string prefix = "spotify:track:";
            var title = ReadText(track, "title");
            var artists = ReadArtists(track);
            if (!uri.StartsWith(prefix, StringComparison.Ordinal) || !IdPattern.IsMatch(uri[prefix.Length..]) ||
                string.IsNullOrWhiteSpace(title) || artists.Length == 0)
            {
                throw new InvalidDataException("Spotify returned a music entry with incomplete metadata.");
            }

            var album = ReadText(Property(track, "album"), "name") ?? ReadText(track, "albumName");
            result.Add(new SpotifyTrack(uri[prefix.Length..], title, artists, ReadNumber(track, "duration"), album));
        }

        if (result.Count == 0)
        {
            throw new InvalidDataException("Spotify returned no music tracks.");
        }

        return new PlaylistSnapshot(id, name, result.ToArray());
    }

    public static string? ParseTrackAlbum(string html)
    {
        var tags = MetaTags.Matches(html);
        var albumTag = tags.FirstOrDefault(tag => (Attribute(tag, "name") ?? Attribute(tag, "property")) == "music:album");
        var descriptionTag = tags.FirstOrDefault(tag => Attribute(tag, "property") == "og:description");
        if (albumTag is null || descriptionTag is null ||
            !Uri.TryCreate(Attribute(albumTag, "content"), UriKind.Absolute, out var albumUri) ||
            albumUri.Host != "open.spotify.com" || !albumUri.AbsolutePath.StartsWith("/album/", StringComparison.Ordinal))
        {
            return null;
        }

        var description = Attribute(descriptionTag, "content")?.Split(" · ", StringSplitOptions.TrimEntries);
        return description is [_, { Length: > 0 } album, "Song", var year] &&
            int.TryParse(year, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? album : null;
    }

    private static int? ReadPageTrackCount(string html)
    {
        var tag = MetaTags.Matches(html).FirstOrDefault(tag => Attribute(tag, "name") == "music:song_count");
        return tag is not null && int.TryParse(Attribute(tag, "content"), NumberStyles.None,
            CultureInfo.InvariantCulture, out var count) ? count : null;
    }

    private static string[] ReadArtists(JsonElement track)
    {
        var artists = Property(track, "artists");
        if (artists.ValueKind == JsonValueKind.Array)
        {
            var names = artists.EnumerateArray().Select(artist => ReadText(artist, "name"))
                .OfType<string>().Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
            if (names.Length > 0)
            {
                return names;
            }
        }

        return ReadText(track, "subtitle")?.Split(",\u00a0", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
    }

    private static string? Attribute(Match tag, string name) => Attributes.Matches(tag.Groups["attributes"].Value)
        .Where(attribute => attribute.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
        .Select(attribute => WebUtility.HtmlDecode(attribute.Groups["value"].Value)).FirstOrDefault();

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? ReadText(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static int ReadNumber(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : 0;
}

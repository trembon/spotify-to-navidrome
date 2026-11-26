using System.Collections.Concurrent;
using System.Net;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;

namespace SpotifyNavidrome.Lidarr;

public sealed class SpotifyNavidromeImportList(
    IImportListStatusService status,
    IConfigService config,
    IParsingService parsing,
    IAppFolderInfo folders,
    IEventAggregator events,
    Logger logger) : ImportListBase<SpotifyNavidromeImportListSettings>(status, config, parsing, logger)
{
    private static readonly ConcurrentDictionary<int, object> Locks = new();
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = 10 * 1024 * 1024
    };

    private static readonly SpotifyHtmlSource Spotify = new(DownloadSpotify);

    public override string Name => "Spotify Playlist (Scraped)";
    public override ImportListType ListType => ImportListType.Spotify;
    public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(6);

    public override IEnumerable<ProviderDefinition> DefaultDefinitions =>
        base.DefaultDefinitions.OfType<ImportListDefinition>().Select(definition =>
        {
            definition.ShouldMonitor = ImportListMonitorType.EntireArtist;
            return definition;
        });

    public override IList<ImportListItemInfo> Fetch()
    {
        if (!Settings.Validate().IsValid)
        {
            _logger.Warn("Spotify import list {0} has invalid settings. Open the list and run Test.", Definition.Id);
            return [];
        }

        lock (Locks.GetOrAdd(Definition.Id, _ => new object()))
        {
            try
            {
                var uri = SubsonicClient.ServerUri(Settings.NavidromeUrl)!;
                var owner = Settings.Username.Trim();
                var client = new SubsonicClient(Http, uri, owner, Settings.Password);
                var refresh = new PlaylistRefresh(Spotify, client, OpenStore(folders, Definition.Id), message => _logger.Warn(message));
                var monitor = ((ImportListDefinition)Definition).ShouldMonitor switch
                {
                    ImportListMonitorType.EntireArtist => MonitorMode.EntireArtist,
                    ImportListMonitorType.SpecificAlbum => MonitorMode.SpecificAlbum,
                    _ => MonitorMode.None
                };

                var entries = refresh.Run(Settings.PlaylistIds, monitor, Settings.AddFeaturedArtists,
                    SnapshotStore.DestinationKey(uri, owner), owner, Settings.Prefix ?? "", Settings.MakePublic, Settings.MatchThreshold);
                return CleanupListItems(entries.Select(entry => new ImportListItemInfo { Artist = entry.Artist, Album = entry.Album }));
            }
            finally
            {
                events.PublishEvent(new SpotifySyncFinishedEvent());
            }
        }
    }

    protected override void Test(List<ValidationFailure> failures)
    {
        failures.AddRange(Settings.Validate().Errors);
        if (failures.Count > 0)
        {
            return;
        }

        try { Spotify.ReadPlaylist(Settings.PlaylistIds[0]); }
        catch (Exception ex) when (PlaylistRefresh.IsSourceFailure(ex))
        {
            failures.Add(new ValidationFailure(nameof(Settings.Playlists),
                "Spotify did not return a readable, complete public playlist. Check the first URL and retry."));
        }

        try
        {
            new SubsonicClient(Http, SubsonicClient.ServerUri(Settings.NavidromeUrl)!, Settings.Username.Trim(), Settings.Password).Ping();
        }
        catch (Exception ex) when (PlaylistRefresh.IsSourceFailure(ex))
        {
            failures.Add(new ValidationFailure(nameof(Settings.NavidromeUrl), "Navidrome ping failed. Check the URL and credentials."));
        }
    }

    internal static SnapshotStore OpenStore(IAppFolderInfo folders, int id) =>
        new(Path.Combine(folders.AppDataFolder, "spotify-to-navidrome"), id);

    private static string DownloadSpotify(Uri address)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.UserAgent.ParseAdd("spotify-to-navidrome/0.1.0");
        request.Headers.Accept.ParseAdd("text/html");
        request.Headers.AcceptLanguage.ParseAdd("en-US");
        using var response = Http.Send(request);
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }
}

public sealed class SpotifySyncFinishedEvent : IEvent;

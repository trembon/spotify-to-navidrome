using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Localization;

namespace SpotifyNavidrome.Lidarr;

public abstract class SpotifyHealthCheckBase(ILocalizationService localization, IImportListFactory lists, IAppFolderInfo folders)
    : HealthCheckBase(localization)
{
    protected IEnumerable<(string List, bool Warn, PlaylistHealth State)> ReadStatus()
    {
        foreach (var list in lists.All().Where(list => list.Enable && list.Settings is SpotifyNavidromeImportListSettings))
        {
            var settings = (SpotifyNavidromeImportListSettings)list.Settings;
            if (!settings.Validate().IsValid)
            {
                yield return (list.Name, true, new PlaylistHealth("invalid list settings", null, null, true, false, 0));
                continue;
            }

            var state = SpotifyNavidromeImportList.OpenStore(folders, list.Id).Read();
            foreach (var id in settings.PlaylistIds)
            {
                if (state.Playlists.GetValueOrDefault(id)?.Health is { } health)
                {
                    yield return (list.Name, settings.WarnAboutMissingTracks, health);
                }
            }
        }
    }

    protected HealthCheck Result(HealthCheckResult level, string message) => new(GetType(), level,
        HealthCheckReason.ImportListStatusUnavailable, message)
    {
        WikiUrl = new HttpUri("https://github.com/trembon/spotify-to-navidrome#playlists-and-troubleshooting")
    };
}

[CheckOn(typeof(SpotifySyncFinishedEvent))]
public sealed class SpotifySyncHealthCheck(ILocalizationService localization, IImportListFactory lists, IAppFolderInfo folders)
    : SpotifyHealthCheckBase(localization, lists, folders)
{
    public override HealthCheck Check()
    {
        try
        {
            var failures = ReadStatus().Where(entry => entry.State.SpotifyFailed || entry.State.NavidromeFailed)
                .Select(entry => $"{entry.List} / {entry.State.Name} ({(entry.State.SpotifyFailed ? "Spotify read" : "Navidrome sync")})").ToArray();
            return failures.Length == 0 ? new HealthCheck(GetType()) : Result(HealthCheckResult.Error,
                $"Spotify playlists could not refresh: {string.Join(", ", failures)}. See Lidarr logs and Settings → Import Lists.");
        }
        catch (Exception ex) when (PlaylistRefresh.IsSourceFailure(ex) || ex is UnauthorizedAccessException)
        {
            return Result(HealthCheckResult.Error, "Could not read Spotify playlist sync state in Lidarr AppData.");
        }
    }
}

[CheckOn(typeof(SpotifySyncFinishedEvent))]
public sealed class SpotifyMissingTracksHealthCheck(ILocalizationService localization, IImportListFactory lists, IAppFolderInfo folders)
    : SpotifyHealthCheckBase(localization, lists, folders)
{
    public override HealthCheck Check()
    {
        try
        {
            var issues = new List<string>();
            foreach (var entry in ReadStatus().Where(entry => !entry.State.SpotifyFailed))
            {
                var state = entry.State;
                if (entry.Warn && state.Available is int available && state.Total is int total && available < total)
                {
                    issues.Add($"{state.Name}: {total - available} missing from Navidrome");
                }

                if (state.UnresolvedAlbums > 0)
                {
                    issues.Add($"{state.Name}: {state.UnresolvedAlbums} tracks with unknown albums");
                }
            }

            return issues.Count == 0 ? new HealthCheck(GetType()) : Result(HealthCheckResult.Warning,
                $"Spotify playlists: {string.Join(", ", issues)}.");
        }
        catch (Exception ex) when (PlaylistRefresh.IsSourceFailure(ex) || ex is UnauthorizedAccessException)
        {
            return new HealthCheck(GetType());
        }
    }
}

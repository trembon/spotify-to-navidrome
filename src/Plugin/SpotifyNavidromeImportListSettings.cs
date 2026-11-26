using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Validation;

namespace SpotifyNavidrome.Lidarr;

public sealed class SpotifyNavidromeImportListSettings : IImportListSettings
{
    public string BaseUrl { get; set; } = "https://open.spotify.com";

    [FieldDefinition(0, Label = "Spotify Playlists", Type = FieldType.Tag, HelpText = "Enter one public Spotify playlist URL or ID per tag.")]
    public IEnumerable<string> Playlists { get; set; } = [];

    [FieldDefinition(1, Label = "Add Featured Artists", Type = FieldType.Checkbox, HelpText = "Applies to Entire Artist and None monitoring modes.")]
    public bool AddFeaturedArtists { get; set; }

    [FieldDefinition(2, Label = "Navidrome URL", Type = FieldType.Textbox, HelpText = "The base URL reachable from Lidarr, including any reverse-proxy base path.")]
    public string NavidromeUrl { get; set; } = "";

    [FieldDefinition(3, Label = "Navidrome Username", Type = FieldType.Textbox)]
    public string Username { get; set; } = "";

    [FieldDefinition(4, Label = "Navidrome Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
    public string Password { get; set; } = "";

    [FieldDefinition(5, Label = "Playlist Name Prefix", Type = FieldType.Textbox)]
    public string Prefix { get; set; } = "[Spotify] ";

    [FieldDefinition(6, Label = "Make Playlists Public", Type = FieldType.Checkbox)]
    public bool MakePublic { get; set; } = true;

    [FieldDefinition(7, Label = "Match Threshold", Type = FieldType.Number, HelpText = "Confidence required between 0 and 1.")]
    public double MatchThreshold { get; set; } = 0.85;

    [FieldDefinition(8, Label = "Warn About Missing Tracks", Type = FieldType.Checkbox)]
    public bool WarnAboutMissingTracks { get; set; } = true;

    internal string[] PlaylistIds => (Playlists ?? []).Select(SpotifyHtmlSource.PlaylistId).OfType<string>()
        .Distinct(StringComparer.Ordinal).ToArray();

    public NzbDroneValidationResult Validate() => new(Validator.Instance.Validate(this));

    private sealed class Validator : AbstractValidator<SpotifyNavidromeImportListSettings>
    {
        public static readonly Validator Instance = new();

        public Validator()
        {
            RuleFor(settings => settings.Playlists).NotEmpty();
            RuleForEach(settings => settings.Playlists).Must(input => input is not null && SpotifyHtmlSource.PlaylistId(input) is not null)
                .WithMessage("Use a public open.spotify.com playlist URL or its 22-character ID.");
            RuleFor(settings => settings.NavidromeUrl).Must(url => url is not null && SubsonicClient.ServerUri(url) is not null)
                .WithMessage("Enter an absolute HTTP or HTTPS Navidrome base URL.");
            RuleFor(settings => settings.Username).NotEmpty();
            RuleFor(settings => settings.Password).NotEmpty();
            RuleFor(settings => settings.MatchThreshold).InclusiveBetween(0, 1);
        }
    }
}

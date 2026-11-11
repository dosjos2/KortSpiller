using System.Text.RegularExpressions;

namespace KortSpiller.Core;

/// <summary>En Spotify-lenke (URL eller URI) brutt ned i type og id.</summary>
public sealed partial record SpotifyLink(string Kind, string Id)
{
    private static readonly HashSet<string> Kinds = new(StringComparer.OrdinalIgnoreCase)
        { "track", "album", "playlist", "artist", "episode", "show" };

    public string Uri => $"spotify:{Kind}:{Id}";

    /// <summary>Spor og episoder spilles som enkeltelementer, resten som kontekst (album, spilleliste ...).</summary>
    public bool IsSingleItem => Kind is "track" or "episode";

    public static bool TryParse(string? input, out SpotifyLink link)
    {
        link = null!;
        if (string.IsNullOrWhiteSpace(input)) return false;
        input = input.Trim();

        var uri = UriRegex().Match(input);
        if (uri.Success && Kinds.Contains(uri.Groups["kind"].Value))
        {
            link = new SpotifyLink(uri.Groups["kind"].Value.ToLowerInvariant(), uri.Groups["id"].Value);
            return true;
        }

        var url = UrlRegex().Match(input);
        if (url.Success && Kinds.Contains(url.Groups["kind"].Value))
        {
            link = new SpotifyLink(url.Groups["kind"].Value.ToLowerInvariant(), url.Groups["id"].Value);
            return true;
        }
        return false;
    }

    public static SpotifyLink Parse(string input) =>
        TryParse(input, out var link) ? link : throw new FormatException($"Ugyldig Spotify-lenke: {input}");

    [GeneratedRegex(@"^spotify:(?:user:[^:]+:)?(?<kind>[a-z]+):(?<id>[0-9A-Za-z]{22})$", RegexOptions.IgnoreCase)]
    private static partial Regex UriRegex();

    [GeneratedRegex(@"^https?://open\.spotify\.com/(?:intl-[a-z-]+/)?(?:embed/)?(?<kind>[a-z]+)/(?<id>[0-9A-Za-z]{22})(?:[/?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}

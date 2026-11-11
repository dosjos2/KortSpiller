namespace KortSpiller.Player;

public sealed class SpotifyOptions
{
    public string ClientId { get; set; } = "";
    public string DeviceName { get; set; } = "Kortspiller";
    public string TokenFile { get; set; } = "data/spotify-token.json";
    public string RedirectUri { get; set; } = "http://127.0.0.1:8888/callback";
    public string ApiBase { get; set; } = "https://api.spotify.com/v1";
    public string AccountsBase { get; set; } = "https://accounts.spotify.com";
    public int DeviceRetries { get; set; } = 15;
    public int DeviceRetryDelayMs { get; set; } = 2000;
}

public sealed class MappingOptions
{
    /// <summary>URL (http/https) eller lokal filsti til mappings.json.</summary>
    public string Source { get; set; } = "data/mappings.json";
    public string CacheFile { get; set; } = "data/mappings.cache.json";
    public int RefreshSeconds { get; set; } = 60;
    public string? BearerToken { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
}

public sealed class VolumeOptions
{
    public bool Enabled { get; set; }
    public int SpiBus { get; set; }
    public int ChipSelect { get; set; } = 1;
    public int Channel { get; set; }
    public int DefaultPercent { get; set; } = 40;
    public int MinStepPercent { get; set; } = 2;
}

public sealed class PinOptions
{
    public bool Enabled { get; set; }
    public int Pin { get; set; }
}

public sealed class PlayerOptions
{
    public List<Core.ReaderOptions> Readers { get; set; } = [];
    public double RescanGapSeconds { get; set; } = 1.2;
}

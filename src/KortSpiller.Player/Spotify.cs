using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace KortSpiller.Player;

public sealed class SpotifyToken
{
    [JsonPropertyName("refreshToken")] public string RefreshToken { get; set; } = "";
    [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class TokenStore(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _lock = new();

    public SpotifyToken? Load()
    {
        lock (_lock)
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<SpotifyToken>(File.ReadAllText(path));
        }
    }

    public void Save(SpotifyToken token)
    {
        lock (_lock)
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var tmp = full + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(token, Json));
            File.Move(tmp, full, overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

public sealed class SpotifyException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public sealed class SpotifyClient
{
    public const string Scopes = "user-modify-playback-state user-read-playback-state";

    private readonly HttpClient _http;
    private readonly SpotifyOptions _options;
    private readonly TokenStore _tokens;
    private readonly ILogger<SpotifyClient> _log;
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private SpotifyToken? _token;
    private string? _deviceId;

    public SpotifyClient(HttpClient http, IOptions<SpotifyOptions> options, TokenStore tokens, ILogger<SpotifyClient> log)
    {
        _http = http; _options = options.Value; _tokens = tokens; _log = log;
    }

    public async Task PlayAsync(string uri, CancellationToken ct)
    {
        object body = uri.StartsWith("spotify:track:") || uri.StartsWith("spotify:episode:")
            ? new { uris = new[] { uri }, context_uri = (string?)null }
            : new { uris = (string[]?)null, context_uri = (string?)uri };
        await OnDeviceAsync(HttpMethod.Put, "/me/player/play", null, JsonContent(body), ct);
    }

    public Task ResumeAsync(CancellationToken ct) => OnDeviceAsync(HttpMethod.Put, "/me/player/play", null, null, ct);
    public Task PauseAsync(CancellationToken ct) => OnDeviceAsync(HttpMethod.Put, "/me/player/pause", null, null, ct);
    public Task NextAsync(CancellationToken ct) => OnDeviceAsync(HttpMethod.Post, "/me/player/next", null, null, ct);
    public Task PreviousAsync(CancellationToken ct) => OnDeviceAsync(HttpMethod.Post, "/me/player/previous", null, null, ct);

    public Task SetVolumeAsync(int percent, CancellationToken ct) =>
        OnDeviceAsync(HttpMethod.Put, "/me/player/volume", $"volume_percent={Math.Clamp(percent, 0, 100)}", null, ct);

    public async Task ToggleAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "/me/player", null, null, ct);
        var playing = false;
        if (response.StatusCode == HttpStatusCode.OK)
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            playing = doc.RootElement.TryGetProperty("is_playing", out var p) && p.GetBoolean();
        }
        await (playing ? PauseAsync(ct) : ResumeAsync(ct));
    }

    public async Task RunActionAsync(string action, CancellationToken ct)
    {
        switch (action)
        {
            case "pause": await PauseAsync(ct); break;
            case "resume": await ResumeAsync(ct); break;
            case "toggle": await ToggleAsync(ct); break;
            case "next": await NextAsync(ct); break;
            case "previous": await PreviousAsync(ct); break;
            default: throw new SpotifyException($"Ukjent kommando '{action}'.");
        }
    }

    private async Task OnDeviceAsync(HttpMethod method, string path, string? query, HttpContent? content, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var device = await ResolveDeviceAsync(ct);
            var q = $"device_id={Uri.EscapeDataString(device)}" + (query is null ? "" : "&" + query);
            using var response = await SendAsync(method, path, q, content, ct);
            if (response.IsSuccessStatusCode) return;
            if (response.StatusCode == HttpStatusCode.NotFound && attempt == 0)
            {
                _deviceId = null; // enheten kan ha fått ny id etter omstart av librespot
                continue;
            }
            throw new SpotifyException($"Spotify svarte {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}", response.StatusCode);
        }
    }

    private async Task<string> ResolveDeviceAsync(CancellationToken ct)
    {
        if (_deviceId is not null) return _deviceId;
        for (var i = 0; i < Math.Max(1, _options.DeviceRetries); i++)
        {
            using var response = await SendAsync(HttpMethod.Get, "/me/player/devices", null, null, ct);
            if (response.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                foreach (var d in doc.RootElement.GetProperty("devices").EnumerateArray())
                {
                    if (string.Equals(d.GetProperty("name").GetString(), _options.DeviceName, StringComparison.OrdinalIgnoreCase)
                        && d.GetProperty("id").GetString() is { } id)
                        return _deviceId = id;
                }
            }
            _log.LogInformation("Spotify-enheten «{Name}» er ikke synlig ennå (forsøk {N}).", _options.DeviceName, i + 1);
            await Task.Delay(_options.DeviceRetryDelayMs, ct);
        }
        throw new SpotifyException($"Fant ikke Spotify-enheten «{_options.DeviceName}». Kjører raspotify, og har du valgt enheten én gang fra Spotify-appen?");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? query, HttpContent? content, CancellationToken ct)
    {
        var url = _options.ApiBase.TrimEnd('/') + path + (string.IsNullOrEmpty(query) ? "" : "?" + query);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url) { Content = content is null ? null : await Clone(content) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(attempt > 0, ct));
            if (content is null && method != HttpMethod.Get) request.Content = new ByteArrayContent([]) { Headers = { ContentLength = 0 } };
            var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { response.Dispose(); continue; }
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                response.Dispose();
                await Task.Delay(wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait, ct);
                continue;
            }
            return response;
        }
    }

    private static async Task<HttpContent> Clone(HttpContent content)
    {
        var copy = new ByteArrayContent(await content.ReadAsByteArrayAsync());
        foreach (var h in content.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return copy;
    }

    private static StringContent JsonContent(object body) =>
        new(JsonSerializer.Serialize(body, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }),
            Encoding.UTF8, "application/json");

    private async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        await _authGate.WaitAsync(ct);
        try
        {
            _token ??= _tokens.Load() ?? throw new SpotifyException(
                $"Ingen Spotify-innlogging funnet ({_options.TokenFile}). Kjør «KortSpiller.Player auth» først.");
            if (!forceRefresh && _token.AccessToken is not null && _token.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60))
                return _token.AccessToken;

            using var response = await _http.PostAsync(_options.AccountsBase.TrimEnd('/') + "/api/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _token.RefreshToken,
                ["client_id"] = _options.ClientId,
            }), ct);
            if (!response.IsSuccessStatusCode)
                throw new SpotifyException($"Kunne ikke fornye Spotify-token ({(int)response.StatusCode}). Kjør «auth» på nytt.", response.StatusCode);

            _token = SpotifyAuth.ReadToken(await response.Content.ReadFromJsonAsync<JsonElement>(ct), _token.RefreshToken);
            _tokens.Save(_token);
            return _token.AccessToken!;
        }
        finally { _authGate.Release(); }
    }
}

public static class SpotifyAuth
{
    public static SpotifyToken ReadToken(JsonElement json, string? previousRefresh) => new()
    {
        AccessToken = json.GetProperty("access_token").GetString(),
        RefreshToken = json.TryGetProperty("refresh_token", out var r) && r.GetString() is { Length: > 0 } rt ? rt : previousRefresh ?? "",
        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(json.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600),
    };

    public static (string Verifier, string Challenge) NewPkce()
    {
        var verifier = Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string BuildAuthUrl(SpotifyOptions o, string challenge, string state) =>
        o.AccountsBase.TrimEnd('/') + "/authorize?" + string.Join('&',
            $"client_id={Uri.EscapeDataString(o.ClientId)}", "response_type=code",
            $"redirect_uri={Uri.EscapeDataString(o.RedirectUri)}", "code_challenge_method=S256",
            $"code_challenge={challenge}", $"scope={Uri.EscapeDataString(SpotifyClient.Scopes)}", $"state={state}");

    public static async Task<int> RunAsync(SpotifyOptions o, bool manual, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.ClientId))
        {
            Console.Error.WriteLine("Spotify:ClientId mangler i appsettings.json.");
            return 1;
        }
        var (verifier, challenge) = NewPkce();
        var state = Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        var url = BuildAuthUrl(o, challenge, state);

        Console.WriteLine("Åpne denne lenken i en nettleser og godkjenn tilgangen:\n\n" + url + "\n");
        string? redirected;
        if (manual)
        {
            Console.WriteLine("Nettleseren sendes til en adresse som begynner med " + o.RedirectUri + " (siden vil gi feilmelding – det er normalt).");
            Console.Write("Lim inn hele adressen her: ");
            redirected = Console.ReadLine();
        }
        else
        {
            TryOpenBrowser(url);
            redirected = await WaitForCallbackAsync(o.RedirectUri, ct);
        }

        if (!Uri.TryCreate(redirected, UriKind.Absolute, out var uri)) { Console.Error.WriteLine("Ugyldig adresse."); return 1; }
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        if (query["error"] is { } err) { Console.Error.WriteLine("Spotify avviste: " + err); return 1; }
        if (query["state"] != state || query["code"] is not { Length: > 0 } code) { Console.Error.WriteLine("Mangler kode eller feil state."); return 1; }

        using var http = new HttpClient();
        using var response = await http.PostAsync(o.AccountsBase.TrimEnd('/') + "/api/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = o.RedirectUri,
            ["client_id"] = o.ClientId, ["code_verifier"] = verifier,
        }), ct);
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Token-utveksling feilet ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");
            return 1;
        }
        var token = ReadToken(await response.Content.ReadFromJsonAsync<JsonElement>(ct), null);
        new TokenStore(o.TokenFile).Save(token);
        Console.WriteLine($"Ferdig! Token lagret i {Path.GetFullPath(o.TokenFile)}. Kopier den filen til Raspberry Pi-en om du kjørte dette på en annen maskin.");
        return 0;
    }

    private static async Task<string?> WaitForCallbackAsync(string redirectUri, CancellationToken ct)
    {
        var prefix = redirectUri.EndsWith('/') ? redirectUri : redirectUri[..(redirectUri.LastIndexOf('/') + 1)];
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        Console.WriteLine("Venter på at du godkjenner i nettleseren …");
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5), ct);
        var page = Encoding.UTF8.GetBytes("<html><body style='font-family:sans-serif'><h2>Ferdig – du kan lukke denne fanen.</h2></body></html>");
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.OutputStream.WriteAsync(page, ct);
        context.Response.Close();
        return context.Request.Url?.ToString();
    }

    private static void TryOpenBrowser(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ingen nettleser tilgjengelig – brukeren åpner lenken selv */ }
    }
}

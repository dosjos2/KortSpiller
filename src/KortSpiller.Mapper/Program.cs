using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KortSpiller.Core;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp =>
{
    var path = sp.GetRequiredService<IConfiguration>()["Mapper:MappingFile"] ?? "data/mappings.json";
    return new MappingStore(path);
});
builder.Services.AddSingleton<ScanService>();
builder.Services.Configure<PublishOptions>(builder.Configuration.GetSection("Mapper:Publish"));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/config", (ScanService scan, IOptions<PublishOptions> publish, MappingStore store) => new
{
    scanAvailable = scan.Available,
    readerName = scan.ReaderName,
    publishAvailable = !string.IsNullOrWhiteSpace(publish.Value.Url),
    mappingFile = store.Path,
    actions = PlayerActions.All.Select(a => new { id = a, label = PlayerActions.Label(a) }),
});

app.MapGet("/api/cards", (MappingStore store) => store.Load());

app.MapPost("/api/cards", async (CardRequest request, MappingStore store, IHttpClientFactory http, CancellationToken ct) =>
{
    var card = new CardMapping { Uid = request.Uid ?? "", Note = request.Note?.Trim(), Title = request.Title?.Trim() };
    if (!string.IsNullOrWhiteSpace(request.Action))
    {
        card.Action = request.Action.Trim().ToLowerInvariant();
        if (!PlayerActions.All.Contains(card.Action))
            return Results.BadRequest(new { error = $"Ukjent kommando. Bruk: {string.Join(", ", PlayerActions.All)}." });
        card.Title = string.IsNullOrWhiteSpace(card.Title) ? PlayerActions.Label(card.Action) : card.Title;
    }
    else
    {
        if (!SpotifyLink.TryParse(request.Url, out var link))
            return Results.BadRequest(new { error = "Dette ser ikke ut som en Spotify-lenke. Lim inn «Del → Kopier lenke» fra Spotify." });
        card.Uri = link.Uri;
        card.Url = $"https://open.spotify.com/{link.Kind}/{link.Id}";
        var info = await FetchOEmbedAsync(http.CreateClient(), card.Url, ct);
        card.Title = string.IsNullOrWhiteSpace(card.Title) ? info?.Title ?? card.Uri : card.Title;
        card.Image = info?.Image;
    }

    try { return Results.Ok(store.Upsert(card)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapDelete("/api/cards/{uid}", (string uid, MappingStore store) =>
    store.Remove(uid) ? Results.NoContent() : Results.NotFound());

app.MapGet("/api/scan", async (ScanService scan, int? timeout, CancellationToken ct) =>
{
    if (!scan.Available) return Results.StatusCode(StatusCodes.Status501NotImplemented);
    var uid = await scan.ScanAsync(TimeSpan.FromSeconds(Math.Clamp(timeout ?? 15, 1, 60)), ct);
    return uid is null ? Results.NoContent() : Results.Ok(new { uid });
});

app.MapGet("/api/export", (MappingStore store) =>
    Results.File(Encoding.UTF8.GetBytes(store.Load().ToJson()), "application/json", "mappings.json"));

app.MapPost("/api/import", async (HttpRequest request, MappingStore store) =>
{
    using var reader = new StreamReader(request.Body);
    try
    {
        var file = MappingFile.Parse(await reader.ReadToEndAsync());
        if (file.Cards.Any(c => string.IsNullOrWhiteSpace(c.Uid))) return Results.BadRequest(new { error = "Filen har kort uten ID." });
        foreach (var card in file.Cards) card.Uid = CardUid.Normalize(card.Uid);
        store.Replace(file);
        return Results.Ok(new { count = file.Cards.Count });
    }
    catch (JsonException) { return Results.BadRequest(new { error = "Filen er ikke gyldig JSON." }); }
});

app.MapPost("/api/publish", async (MappingStore store, IHttpClientFactory http, IOptions<PublishOptions> options, CancellationToken ct) =>
{
    var publish = options.Value;
    if (string.IsNullOrWhiteSpace(publish.Url))
        return Results.BadRequest(new { error = "Mapper:Publish:Url er ikke satt i appsettings.json." });

    using var message = new HttpRequestMessage(new HttpMethod(publish.Method), publish.Url)
    {
        Content = new StringContent(store.Load().ToJson(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
    };
    foreach (var (name, value) in publish.Headers) message.Headers.TryAddWithoutValidation(name, value);
    if (!string.IsNullOrWhiteSpace(publish.BearerToken))
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", publish.BearerToken);

    var response = await http.CreateClient().SendAsync(message, ct);
    return response.IsSuccessStatusCode
        ? Results.Ok(new { status = (int)response.StatusCode })
        : Results.Json(new { error = $"Serveren svarte {(int)response.StatusCode} {response.ReasonPhrase}." }, statusCode: 502);
});

app.Run();

static async Task<OEmbed?> FetchOEmbedAsync(HttpClient client, string url, CancellationToken ct)
{
    try
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var doc = await JsonDocument.ParseAsync(
            await client.GetStreamAsync($"https://open.spotify.com/oembed?url={Uri.EscapeDataString(url)}", timeout.Token),
            cancellationToken: timeout.Token);
        var title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
        var image = doc.RootElement.TryGetProperty("thumbnail_url", out var i) ? i.GetString() : null;
        return new OEmbed(title, image);
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException) { return null; }
}

public sealed record CardRequest(string? Uid, string? Url, string? Action, string? Title, string? Note);
public sealed record OEmbed(string? Title, string? Image);

public sealed class PublishOptions
{
    public string? Url { get; set; }
    public string Method { get; set; } = "PUT";
    public string? BearerToken { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
}

/// <summary>Valgfri leser koblet til maskinen mapperen kjører på (typisk en Raspberry Pi med skjerm).</summary>
public sealed class ScanService : IDisposable
{
    private readonly ICardReader? _reader;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ScanService(IConfiguration configuration, ILogger<ScanService> logger)
    {
        var section = configuration.GetSection("Mapper:Reader");
        if (string.IsNullOrWhiteSpace(section["Type"])) return;
        try { _reader = CardReaderFactory.Create(section.Get<ReaderOptions>()!); }
        catch (Exception ex) { logger.LogWarning(ex, "Kunne ikke starte RFID-leseren. Skann-knappen er deaktivert."); }
    }

    public bool Available => _reader is not null;
    public string? ReaderName => _reader?.Name;

    public async Task<string?> ScanAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_reader is null) return null;
        await _gate.WaitAsync(ct);
        try
        {
            var until = DateTime.UtcNow + timeout;
            return await Task.Run(() =>
            {
                while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
                    if (_reader.Read(TimeSpan.FromMilliseconds(300), ct) is { } uid) return uid;
                return null;
            }, ct);
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _reader?.Dispose();
}

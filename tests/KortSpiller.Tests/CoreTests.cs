using System.Net;
using System.Text;
using KortSpiller.Core;
using KortSpiller.Player;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KortSpiller.Tests;

public class CoreTests
{
    [Theory]
    [InlineData("https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC?si=abc", "spotify:track:4uLU6hMCjMI75M1A2tKUQC")]
    [InlineData("https://open.spotify.com/intl-no/album/4uLU6hMCjMI75M1A2tKUQC", "spotify:album:4uLU6hMCjMI75M1A2tKUQC")]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("spotify:user:someone:playlist:37i9dQZF1DXcBWIGoYBM5M", "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M")]
    public void SpotifyLink_parses(string input, string uri)
    {
        Assert.True(SpotifyLink.TryParse(input, out var link));
        Assert.Equal(uri, link.Uri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.com/track/4uLU6hMCjMI75M1A2tKUQC")]
    [InlineData("hello")]
    public void SpotifyLink_rejects_garbage(string input) => Assert.False(SpotifyLink.TryParse(input, out _));

    [Fact]
    public void Uid_hex_and_decimal_forms_match()
    {
        // 04:A1:B2:C3 som hex (RC522/PN532) og som desimal fra en USB-leser
        Assert.True(CardUid.Matches("04A1B2C3", "04A1B2C3".ToLower()));
        var big = Convert.ToUInt32("04A1B2C3", 16).ToString();
        Assert.True(CardUid.Matches("04A1B2C3", big));
        var little = BitConverter.ToUInt32(Convert.FromHexString("04A1B2C3"), 0).ToString();
        Assert.True(CardUid.Matches("04A1B2C3", little));
        Assert.False(CardUid.Matches("04A1B2C3", "04A1B2C4"));
    }

    [Fact]
    public void Store_roundtrip_and_validation()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new MappingStore(path);
            store.Upsert(new CardMapping { Uid = "04-a1-b2-c3", Uri = "spotify:track:4uLU6hMCjMI75M1A2tKUQC", Title = "x" });
            Assert.Throws<ArgumentException>(() => store.Upsert(new CardMapping { Uid = "AA" }));

            var index = new MappingIndex(new MappingStore(path).Load());
            Assert.Equal("x", index.Find(Convert.ToUInt32("04A1B2C3", 16).ToString())?.Title);
            Assert.True(store.Remove("04A1B2C3"));
            Assert.Empty(store.Load().Cards);
        }
        finally { File.Delete(path); }
    }
}

public class SpotifyClientTests
{
    private sealed class Fake : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.Method, request.RequestUri!.PathAndQuery, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            var json = request.RequestUri.AbsolutePath.EndsWith("/devices")
                ? """{"devices":[{"id":"dev1","name":"Kortspiller"}]}"""
                : "";
            return new HttpResponseMessage(request.Method == HttpMethod.Get ? HttpStatusCode.OK : HttpStatusCode.NoContent)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (SpotifyClient, Fake) Create()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var store = new TokenStore(path);
        store.Save(new SpotifyToken { RefreshToken = "r", AccessToken = "a", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        var fake = new Fake();
        var options = Options.Create(new SpotifyOptions { ClientId = "id", DeviceRetries = 1, DeviceRetryDelayMs = 1 });
        return (new SpotifyClient(new HttpClient(fake), options, store, NullLogger<SpotifyClient>.Instance), fake);
    }

    [Fact]
    public async Task Play_track_sends_uris_to_named_device()
    {
        var (client, fake) = Create();
        await client.PlayAsync("spotify:track:4uLU6hMCjMI75M1A2tKUQC", default);

        var play = Assert.Single(fake.Calls, c => c.Url.StartsWith("/v1/me/player/play"));
        Assert.Equal(HttpMethod.Put, play.Method);
        Assert.Contains("device_id=dev1", play.Url);
        Assert.Contains("\"uris\":[\"spotify:track:4uLU6hMCjMI75M1A2tKUQC\"]", play.Body);
        Assert.DoesNotContain("context_uri", play.Body);
    }

    [Fact]
    public async Task Play_playlist_uses_context_uri_and_volume_is_clamped()
    {
        var (client, fake) = Create();
        await client.PlayAsync("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", default);
        await client.SetVolumeAsync(250, default);

        Assert.Contains("\"context_uri\":\"spotify:playlist:37i9dQZF1DXcBWIGoYBM5M\"", fake.Calls.First(c => c.Url.Contains("/play?")).Body);
        Assert.Contains(fake.Calls, c => c.Url.Contains("volume_percent=100"));
    }
}

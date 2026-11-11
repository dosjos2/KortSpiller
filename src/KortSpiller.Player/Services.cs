using System.Net;
using System.Net.Http.Headers;
using Iot.Device.Adc;
using KortSpiller.Core;
using Microsoft.Extensions.Options;
using System.Device.Gpio;
using System.Device.Spi;

namespace KortSpiller.Player;

/// <summary>Holder mappingfilen oppdatert fra URL/fil og faller tilbake på lokal cache uten nett.</summary>
public sealed class MappingSync : BackgroundService
{
    private readonly HttpClient _http;
    private readonly MappingOptions _options;
    private readonly ILogger<MappingSync> _log;
    private string? _etag;
    private DateTimeOffset? _lastModified;
    private DateTime _fileStamp;

    public MappingSync(HttpClient http, IOptions<MappingOptions> options, ILogger<MappingSync> log)
    {
        _http = http; _options = options.Value; _log = log;
        LoadCache();
    }

    public MappingIndex Current { get; private set; } = MappingIndex.Empty;

    private bool IsUrl => _options.Source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                          || _options.Source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private void LoadCache()
    {
        try
        {
            if (File.Exists(_options.CacheFile)) Apply(File.ReadAllText(_options.CacheFile), cache: false);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Kunne ikke lese cache {File}.", _options.CacheFile); }
    }

    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        try
        {
            string? json = IsUrl ? await DownloadAsync(ct) : ReadLocal();
            if (json is null) return false;
            Apply(json, cache: true);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or TaskCanceledException)
        {
            if (!ct.IsCancellationRequested) _log.LogWarning("Kunne ikke hente mappingfil ({Message}). Bruker {Count} kort fra cache.", ex.Message, Current.Count);
            return false;
        }
    }

    private string? ReadLocal()
    {
        var stamp = File.GetLastWriteTimeUtc(_options.Source);
        if (stamp == _fileStamp) return null;
        _fileStamp = stamp;
        return File.ReadAllText(_options.Source);
    }

    private async Task<string?> DownloadAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _options.Source);
        if (_etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
        if (_lastModified is not null) request.Headers.IfModifiedSince = _lastModified;
        foreach (var (k, v) in _options.Headers) request.Headers.TryAddWithoutValidation(k, v);
        if (!string.IsNullOrWhiteSpace(_options.BearerToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotModified) return null;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        _etag = response.Headers.ETag?.ToString();
        _lastModified = response.Content.Headers.LastModified;
        return json;
    }

    private void Apply(string json, bool cache)
    {
        var file = MappingFile.Parse(json);
        Current = new MappingIndex(file);
        _log.LogInformation("Mappingfil lastet: {Count} kort.", file.Cards.Count);
        if (!cache) return;
        var dir = Path.GetDirectoryName(Path.GetFullPath(_options.CacheFile))!;
        Directory.CreateDirectory(dir);
        var tmp = _options.CacheFile + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _options.CacheFile, overwrite: true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(5, _options.RefreshSeconds));
        await RefreshAsync(stoppingToken);
        using var timer = new PeriodicTimer(delay);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await RefreshAsync(stoppingToken);
    }
}

public enum LedState { Off, Ready, Busy, Error }

/// <summary>Status-LED. Gjør ingenting hvis den ikke er aktivert.</summary>
public sealed class StatusLed : BackgroundService
{
    private readonly PinOptions _options;
    private volatile LedState _state = LedState.Off;
    private DateTime _flashUntil;

    public StatusLed(IOptionsMonitor<PinOptions> options) => _options = options.Get("Led");

    public void Set(LedState state) => _state = state;
    public void Flash() => _flashUntil = DateTime.UtcNow.AddMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_options.Enabled) return;
        using var gpio = new GpioController();
        gpio.OpenPin(_options.Pin, PinMode.Output);
        var tick = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var on = DateTime.UtcNow < _flashUntil ? tick % 2 == 0 : _state switch
                {
                    LedState.Ready => true,
                    LedState.Busy => tick % 4 < 2,
                    LedState.Error => tick % 2 == 0,
                    _ => false,
                };
                gpio.Write(_options.Pin, on ? PinValue.High : PinValue.Low);
                tick++;
                await Task.Delay(_state == LedState.Error ? 120 : 200, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { gpio.Write(_options.Pin, PinValue.Low); }
    }
}

/// <summary>Siste kjente volum (0–100) fra potmeteret, slik at det kan settes etter at en sang starter.</summary>
public sealed class VolumeState
{
    private int _percent = -1;
    public int Percent { get => Volatile.Read(ref _percent); set => Volatile.Write(ref _percent, value); }
}

public sealed class VolumeKnob(IOptionsMonitor<VolumeOptions> monitor, SpotifyClient spotify, VolumeState state, ILogger<VolumeKnob> log) : BackgroundService
{
    private readonly VolumeOptions _options = monitor.CurrentValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            if (_options.DefaultPercent >= 0) state.Percent = _options.DefaultPercent;
            return;
        }

        using var spi = SpiDevice.Create(new SpiConnectionSettings(_options.SpiBus, _options.ChipSelect) { ClockFrequency = 1_000_000, Mode = SpiMode.Mode0 });
        using var adc = new Mcp3008(spi);
        var lastSent = -100;
        var lastSentAt = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var raw = 0;
                for (var i = 0; i < 4; i++) raw += adc.Read(_options.Channel);
                var percent = (int)Math.Round(raw / 4.0 / 1023.0 * 100);
                state.Percent = percent;

                if (Math.Abs(percent - lastSent) >= _options.MinStepPercent && DateTime.UtcNow - lastSentAt > TimeSpan.FromMilliseconds(400))
                {
                    lastSent = percent; lastSentAt = DateTime.UtcNow;
                    await spotify.SetVolumeAsync(percent, ct);
                }
                await Task.Delay(150, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogWarning("Volumjustering feilet: {Message}", ex.Message);
                await Task.Delay(2000, ct);
            }
        }
    }
}

public sealed class PauseButton(IOptionsMonitor<PinOptions> monitor, SpotifyClient spotify, StatusLed led, ILogger<PauseButton> log) : BackgroundService
{
    private readonly PinOptions _options = monitor.Get("Button");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_options.Enabled) return;
        using var gpio = new GpioController();
        gpio.OpenPin(_options.Pin, PinMode.InputPullUp);
        var wasDown = false;
        var lastPress = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            var down = gpio.Read(_options.Pin) == PinValue.Low;
            if (down && !wasDown && DateTime.UtcNow - lastPress > TimeSpan.FromMilliseconds(300))
            {
                lastPress = DateTime.UtcNow;
                led.Flash();
                try { await spotify.ToggleAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { log.LogWarning("Pauseknappen feilet: {Message}", ex.Message); }
            }
            wasDown = down;
            try { await Task.Delay(25, ct); } catch (OperationCanceledException) { break; }
        }
    }
}

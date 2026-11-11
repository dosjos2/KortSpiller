using KortSpiller.Core;
using Microsoft.Extensions.Options;

namespace KortSpiller.Player;

/// <summary>Slår opp et skannet kort i mappingen og utfører handlingen.</summary>
public sealed class CardHandler(MappingSync mappings, SpotifyClient spotify, VolumeState volume, StatusLed led, ILogger<CardHandler> log)
{
    public async Task HandleAsync(string uid, CancellationToken ct)
    {
        led.Flash();
        var card = mappings.Current.Find(uid);
        if (card is null)
        {
            log.LogWarning("Ukjent kort {Uid}. Legg det til i mapperen.", uid);
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(card.Action))
            {
                log.LogInformation("Kort {Uid}: kommando {Action}", uid, card.Action);
                await spotify.RunActionAsync(card.Action, ct);
            }
            else if (!string.IsNullOrWhiteSpace(card.Uri))
            {
                log.LogInformation("Kort {Uid}: spiller «{Title}» ({Uri})", uid, card.Title, card.Uri);
                led.Set(LedState.Busy);
                await spotify.PlayAsync(card.Uri, ct);
                if (volume.Percent >= 0) await spotify.SetVolumeAsync(volume.Percent, ct);
            }
            led.Set(LedState.Ready);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogError("Kunne ikke utføre kort {Uid}: {Message}", uid, ex.Message);
            led.Set(LedState.Error);
            await Task.Delay(1500, ct);
            led.Set(LedState.Ready);
        }
    }
}

/// <summary>Leser fra alle konfigurerte lesere. Samme kort ignoreres til det har vært borte en stund.</summary>
public sealed class ScannerService(IOptions<PlayerOptions> options, CardHandler handler, StatusLed led, ILogger<ScannerService> log) : BackgroundService
{
    private readonly PlayerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_options.Readers.Count == 0)
        {
            log.LogError("Ingen lesere konfigurert (Player:Readers).");
            led.Set(LedState.Error);
            return;
        }
        led.Set(LedState.Ready);
        await Task.WhenAll(_options.Readers.Select(r => Task.Run(() => ReadLoopAsync(r, ct), ct)));
    }

    private async Task ReadLoopAsync(ReaderOptions config, CancellationToken ct)
    {
        var gap = TimeSpan.FromSeconds(_options.RescanGapSeconds);
        while (!ct.IsCancellationRequested)
        {
            ICardReader reader;
            try { reader = CardReaderFactory.Create(config); }
            catch (Exception ex)
            {
                log.LogError("Kunne ikke starte leser {Type}: {Message}. Prøver igjen om 10 s.", config.Type, ex.Message);
                led.Set(LedState.Error);
                try { await Task.Delay(10_000, ct); } catch (OperationCanceledException) { return; }
                continue;
            }

            using (reader)
            {
                log.LogInformation("Leser {Name} klar.", reader.Name);
                led.Set(LedState.Ready);
                string? lastUid = null;
                var lastSeen = DateTime.MinValue;
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var uid = reader.Read(TimeSpan.FromMilliseconds(300), ct);
                        if (uid is null) continue;

                        var isRepeat = uid == lastUid && DateTime.UtcNow - lastSeen < gap;
                        lastUid = uid; lastSeen = DateTime.UtcNow;
                        if (!isRepeat) await handler.HandleAsync(uid, ct);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    log.LogError("Leseren {Name} feilet: {Message}. Starter den på nytt.", reader.Name, ex.Message);
                    led.Set(LedState.Error);
                    try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { return; }
                }
            }
        }
    }
}

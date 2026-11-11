using System.Device.Gpio;
using System.Device.I2c;
using System.Device.Spi;
using Iot.Device.Mfrc522;
using Iot.Device.Pn532;
using Iot.Device.Pn532.ListPassive;
using Iot.Device.Rfid;

namespace KortSpiller.Core;

/// <summary>Innstillinger for én RFID-leser (leses fra appsettings.json).</summary>
public sealed class ReaderOptions
{
    /// <summary>Mfrc522 (RC522, SPI), Pn532 (I2C eller UART), UsbKeyboard (USB-leser som oppfører seg som tastatur) eller Console (test).</summary>
    public string Type { get; set; } = "Mfrc522";

    // Mfrc522
    public int SpiBus { get; set; }
    public int SpiChipSelect { get; set; }
    public int ResetPin { get; set; } = 25;

    // Pn532
    public string Interface { get; set; } = "I2C";
    public int I2cBus { get; set; } = 1;
    public int I2cAddress { get; set; } = 0x24;
    public string SerialPort { get; set; } = "/dev/serial0";

    // UsbKeyboard: del av enhetsnavnet, for eksempel "RFID" eller "Sycreader"
    public string DeviceName { get; set; } = "";
}

/// <summary>En RFID-leser som returnerer kort-ID-en til kortet som ligger på leseren akkurat nå.</summary>
public interface ICardReader : IDisposable
{
    string Name { get; }
    /// <summary>Venter opptil <paramref name="timeout"/> på et kort. Returnerer hex-ID eller null.</summary>
    string? Read(TimeSpan timeout, CancellationToken cancellationToken);
}

public static class CardReaderFactory
{
    public static ICardReader Create(ReaderOptions options) => options.Type.ToLowerInvariant() switch
    {
        "mfrc522" or "rc522" => new Mfrc522CardReader(options),
        "pn532" => new Pn532CardReader(options),
        "usbkeyboard" or "usb" => new LinuxKeyboardCardReader(options),
        "console" => new ConsoleCardReader(),
        _ => throw new NotSupportedException($"Ukjent lesertype '{options.Type}'. Bruk Mfrc522, Pn532, UsbKeyboard eller Console."),
    };
}

/// <summary>RC522 (MFRC522) koblet til SPI.</summary>
public sealed class Mfrc522CardReader : ICardReader
{
    private readonly SpiDevice _spi;
    private readonly GpioController _gpio = new();
    private readonly MfRc522 _reader;

    public Mfrc522CardReader(ReaderOptions options)
    {
        _spi = SpiDevice.Create(new SpiConnectionSettings(options.SpiBus, options.SpiChipSelect)
        {
            ClockFrequency = 1_000_000,
            Mode = SpiMode.Mode0,
        });
        _reader = new MfRc522(_spi, options.ResetPin, _gpio, shouldDispose: false);
    }

    public string Name => "RC522";

    public string? Read(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return null;
        return _reader.ListenToCardIso14443TypeA(out var card, timeout) && card?.NfcId is { Length: > 0 } id
            ? CardUid.FromBytes(id)
            : null;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _spi.Dispose();
        _gpio.Dispose();
    }
}

/// <summary>PN532 (NFC-modul) over I2C eller UART.</summary>
public sealed class Pn532CardReader : ICardReader
{
    private readonly Pn532 _reader;
    private readonly I2cDevice? _i2c;

    public Pn532CardReader(ReaderOptions options)
    {
        if (options.Interface.Equals("UART", StringComparison.OrdinalIgnoreCase))
        {
            _reader = new Pn532(options.SerialPort);
        }
        else
        {
            _i2c = I2cDevice.Create(new I2cConnectionSettings(options.I2cBus, options.I2cAddress));
            _reader = new Pn532(_i2c);
        }
    }

    public string Name => "PN532";

    public string? Read(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var data = _reader.ListPassiveTarget(MaxTarget.One, TargetBaudRate.B106kbpsTypeA);
        if (data is null || data.Length < 2)
        {
            cancellationToken.WaitHandle.WaitOne(timeout);
            return null;
        }
        return _reader.TryDecode106kbpsTypeA(data.AsSpan().Slice(1)) is { NfcId.Length: > 0 } card
            ? CardUid.FromBytes(card.NfcId)
            : null;
    }

    public void Dispose() { _reader.Dispose(); _i2c?.Dispose(); }
}

/// <summary>Skriv inn kort-ID i terminalen. Kun for test på PC.</summary>
public sealed class ConsoleCardReader : ICardReader
{
    private readonly System.Collections.Concurrent.BlockingCollection<string> _lines = new();

    public ConsoleCardReader()
    {
        new Thread(() =>
        {
            while (Console.ReadLine() is { } line)
                if (CardUid.Normalize(line).Length > 0) _lines.Add(CardUid.Normalize(line));
        }) { IsBackground = true }.Start();
    }

    public string Name => "Konsoll";

    public string? Read(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try { return _lines.TryTake(out var line, (int)timeout.TotalMilliseconds, cancellationToken) ? line : null; }
        catch (OperationCanceledException) { return null; }
    }

    public void Dispose() => _lines.Dispose();
}

/// <summary>
/// USB-lesere som oppfører seg som tastatur (skriver ID-en og Enter). Leser rå hendelser fra /dev/input
/// slik at det fungerer uten skjerm og uten fokus. Kun Linux. Brukeren må være i gruppen "input".
/// </summary>
public sealed class LinuxKeyboardCardReader : ICardReader
{
    private const int EventSize = 24; // struct input_event på 64-bit (timeval 16 + type 2 + code 2 + value 4)
    private const ushort EvKey = 1;
    private readonly FileStream _stream;
    private readonly System.Collections.Concurrent.BlockingCollection<string> _ids = new();
    private readonly CancellationTokenSource _stop = new();

    public LinuxKeyboardCardReader(ReaderOptions options)
    {
        var path = FindDevice(options.DeviceName)
            ?? throw new InvalidOperationException($"Fant ingen inndataenhet som inneholder '{options.DeviceName}' i /proc/bus/input/devices.");
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, false);
        new Thread(Pump) { IsBackground = true }.Start();
    }

    public string Name => "USB-leser";

    public string? Read(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try { return _ids.TryTake(out var id, (int)timeout.TotalMilliseconds, cancellationToken) ? id : null; }
        catch (OperationCanceledException) { return null; }
    }

    private void Pump()
    {
        var buffer = new byte[EventSize];
        var digits = new System.Text.StringBuilder();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var read = 0;
                while (read < EventSize)
                {
                    var n = _stream.Read(buffer, read, EventSize - read);
                    if (n <= 0) return;
                    read += n;
                }
                var type = BitConverter.ToUInt16(buffer, 16);
                var code = BitConverter.ToUInt16(buffer, 18);
                var value = BitConverter.ToInt32(buffer, 20);
                if (type != EvKey || value != 1) continue;
                if (code is 28 or 96) // Enter / keypad Enter
                {
                    if (digits.Length > 0) _ids.Add(digits.ToString());
                    digits.Clear();
                }
                else if (DigitFor(code) is { } digit) digits.Append(digit);
            }
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private static char? DigitFor(ushort code) => code switch
    {
        >= 2 and <= 10 => (char)('1' + (code - 2)),
        11 => '0',
        82 => '0',
        >= 79 and <= 81 => (char)('1' + (code - 79)),
        >= 75 and <= 77 => (char)('4' + (code - 75)),
        >= 71 and <= 73 => (char)('7' + (code - 71)),
        _ => null,
    };

    private static string? FindDevice(string nameFragment)
    {
        const string devices = "/proc/bus/input/devices";
        if (!File.Exists(devices)) return null;
        foreach (var block in File.ReadAllText(devices).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!block.Contains(nameFragment, StringComparison.OrdinalIgnoreCase)) continue;
            var handlers = block.Split('\n').FirstOrDefault(l => l.StartsWith("H: Handlers=", StringComparison.Ordinal));
            var eventName = handlers?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(h => h.StartsWith("event", StringComparison.Ordinal));
            if (eventName is not null) return $"/dev/input/{eventName}";
        }
        return null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stream.Dispose();
        _ids.Dispose();
    }
}

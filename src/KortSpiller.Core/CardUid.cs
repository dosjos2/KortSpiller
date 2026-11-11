using System.Globalization;

namespace KortSpiller.Core;

/// <summary>
/// Normalisering av kort-ID. Ulike lesere gir samme kort ulik tekst: RC522/PN532 gir hex ("04A1B2C3"),
/// mange USB-lesere gir et desimaltall. Alias-listen gjør at et kort kan gjenkjennes uansett leser.
/// </summary>
public static class CardUid
{
    public static string Normalize(string? uid) =>
        new string((uid ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    public static string FromBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    /// <summary>Alle skrivemåter vi kan gjenkjenne for samme kort (inkludert selve ID-en).</summary>
    public static IReadOnlyCollection<string> Aliases(string? uid)
    {
        var id = Normalize(uid);
        var result = new HashSet<string>();
        if (id.Length == 0) return result;
        result.Add(id);

        if (id.Length == 8 && IsHex(id))
        {
            var bytes = Convert.FromHexString(id);
            var big = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
            var little = (uint)(bytes[3] << 24 | bytes[2] << 16 | bytes[1] << 8 | bytes[0]);
            result.Add(big.ToString(CultureInfo.InvariantCulture));
            result.Add(little.ToString(CultureInfo.InvariantCulture));
            result.Add(big.ToString("D10", CultureInfo.InvariantCulture));
            result.Add(little.ToString("D10", CultureInfo.InvariantCulture));
        }
        else if (id.All(char.IsAsciiDigit) && id.Length <= 10 && uint.TryParse(id, out var number))
        {
            var bigHex = number.ToString("X8", CultureInfo.InvariantCulture);
            var bytes = Convert.FromHexString(bigHex);
            Array.Reverse(bytes);
            result.Add(bigHex);
            result.Add(Convert.ToHexString(bytes));
        }
        return result;
    }

    public static bool Matches(string? a, string? b)
    {
        var other = Aliases(b);
        return Aliases(a).Any(other.Contains);
    }

    private static bool IsHex(string value) => value.All(char.IsAsciiHexDigit);
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace KortSpiller.Core;

/// <summary>Én kobling fra et kort til en Spotify-lenke eller en kommando (pause, neste ...).</summary>
public sealed class CardMapping
{
    public string Uid { get; set; } = "";
    /// <summary>Spotify-URI, for eksempel spotify:track:... Tom hvis kortet er et kommandokort.</summary>
    public string? Uri { get; set; }
    /// <summary>Kommando i stedet for sang: pause, resume, toggle, next, previous.</summary>
    public string? Action { get; set; }
    public string? Title { get; set; }
    public string? Url { get; set; }
    public string? Image { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset Added { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MappingFile
{
    public int Version { get; set; } = 1;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public List<CardMapping> Cards { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static MappingFile Parse(string json) =>
        JsonSerializer.Deserialize<MappingFile>(json, Json) ?? new MappingFile();

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>Raskt oppslag av kort via alle aliaser av kort-ID-en.</summary>
public sealed class MappingIndex
{
    public static readonly MappingIndex Empty = new(new MappingFile());

    private readonly Dictionary<string, CardMapping> _byAlias = new();

    public MappingIndex(MappingFile file)
    {
        File = file;
        foreach (var card in file.Cards)
            foreach (var alias in CardUid.Aliases(card.Uid))
                _byAlias.TryAdd(alias, card);
    }

    public MappingFile File { get; }
    public int Count => File.Cards.Count;

    public CardMapping? Find(string? uid)
    {
        foreach (var alias in CardUid.Aliases(uid))
            if (_byAlias.TryGetValue(alias, out var card)) return card;
        return null;
    }
}

/// <summary>Leser og skriver mapping-filen trygt (atomisk skriving, én skriver om gangen).</summary>
public sealed class MappingStore(string path)
{
    private readonly object _lock = new();
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public MappingFile Load()
    {
        lock (_lock) return LoadUnlocked();
    }

    public CardMapping Upsert(CardMapping card)
    {
        card.Uid = CardUid.Normalize(card.Uid);
        if (card.Uid.Length == 0) throw new ArgumentException("Kort-ID mangler.");
        if (string.IsNullOrWhiteSpace(card.Uri) == string.IsNullOrWhiteSpace(card.Action))
            throw new ArgumentException("Oppgi enten en Spotify-lenke eller en kommando.");

        lock (_lock)
        {
            var file = LoadUnlocked();
            var existing = file.Cards.FindIndex(c => CardUid.Matches(c.Uid, card.Uid));
            if (existing >= 0) file.Cards[existing] = card; else file.Cards.Add(card);
            SaveUnlocked(file);
            return card;
        }
    }

    public bool Remove(string uid)
    {
        lock (_lock)
        {
            var file = LoadUnlocked();
            var removed = file.Cards.RemoveAll(c => CardUid.Matches(c.Uid, uid)) > 0;
            if (removed) SaveUnlocked(file);
            return removed;
        }
    }

    public void Replace(MappingFile file)
    {
        lock (_lock) SaveUnlocked(file);
    }

    private MappingFile LoadUnlocked() =>
        File.Exists(Path) ? MappingFile.Parse(File.ReadAllText(Path)) : new MappingFile();

    private void SaveUnlocked(MappingFile file)
    {
        file.Updated = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, file.ToJson());
        File.Move(temp, Path, overwrite: true);
    }
}

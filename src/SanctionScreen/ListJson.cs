using System.Text.Json;
using System.Text.Json.Serialization;

namespace SanctionScreen;

/// <summary>A compact JSON form of a list, for loading in a browser: names, types, programs, countries and birth years.</summary>
public static class ListJson
{
    /// <summary>The largest file read.</summary>
    public const long MaxBytes = 32L * 1024 * 1024;

    /// <summary>Writes the list.</summary>
    public static void Save(WatchList list, Stream output)
    {
        ArgumentNullException.ThrowIfNull(list);
        var doc = new Document(list.Source, list.Published?.ToString("yyyy-MM-dd"), list.Entries.Select(e => new Item(
            e.Id, e.Type.ToString().ToLowerInvariant(), e.Names.Select(n => new[] { n.Name, n.Kind }).ToArray(),
            e.Programs.ToArray(), e.Countries.ToArray(), e.BirthYears.ToArray())).ToArray());
        JsonSerializer.Serialize(output, doc, ListJsonContext.Default.Document);
    }

    /// <summary>Reads a list written by <see cref="Save"/>. Throws <see cref="ListFormatException"/> for anything else.</summary>
    public static WatchList Load(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.CanSeek && input.Length > MaxBytes) throw new ListFormatException($"The list is larger than {MaxBytes / 1024 / 1024} MB.");
        try
        {
            return Build(JsonSerializer.Deserialize(new LimitedStream(input, MaxBytes), ListJsonContext.Default.Document));
        }
        catch (JsonException e)
        {
            throw new ListFormatException("The list isn't valid: " + e.Message);
        }
    }

    /// <summary>Reads a list without blocking: the form to use in a browser, where synchronous reads aren't allowed.</summary>
    public static async Task<WatchList> LoadAsync(Stream input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            return Build(await JsonSerializer.DeserializeAsync(new LimitedStream(input, MaxBytes), ListJsonContext.Default.Document, ct).ConfigureAwait(false));
        }
        catch (JsonException e)
        {
            throw new ListFormatException("The list isn't valid: " + e.Message);
        }
    }

    private static WatchList Build(Document? doc)
    {
        if (doc?.Entries is not { Length: > 0 }) throw new ListFormatException("The list holds no entries.");
        var entries = new List<ListEntry>(doc.Entries.Length);
        foreach (var i in doc.Entries)
        {
            if (i?.Names is not { Length: > 0 } || i.Id is null || i.Names.Any(n => n is not { Length: 2 } || n[0] is null))
                throw new ListFormatException("An entry in the list has no id or no names.");
            var type = i.Type switch { "individual" => PartyType.Individual, "vessel" => PartyType.Vessel, "aircraft" => PartyType.Aircraft, "entity" => PartyType.Entity, _ => throw new ListFormatException($"Entry {i.Id[..Math.Min(12, i.Id.Length)]} has an unknown type.") };
            entries.Add(new ListEntry(i.Id, type, i.Names.Take(200).Select(n => new ListedName(n[0], n[1] ?? "aka")).ToList(),
                i.Programs ?? [], i.Countries ?? [], i.BirthYears ?? [], null));
        }
        DateOnly? published = DateOnly.TryParseExact(doc.Published, "yyyy-MM-dd", out var d) ? d : null;
        return new WatchList(doc.Source ?? "list", published, entries);
    }

    internal sealed record Document(
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("published")] string? Published,
        [property: JsonPropertyName("entries")] Item[]? Entries);

    internal sealed record Item(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("names")] string[][] Names,
        [property: JsonPropertyName("programs")] string[]? Programs,
        [property: JsonPropertyName("countries")] string[]? Countries,
        [property: JsonPropertyName("birthYears")] int[]? BirthYears);
}

/// <summary>A stream that stops with <see cref="ListFormatException"/> after a number of bytes, for inputs whose length isn't known in advance.</summary>
internal sealed class LimitedStream(Stream inner, long limit) : Stream
{
    private long read;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => read; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Count(await inner.ReadAsync(buffer, ct).ConfigureAwait(false));
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    private int Count(int n)
    {
        read += n;
        if (read > limit) throw new ListFormatException($"The list is larger than {limit / 1024 / 1024} MB.");
        return n;
    }
}

[JsonSourceGenerationOptions(MaxDepth = 8)]
[JsonSerializable(typeof(ListJson.Document))]
internal sealed partial class ListJsonContext : JsonSerializerContext;

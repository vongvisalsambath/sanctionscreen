using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SanctionScreen;

/// <summary>
/// Reads OFAC's Specially Designated Nationals list in its CSV form (SDN.CSV, with ALT.CSV for aliases and ADD.CSV for
/// addresses), as published at https://sanctionslistservice.ofac.treas.gov. Empty values are written "-0-".
/// </summary>
public static partial class Ofac
{
    /// <summary>The largest file read.</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;
    /// <summary>The most rows read from one file.</summary>
    public const int MaxRows = 500_000;
    private const int MaxField = 20_000;

    /// <summary>OFAC's download addresses for the three files.</summary>
    public static readonly IReadOnlyDictionary<string, string> Downloads = new Dictionary<string, string>
    {
        ["SDN.CSV"] = "https://sanctionslistservice.ofac.treas.gov/api/PublicationPreview/exports/SDN.CSV",
        ["ALT.CSV"] = "https://sanctionslistservice.ofac.treas.gov/api/PublicationPreview/exports/ALT.CSV",
        ["ADD.CSV"] = "https://sanctionslistservice.ofac.treas.gov/api/PublicationPreview/exports/ADD.CSV",
    };

    /// <summary>Reads the list. <paramref name="alt"/> and <paramref name="add"/> are optional. Throws <see cref="ListFormatException"/> for anything that isn't the expected format.</summary>
    public static WatchList Load(Stream sdn, Stream? alt = null, Stream? add = null, DateOnly? published = null)
    {
        ArgumentNullException.ThrowIfNull(sdn);
        var entries = new Dictionary<string, Builder>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (row, line) in Rows(sdn, "SDN.CSV"))
        {
            if (row.Count != 12) throw new ListFormatException($"SDN.CSV line {line} has {row.Count} columns; the SDN format has 12.");
            var id = Id(row[0], "SDN.CSV", line);
            var name = Value(row[1]) ?? throw new ListFormatException($"SDN.CSV line {line} has no name.");
            var type = Value(row[2])?.ToLowerInvariant() switch
            {
                null => PartyType.Entity,
                "individual" => PartyType.Individual,
                "vessel" => PartyType.Vessel,
                "aircraft" => PartyType.Aircraft,
                var other => throw new ListFormatException($"SDN.CSV line {line} has an unknown type \"{Clean(other!)}\"."),
            };
            if (entries.ContainsKey(id)) throw new ListFormatException($"SDN.CSV line {line} repeats entry {id}.");
            var remarks = Value(row[11]);
            var b = new Builder(id, type, remarks);
            b.Names.Add(new ListedName(name, "primary"));
            b.Programs.AddRange(ProgramPattern().Matches(row[3]).Select(m => m.Groups[1].Value).DefaultIfEmpty(Value(row[3]) ?? "").Where(p => p.Length > 0));
            if (remarks is not null)
            {
                foreach (Match m in BirthYear().Matches(remarks)) b.BirthYears.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
                foreach (Match m in Nationality().Matches(remarks)) b.Countries.Add(m.Groups[1].Value.Trim());
            }
            if (Value(row[9]) is { } flag) b.Countries.Add(flag);
            entries.Add(id, b);
            order.Add(id);
        }
        if (entries.Count == 0) throw new ListFormatException("SDN.CSV holds no entries.");

        if (alt is not null)
            foreach (var (row, line) in Rows(alt, "ALT.CSV"))
            {
                if (row.Count != 5) throw new ListFormatException($"ALT.CSV line {line} has {row.Count} columns; the ALT format has 5.");
                if (!entries.TryGetValue(Id(row[0], "ALT.CSV", line), out var b)) continue; // an alias for an entry that isn't listed
                if (Value(row[3]) is { } altName && b.Names.Count < 200) b.Names.Add(new ListedName(altName, Value(row[2])?.ToLowerInvariant() ?? "aka"));
            }

        if (add is not null)
            foreach (var (row, line) in Rows(add, "ADD.CSV"))
            {
                if (row.Count != 6) throw new ListFormatException($"ADD.CSV line {line} has {row.Count} columns; the ADD format has 6.");
                if (entries.TryGetValue(Id(row[0], "ADD.CSV", line), out var b) && Value(row[4]) is { } country && b.Countries.Count < 100) b.Countries.Add(country);
            }

        return new WatchList("OFAC SDN", published, order.Select(id => entries[id].Build()).ToList());
    }

    /// <summary>Reads the three files from a folder (ALT.CSV and ADD.CSV are optional), with the publication date from a "published.txt" next to them if there is one.</summary>
    public static WatchList LoadFolder(string folder)
    {
        Stream Open(string name)
        {
            var path = Path.Combine(folder, name);
            if (new FileInfo(path).Length > MaxFileBytes) throw new ListFormatException($"{name} is larger than {MaxFileBytes / 1024 / 1024} MB.");
            return File.OpenRead(path);
        }
        if (!File.Exists(Path.Combine(folder, "SDN.CSV"))) throw new ListFormatException($"There is no SDN.CSV in {folder}.");
        using var sdn = Open("SDN.CSV");
        using var alt = File.Exists(Path.Combine(folder, "ALT.CSV")) ? Open("ALT.CSV") : null;
        using var add = File.Exists(Path.Combine(folder, "ADD.CSV")) ? Open("ADD.CSV") : null;
        DateOnly? published = null;
        var stamp = Path.Combine(folder, "published.txt");
        if (File.Exists(stamp) && DateOnly.TryParseExact(File.ReadAllText(stamp).Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) published = d;
        return Load(sdn, alt, add, published);
    }

    private sealed class Builder(string id, PartyType type, string? remarks)
    {
        public List<ListedName> Names { get; } = [];
        public List<string> Programs { get; } = [];
        public List<string> Countries { get; } = [];
        public List<int> BirthYears { get; } = [];
        public ListEntry Build() => new(id, type, Names, Programs.Distinct().ToList(), Countries.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), BirthYears.Distinct().ToList(), remarks);
    }

    private static string? Value(string s)
    {
        var t = s.Trim();
        return t.Length == 0 || t == "-0-" ? null : t;
    }

    private static string Id(string s, string file, int line) =>
        s.Trim() is { Length: > 0 and <= 12 } t && t.All(char.IsAsciiDigit) ? t : throw new ListFormatException($"{file} line {line} doesn't start with an entry number.");

    private static string Clean(string s) => new(s.Take(40).Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());

    /// <summary>CSV rows (RFC 4180 quoting). OFAC's files are Latin-1 text ending with an end-of-file byte (0x1A).</summary>
    private static IEnumerable<(List<string> Row, int Line)> Rows(Stream stream, string file)
    {
        using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var line = 1;
        var start = 1;
        var rows = 0;
        int c;
        while ((c = reader.Read()) != -1)
        {
            var ch = (char)c;
            if (quoted)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                    else quoted = false;
                }
                else
                {
                    if (ch == '\n') line++;
                    field.Append(ch);
                }
            }
            else if (ch == '"' && field.Length == 0) quoted = true;
            else if (ch == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (ch == '\r') { }
            else if (ch == '\n' || ch == '\x1A')
            {
                if (ch == '\n') line++;
                if (row.Count > 0 || field.Length > 0)
                {
                    row.Add(field.ToString());
                    field.Clear();
                    if (++rows > MaxRows) throw new ListFormatException($"{file} has more than {MaxRows:N0} rows.");
                    yield return (row, start);
                    row = [];
                }
                start = line;
            }
            else field.Append(ch);
            if (field.Length > MaxField) throw new ListFormatException($"{file} line {line} has a value longer than {MaxField:N0} characters.");
            if (row.Count > 50) throw new ListFormatException($"{file} line {line} has more than 50 columns.");
        }
        if (quoted) throw new ListFormatException($"{file} ends inside a quoted value (from line {start}).");
        if (row.Count > 0 || field.Length > 0)
        {
            row.Add(field.ToString());
            yield return (row, start);
        }
    }

    [GeneratedRegex(@"\[([^\]]{1,40})\]")]
    private static partial Regex ProgramPattern();

    [GeneratedRegex(@"DOB (?:circa )?(?:\d{1,2} [A-Za-z]{3} |[A-Za-z]{3} )?(\d{4})")]
    private static partial Regex BirthYear();

    [GeneratedRegex(@"[Nn]ationality ([A-Za-z][A-Za-z ,.'-]{1,40}?)[;.]")]
    private static partial Regex Nationality();
}

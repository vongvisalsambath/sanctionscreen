using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SanctionScreen;
using SanctionScreen.Cli;

try
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help") return Help();
    var (command, positional, options) = Args.Parse(args);
    var lists = options.GetValueOrDefault("lists") ?? "lists";
    return command switch
    {
        "update" => await Update(lists),
        "check" => Check(positional, options, lists),
        "check-file" => CheckFile(positional, options, lists),
        "export" => Export(options, lists),
        _ => throw new UsageException($"Unknown command {Show.Text(command)}."),
    };
}
catch (UsageException e)
{
    Console.Error.WriteLine("error: " + e.Message);
    Console.Error.WriteLine("Run 'sanctionscreen --help' for the commands.");
    return 2;
}
catch (ListFormatException e)
{
    Console.Error.WriteLine("The list can't be read: " + Show.Text(e.Message, 300));
    return 2;
}

static async Task<int> Update(string folder)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("sanctionscreen/0.1 (+https://github.com/vongvisalsambath/sanctionscreen)");
    var temp = Path.Combine(Path.GetTempPath(), "sanctionscreen-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        DateOnly? published = null;
        foreach (var (name, url) in Ofac.Downloads)
        {
            Console.Error.WriteLine($"downloading {name}…");
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            // OFAC redirects to a file whose address holds the publication date: …/Published/<id>/2026-11-02/…
            var final = response.RequestMessage?.RequestUri?.AbsolutePath ?? "";
            if (Regex.Match(final, @"/(\d{4}-\d{2}-\d{2})/") is { Success: true } m && DateOnly.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                published = d;
            await using var body = await response.Content.ReadAsStreamAsync();
            await using var file = File.Create(Path.Combine(temp, name));
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer)) > 0)
            {
                total += read;
                if (total > Ofac.MaxFileBytes) throw new UsageException($"{name} is larger than {Ofac.MaxFileBytes / 1024 / 1024} MB; that isn't OFAC's list.");
                await file.WriteAsync(buffer.AsMemory(0, read));
            }
        }
        if (published is not null) File.WriteAllText(Path.Combine(temp, "published.txt"), published.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var list = Ofac.LoadFolder(temp); // check it before it replaces anything
        if (list.Entries.Count < 1000) throw new UsageException($"The download holds only {list.Entries.Count} entries; the real list has thousands. Nothing was changed.");
        Directory.CreateDirectory(folder);
        foreach (var f in Directory.GetFiles(temp)) File.Copy(f, Path.Combine(folder, Path.GetFileName(f)), overwrite: true);
        Console.WriteLine($"{list.Entries.Count:N0} entries, {list.Entries.Sum(e => e.Names.Count):N0} names{(published is null ? "" : $", published {published:yyyy-MM-dd}")}, saved in {folder}.");
        return 0;
    }
    catch (HttpRequestException e)
    {
        Console.Error.WriteLine($"Couldn't download the list: {Show.Text(e.Message)}");
        return 2;
    }
    finally
    {
        Directory.Delete(temp, recursive: true);
    }
}

static Screener Load(string folder)
{
    if (!Directory.Exists(folder)) throw new UsageException($"There is no list in {Show.Text(folder)}. Run 'sanctionscreen update' first, or give --lists.");
    var list = Ofac.LoadFolder(folder);
    if (list.Published is DateOnly p && p < DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-14))
        Console.Error.WriteLine($"warning: the list was published on {p:yyyy-MM-dd}; OFAC updates it often. Run 'sanctionscreen update'.");
    return new Screener(list);
}

static Query QueryFrom(string name, Dictionary<string, string> options) => new(
    name,
    options.GetValueOrDefault("type") switch
    {
        null => null,
        "individual" => PartyType.Individual,
        "entity" => PartyType.Entity,
        "vessel" => PartyType.Vessel,
        "aircraft" => PartyType.Aircraft,
        var t => throw new UsageException($"--type is individual, entity, vessel or aircraft, not {Show.Text(t)}."),
    },
    options.GetValueOrDefault("born") is { } b ? int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 1850 and <= 2100 ? y : throw new UsageException("--born is a year, such as 1970.") : null,
    options.GetValueOrDefault("country"));

static double Threshold(Dictionary<string, string> options) =>
    options.GetValueOrDefault("threshold") is { } t
        ? double.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && v is >= 0.5 and <= 1 ? v : throw new UsageException("--threshold is from 0.5 to 1, such as 0.85.")
        : Screener.DefaultThreshold;

static int Check(List<string> positional, Dictionary<string, string> options, string folder)
{
    if (positional.Count != 1) throw new UsageException("'check' takes one name, in quotes.");
    var screener = Load(folder);
    var hits = screener.Screen(QueryFrom(positional[0], options), Threshold(options));
    if (options.ContainsKey("json"))
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            name = positional[0],
            list = new { source = screener.List.Source, published = screener.List.Published?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            hits = hits.Select(h => new { h.Entry.Id, type = h.Entry.Type.ToString().ToLowerInvariant(), name = h.Entry.Name, matched = h.MatchedName.Name, h.Score, h.Entry.Programs, h.Reasons }),
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    else if (hits.Count == 0) Console.WriteLine($"No hits for {Show.Text(positional[0])} in {screener.List.Source}{Published(screener.List)}.");
    else
    {
        Console.WriteLine($"{hits.Count} possible match{(hits.Count == 1 ? "" : "es")} for {Show.Text(positional[0])} in {screener.List.Source}{Published(screener.List)}. Review each one:");
        foreach (var h in hits)
        {
            Console.WriteLine($"  {h.Score:0.00}  {Show.Text(h.Entry.Name)} ({h.Entry.Type.ToString().ToLowerInvariant()}, entry {h.Entry.Id}, {string.Join(" ", h.Entry.Programs.Select(p => Show.Text(p)))})");
            if (h.MatchedName.Kind != "primary") Console.WriteLine($"        matched its {h.MatchedName.Kind}: {Show.Text(h.MatchedName.Name)}");
            Console.WriteLine($"        {Show.Text(string.Join("; ", h.Reasons), 300)}");
        }
    }
    return hits.Count == 0 ? 0 : 1;
}

static int CheckFile(List<string> positional, Dictionary<string, string> options, string folder)
{
    if (positional.Count != 1) throw new UsageException("'check-file' takes one CSV file: a header row, then name[,type,born,country] per line.");
    var screener = Load(folder);
    var threshold = Threshold(options);
    var path = positional[0];
    if (!File.Exists(path)) throw new UsageException($"No such file: {Show.Text(path)}.");
    if (new FileInfo(path).Length > 50L * 1024 * 1024) throw new UsageException("The file is larger than 50 MB.");
    var output = new StringBuilder("row,name,score,entry,listed_name,matched_name,type,programs,reasons\r\n");
    int rows = 0, flagged = 0;
    foreach (var line in File.ReadLines(path).Skip(1))
    {
        if (++rows > 100_000) throw new UsageException("The file has more than 100,000 names; split it.");
        var cells = Csv.Split(line);
        if (cells.Count == 0 || cells[0].Trim().Length == 0) continue;
        var query = QueryFrom(cells[0], new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = cells.ElementAtOrDefault(1) is { Length: > 0 } t ? t.Trim().ToLowerInvariant() : null!,
            ["born"] = cells.ElementAtOrDefault(2) is { Length: > 0 } b ? b.Trim() : null!,
            ["country"] = cells.ElementAtOrDefault(3) is { Length: > 0 } c ? c.Trim() : null!,
        }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value));
        var hits = screener.Screen(query, threshold, 5);
        if (hits.Count > 0) flagged++;
        foreach (var h in hits)
            output.Append(string.Join(',', rows.ToString(CultureInfo.InvariantCulture), Csv.Cell(cells[0]), h.Score.ToString("0.000", CultureInfo.InvariantCulture), Csv.Cell(h.Entry.Id),
                Csv.Cell(h.Entry.Name), Csv.Cell(h.MatchedName.Name), h.Entry.Type.ToString().ToLowerInvariant(), Csv.Cell(string.Join(" ", h.Entry.Programs)), Csv.Cell(string.Join("; ", h.Reasons)))).Append("\r\n");
    }
    if (options.GetValueOrDefault("out") is { } outPath) File.WriteAllText(outPath, output.ToString());
    else Console.Write(output);
    Console.Error.WriteLine($"{rows:N0} names screened, {flagged:N0} with possible matches, against {screener.List.Source}{Published(screener.List)}.");
    return flagged == 0 ? 0 : 1;
}

static int Export(Dictionary<string, string> options, string folder)
{
    var outPath = options.GetValueOrDefault("out") ?? throw new UsageException("Give the output file with --out list.json.");
    var list = Ofac.LoadFolder(folder);
    using (var file = File.Create(outPath)) ListJson.Save(list, file);
    Console.WriteLine($"{list.Entries.Count:N0} entries written to {Show.Text(outPath)}.");
    return 0;
}

static string Published(WatchList list) => list.Published is DateOnly p ? $" (published {p:yyyy-MM-dd})" : "";

static int Help()
{
    Console.WriteLine("""
        sanctionscreen: screen names against OFAC's Specially Designated Nationals (SDN) list.

          sanctionscreen update [--lists DIR]                  download the current list from OFAC (default folder: lists)
          sanctionscreen check "NAME" [options] [--json]      screen one name
          sanctionscreen check-file names.csv [--out FILE]    screen a CSV of names (name[,type,born,country] per row)
          sanctionscreen export --out list.json               write the list as compact JSON (for the website)

          --type individual|entity|vessel|aircraft   only that kind of party
          --born YEAR                                a person's birth year
          --country NAME-OR-CODE                     where the party is
          --threshold 0.5-1                          how alike a name must be to be a hit (default 0.85)

        A screening aid, not a legal determination: review every hit, and check OFAC's own search when in doubt.
        Exit codes: 0 no hits, 1 possible matches to review, 2 usage, list or network error.
        """);
    return 0;
}

namespace SanctionScreen.Cli
{
    internal sealed class UsageException(string message) : Exception(message);

    internal static class Args
    {
        private static readonly HashSet<string> Flags = ["json"];
        private static readonly HashSet<string> Valued = ["lists", "type", "born", "country", "threshold", "out"];

        public static (string, List<string>, Dictionary<string, string>) Parse(string[] args)
        {
            var positional = new List<string>();
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (!a.StartsWith("--", StringComparison.Ordinal)) { positional.Add(a); continue; }
                var name = a[2..];
                if (Flags.Contains(name)) { options[name] = ""; continue; }
                if (!Valued.Contains(name)) throw new UsageException($"Unknown option {Show.Text(a)}.");
                if (i + 1 >= args.Length) throw new UsageException($"{a} needs a value.");
                if (!options.TryAdd(name, args[++i])) throw new UsageException($"{a} is given twice.");
            }
            return (args[0], positional, options);
        }
    }

    /// <summary>Text safe to print: no control or direction characters, cut to a length.</summary>
    internal static class Show
    {
        public static string Text(string? s, int max = 120)
        {
            if (s is null) return "";
            var sb = new StringBuilder();
            foreach (var r in s.EnumerateRunes())
            {
                if (sb.Length >= max) { sb.Append('…'); break; }
                var c = r.Value;
                sb.Append(Rune.IsControl(r) || c is 0x061C or (>= 0x200B and <= 0x200F) or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069) or 0xFEFF ? $"\\u{c:X4}" : r.ToString());
            }
            return sb.ToString();
        }
    }

    internal static class Csv
    {
        /// <summary>One CSV line's cells (quotes allowed; at most 20 cells).</summary>
        public static List<string> Split(string line)
        {
            var cells = new List<string>();
            var cell = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length && cells.Count < 20; i++)
            {
                var c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else cell.Append(c);
                }
                else if (c == '"' && cell.Length == 0) quoted = true;
                else if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); }
                else cell.Append(c);
            }
            if (cells.Count < 20) cells.Add(cell.ToString());
            return cells;
        }

        /// <summary>A quoted cell; a leading = + - @ tab or carriage return gets an apostrophe so spreadsheets don't run it as a formula.</summary>
        public static string Cell(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var text = Show.Text(s, 1000);
            if (text[0] is '=' or '+' or '-' or '@' || s[0] is '\t' or '\r') text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
    }
}

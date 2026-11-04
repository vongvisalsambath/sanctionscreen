using System.Globalization;

namespace SanctionScreen;

/// <summary>
/// Screens names against a list. Names are compared word by word, in any order, allowing for typos and transliteration
/// (Jaro-Winkler similarity), accents, punctuation, company forms (LTD, LLC…) and words written together or apart.
/// A screening aid: every hit needs a person to review it, and no hit doesn't prove a party isn't sanctioned.
/// </summary>
public sealed class Screener
{
    /// <summary>The default threshold: hits must score at least this.</summary>
    public const double DefaultThreshold = 0.85;
    /// <summary>Two words count as the same word from this similarity up.</summary>
    public const double WordMatch = 0.88;

    private sealed record Candidate(ListEntry Entry, ListedName Name, string[] Tokens, string Joined, int Weight);

    private readonly Candidate[] candidates;
    private readonly string[] vocabulary;              // every distinct listed word
    private readonly Dictionary<string, int[]> byWord; // word → candidates using it

    /// <summary>The list being screened against.</summary>
    public WatchList List { get; }

    /// <summary>Prepares a list for screening.</summary>
    public Screener(WatchList list)
    {
        List = list ?? throw new ArgumentNullException(nameof(list));
        var all = new List<Candidate>();
        foreach (var e in list.Entries)
            foreach (var n in e.Names)
            {
                var t = Names.Tokens(n.Name).ToArray();
                if (t.Length > 0) all.Add(new Candidate(e, n, t, string.Concat(t), t.Sum(Weight)));
            }
        candidates = [.. all];
        var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Length; i++)
            foreach (var t in candidates[i].Tokens.Distinct())
                (index.TryGetValue(t, out var l) ? l : index[t] = []).Add(i);
        byWord = index.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
        vocabulary = [.. byWord.Keys];
    }

    /// <summary>Hits scoring at least <paramref name="threshold"/>, best first, one per listed party, at most <paramref name="max"/>.</summary>
    public IReadOnlyList<Hit> Screen(Query query, double threshold = DefaultThreshold, int max = 25)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (threshold is < 0.5 or > 1) throw new ArgumentOutOfRangeException(nameof(threshold), "The threshold is from 0.5 to 1.");
        var q = Names.Tokens(query.Name ?? "").ToArray();
        if (q.Length == 0) return [];
        var qJoined = string.Concat(q);

        // which listed words are like a query word: one pass over the vocabulary, not one per listed name
        var similar = new Dictionary<string, double>(StringComparer.Ordinal);
        var maybe = new HashSet<int>();
        foreach (var word in vocabulary)
        {
            var closest = 0.0;
            foreach (var t in q) closest = Math.Max(closest, Similarity(t, word));
            if (closest >= WordMatch) { similar[word] = closest; foreach (var i in byWord[word]) maybe.Add(i); }
        }
        // words written together or apart ("BINLADEN", "BIN LADEN"): also consider names whose joined form is close
        if (q.Length <= 3)
            for (var i = 0; i < candidates.Length; i++)
                if (candidates[i].Tokens.Length != q.Length && Math.Abs(candidates[i].Joined.Length - qJoined.Length) <= 2 && candidates[i].Joined[0] == qJoined[0])
                    maybe.Add(i);

        var best = new Dictionary<string, Hit>(StringComparer.Ordinal);
        foreach (var i in maybe)
        {
            var c = candidates[i];
            if (query.Type is { } type && c.Entry.Type != type) continue;
            var (score, reasons) = Score(q, qJoined, c);
            if (score < threshold - 0.15) continue;
            (score, reasons) = Adjust(query, c.Entry, score, reasons);
            if (score < threshold) continue;
            if (!best.TryGetValue(c.Entry.Id, out var existing) || existing.Score < score)
                best[c.Entry.Id] = new Hit(c.Entry, c.Name, Math.Round(score, 3), reasons);
        }
        return best.Values.OrderByDescending(h => h.Score).ThenBy(h => h.Entry.Name, StringComparer.Ordinal).Take(max).ToList();
    }

    private static int Weight(string token) => token.Length == 1 ? 1 : Math.Min(token.Length, 8);

    /// <summary>
    /// Word similarity: an initial matches any word starting with it, and an Arabic article written onto a word
    /// ("ALFICTIONAL") may be left off, if at least five letters remain (so ALBERT doesn't become BERT).
    /// </summary>
    private static double Similarity(string a, string b)
    {
        if (a.Length == 1 || b.Length == 1) return a[0] == b[0] ? 0.9 : 0;
        var s = Names.JaroWinkler(a, b);
        if (s < 1 && WithoutArticle(a) is { } a2) s = Math.Max(s, Names.JaroWinkler(a2, b) * 0.95);
        if (s < 1 && WithoutArticle(b) is { } b2) s = Math.Max(s, Names.JaroWinkler(a, b2) * 0.95);
        return s;
    }

    private static string? WithoutArticle(string w) => w.Length >= 7 && (w.StartsWith("AL", StringComparison.Ordinal) || w.StartsWith("EL", StringComparison.Ordinal)) ? w[2..] : null;

    private static (double, List<string>) Score(string[] q, string qJoined, Candidate c)
    {
        var reasons = new List<string>();
        var used = new bool[c.Tokens.Length];
        double matched = 0, matchedListWeight = 0;
        var qWeight = q.Sum(Weight);
        var matchedWords = 0;
        // pair each query word with its most similar unused listed word, the longest words first
        foreach (var t in q.OrderByDescending(x => x.Length))
        {
            int bestJ = -1;
            var bestSim = 0.0;
            for (var j = 0; j < c.Tokens.Length; j++)
            {
                if (used[j]) continue;
                var s = Similarity(t, c.Tokens[j]);
                if (s > bestSim) { bestSim = s; bestJ = j; }
            }
            if (bestJ >= 0 && bestSim >= WordMatch)
            {
                used[bestJ] = true;
                // an initial is credited as one letter, however long the word it stands for
                matched += bestSim * Math.Min(Weight(t), Weight(c.Tokens[bestJ]));
                matchedListWeight += Weight(c.Tokens[bestJ]);
                matchedWords++;
                reasons.Add(t == c.Tokens[bestJ] ? $"{t} = {c.Tokens[bestJ]}"
                    : t.Length == 1 || c.Tokens[bestJ].Length == 1 ? $"{t} ≈ {c.Tokens[bestJ]} (initial)"
                    : $"{t} ≈ {c.Tokens[bestJ]} ({bestSim.ToString("0.00", CultureInfo.InvariantCulture)} alike)");
            }
            else
            {
                // a word written together that the list writes apart ("ABDULRAHMAN", "ABDUL RAHMAN")
                var fused = -1;
                var fusedSim = 0.0;
                for (var j = 0; j + 1 < c.Tokens.Length; j++)
                {
                    if (used[j] || used[j + 1]) continue;
                    var sim = Similarity(t, c.Tokens[j] + c.Tokens[j + 1]);
                    if (sim > fusedSim) { fusedSim = sim; fused = j; }
                }
                if (fused >= 0 && fusedSim >= WordMatch)
                {
                    used[fused] = used[fused + 1] = true;
                    matched += fusedSim * Weight(t);
                    matchedListWeight += Weight(c.Tokens[fused]) + Weight(c.Tokens[fused + 1]);
                    matchedWords += 2;
                    reasons.Add($"{t} ≈ {c.Tokens[fused]} {c.Tokens[fused + 1]} written as one word");
                }
                else reasons.Add($"{t} isn't in the listed name");
            }
        }
        var missing = c.Tokens.Where((t, j) => !used[j]).ToList();
        if (missing.Count > 0) reasons.Add($"the listed name also has {string.Join(" ", missing)}");

        // how much of the query is in the listed name, lowered a little for listed words the query lacks
        var score = (matched / qWeight) * (0.85 + 0.15 * matchedListWeight / c.Weight);
        // one word can't identify a party that has several
        if (matchedWords < 2 && c.Tokens.Length >= 2) score *= 0.85;

        // words written together or apart
        if (q.Length != c.Tokens.Length)
        {
            var joined = Names.EditSimilarity(qJoined, c.Joined) * 0.97;
            if (joined > score && joined >= WordMatch)
            {
                score = joined;
                reasons.Insert(0, $"{qJoined} ≈ {c.Joined} written as one word ({joined.ToString("0.00", CultureInfo.InvariantCulture)} alike)");
            }
        }
        return (Math.Min(1, score), reasons);
    }

    private static (double, List<string>) Adjust(Query query, ListEntry e, double score, List<string> reasons)
    {
        if (query.BirthYear is int born && e.BirthYears.Count > 0)
        {
            var nearest = e.BirthYears.Min(y => Math.Abs(y - born));
            if (nearest <= 2) { score = Math.Min(1, score + 0.03); reasons.Add($"birth year {born} matches the listed {string.Join(" or ", e.BirthYears)}"); }
            else { score *= 0.85; reasons.Add($"born {born}, but the listed birth year is {string.Join(" or ", e.BirthYears)}"); }
        }
        if (query.Country is { Length: > 0 } country && e.Countries.Count > 0)
        {
            var wanted = Countries.Normalize(country);
            if (e.Countries.Any(c => Countries.Normalize(c) == wanted)) reasons.Add($"listed with {country}");
            else { score *= 0.9; reasons.Add($"listed with {string.Join(", ", e.Countries.Take(3))}, not {country}"); }
        }
        return (score, reasons);
    }
}

/// <summary>Country names and codes, compared loosely.</summary>
internal static class Countries
{
    private static readonly Dictionary<string, string> Codes = new(StringComparer.Ordinal)
    {
        ["US"] = "UNITED STATES", ["USA"] = "UNITED STATES", ["GB"] = "UNITED KINGDOM", ["UK"] = "UNITED KINGDOM", ["RU"] = "RUSSIA", ["CN"] = "CHINA",
        ["IR"] = "IRAN", ["KP"] = "KOREA NORTH", ["SY"] = "SYRIA", ["CU"] = "CUBA", ["VE"] = "VENEZUELA", ["KH"] = "CAMBODIA", ["TH"] = "THAILAND",
        ["VN"] = "VIETNAM", ["MY"] = "MALAYSIA", ["SG"] = "SINGAPORE", ["ID"] = "INDONESIA", ["PH"] = "PHILIPPINES", ["MM"] = "BURMA", ["LA"] = "LAOS",
        ["AE"] = "UNITED ARAB EMIRATES", ["TR"] = "TURKEY", ["IQ"] = "IRAQ", ["AF"] = "AFGHANISTAN", ["PK"] = "PAKISTAN", ["LB"] = "LEBANON", ["BY"] = "BELARUS",
        ["UA"] = "UKRAINE", ["DE"] = "GERMANY", ["FR"] = "FRANCE", ["HK"] = "HONG KONG", ["MX"] = "MEXICO", ["CO"] = "COLOMBIA", ["IN"] = "INDIA",
    };

    public static string Normalize(string s)
    {
        var words = string.Join(" ", Names.Tokens(s));
        if (Codes.TryGetValue(words, out var name)) return name;
        return words switch
        {
            "NORTH KOREA" or "KOREA DEMOCRATIC PEOPLES REPUBLIC" => "KOREA NORTH",
            "MYANMAR" => "BURMA",
            "TURKIYE" => "TURKEY",
            "RUSSIAN FEDERATION" => "RUSSIA",
            "VIET NAM" => "VIETNAM",
            _ => words,
        };
    }
}

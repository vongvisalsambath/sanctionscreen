using System.Globalization;
using System.Text;

namespace SanctionScreen;

/// <summary>Name normalisation and similarity.</summary>
public static partial class Names
{
    /// <summary>The longest name screened, in characters.</summary>
    public const int MaxLength = 300;
    /// <summary>The most words in a name that are compared.</summary>
    public const int MaxTokens = 16;

    // words that say what kind of company something is, not which one
    private static readonly HashSet<string> LegalForms = new(StringComparer.Ordinal)
    {
        "LTD", "LIMITED", "LLC", "LLP", "INC", "INCORPORATED", "CORP", "CORPORATION", "CO", "COMPANY", "PLC", "SA", "SAS", "SARL", "SRL", "SPA",
        "GMBH", "AG", "KG", "BV", "NV", "AB", "AS", "OY", "JSC", "OJSC", "CJSC", "PJSC", "OOO", "ZAO", "OAO", "PAO", "AO", "PTE", "PVT", "PT", "TBK",
        "SDN", "BHD", "LLP", "FZE", "FZCO", "FZ", "DMCC", "KFT", "ZRT", "DOO", "SP", "ZOO", "THE", "AND", "OF", "DE", "DEL", "LA", "LE",
    };

    // titles and particles that don't identify a person
    private static readonly HashSet<string> Honorifics = new(StringComparer.Ordinal) { "MR", "MRS", "MS", "MISS", "DR", "PROF", "SIR", "SHEIKH", "SHAYKH", "HAJI", "HAJJI" };

    /// <summary>
    /// The words of a name, ready to compare: accents removed, uppercase, punctuation as spaces, company forms and titles
    /// dropped, "al-" particles separated. Order is kept; comparison ignores it.
    /// </summary>
    public static IReadOnlyList<string> Tokens(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length > MaxLength) name = name[..MaxLength];
        // "S.A." and "J.P." are one word each: join letters that dots separate
        name = DottedLetters().Replace(name, "");
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Normalize(NormalizationForm.FormKD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            // accents, and invisible formatting (zero-width spaces, direction marks): dropped, so they can't split or hide a word
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.Format) continue;
            var c = char.ToUpperInvariant(ch);
            sb.Append(c switch
            {
                'ß' => "SS", 'Æ' => "AE", 'Ø' => "O", 'Œ' => "OE", 'Đ' => "D", 'Ł' => "L", 'Þ' => "TH", 'İ' => "I", 'ı' => "I",
                '\'' or '’' or '`' or 'ʼ' => "", // O'BRIEN → OBRIEN
                _ when char.IsLetterOrDigit(c) => c.ToString(),
                _ => " ",
            });
        }
        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var kept = words.Where(w => !LegalForms.Contains(w) && !Honorifics.Contains(w)).ToList();
        if (kept.Count == 0) kept = words; // a name that is only such words ("THE COMPANY") keeps them
        // "ALQAIDA" and "AL QAIDA" should meet: drop a standalone AL/EL particle, it is optional in transliteration
        var result = kept.Where(w => w is not ("AL" or "EL") || kept.Count == 1).Take(MaxTokens).ToList();
        return result.Count == 0 ? kept.Take(MaxTokens).ToList() : result;
    }

    /// <summary>1 minus the edit distance over the longer length: strict, for comparing whole names written as one word. Strings over 120 characters score 0.</summary>
    public static double EditSimilarity(string a, string b)
    {
        if (a == b) return 1;
        if (a.Length == 0 || b.Length == 0 || a.Length > 120 || b.Length > 120) return 0;
        Span<int> prev = stackalloc int[b.Length + 1];
        Span<int> cur = stackalloc int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            cur.CopyTo(prev);
        }
        return 1 - (double)prev[b.Length] / Math.Max(a.Length, b.Length);
    }

    /// <summary>Jaro-Winkler similarity of two words, from 0 (nothing alike) to 1 (the same).</summary>
    public static double JaroWinkler(string a, string b)
    {
        if (a == b) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        var range = Math.Max(0, Math.Max(a.Length, b.Length) / 2 - 1);
        Span<bool> aMatched = a.Length <= 128 ? stackalloc bool[a.Length] : new bool[a.Length];
        Span<bool> bMatched = b.Length <= 128 ? stackalloc bool[b.Length] : new bool[b.Length];
        var matches = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var lo = Math.Max(0, i - range);
            var hi = Math.Min(b.Length - 1, i + range);
            for (var j = lo; j <= hi; j++)
            {
                if (bMatched[j] || a[i] != b[j]) continue;
                aMatched[i] = bMatched[j] = true;
                matches++;
                break;
            }
        }
        if (matches == 0) return 0;
        var transpositions = 0;
        for (int i = 0, k = 0; i < a.Length; i++)
        {
            if (!aMatched[i]) continue;
            while (!bMatched[k]) k++;
            if (a[i] != b[k]) transpositions++;
            k++;
        }
        double m = matches;
        var jaro = (m / a.Length + m / b.Length + (m - transpositions / 2.0) / m) / 3;
        var prefix = 0;
        while (prefix < Math.Min(4, Math.Min(a.Length, b.Length)) && a[prefix] == b[prefix]) prefix++;
        return jaro + prefix * 0.1 * (1 - jaro);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=(?:^|[^\p{L}])\p{L})\.(?=\p{L}(?:[^\p{L}]|$))")]
    private static partial System.Text.RegularExpressions.Regex DottedLetters();
}

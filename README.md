# SanctionScreen

Screen a person, company, ship or aircraft against the US Treasury's **Specially Designated Nationals (SDN) list**. Matching copes with:
- typos and other spellings;
- word order;
- accents;
- company forms such as LTD, LLC, S.A. and FZE;
- initials;
- words written together or apart.

Every possible match says why it was flagged. SanctionScreen comes as a .NET library, a `sanctionscreen` command, and a [website](site/SanctionScreen.Web) that screens in the browser.

```
$ sanctionscreen update
19,499 entries, 39,707 names, published 2026-11-02, saved in lists.

$ sanctionscreen check "Banko Nasional de Cuba"
1 possible match for Banko Nasional de Cuba in OFAC SDN (published 2026-11-02). Review each one:
  0.94  BANCO NACIONAL DE CUBA (entity, entry 306, CUBA)
        NASIONAL ≈ NACIONAL (0.93 alike); BANKO ≈ BANCO (0.91 alike); CUBA = CUBA
```

**A screening aid, not a legal determination.** A possible match needs a person to compare the details: birth date, address, identifiers. No match doesn't prove a party isn't sanctioned: it may be listed under a name this matcher can't relate to, or be owned by a listed party. Only OFAC's SDN list is covered. The EU, UN and UK lists, and OFAC's other lists, aren't.

## Library

```csharp
using SanctionScreen;

var list = Ofac.LoadFolder("lists");                  // SDN.CSV, ALT.CSV, ADD.CSV from OFAC
var screener = new Screener(list);
var hits = screener.Screen(new Query("Jonathan Doe", PartyType.Individual, BirthYear: 1970, Country: "KH"));
foreach (var h in hits)
    Console.WriteLine($"{h.Score:0.00} {h.Entry.Name} [{string.Join(" ", h.Entry.Programs)}]: {string.Join("; ", h.Reasons)}");
```

### How names are compared

1. **Normalising.**
   - Accents are removed and everything is uppercased.
   - Punctuation becomes spaces, and dotted abbreviations are joined (S.A. becomes SA).
   - Company forms (LTD, LLC, GMBH, JSC, FZE…) and titles (MR, DR, SHEIKH…) are dropped, and so is a standalone Arabic article (AL, EL).
2. **Pairing words.** Each word of the query is paired with the most similar unused word of a listed name, in any order.
   - Words count as the same from a Jaro-Winkler similarity of 0.88.
   - An initial matches the words it stands for, but counts as one letter.
   - A word written together ("ABDULRAHMAN") can match two listed words ("ABDUL RAHMAN").
   - An article written onto a word ("ALFICTIONAL") may be left off, if at least five letters remain.
3. **Scoring.**
   - The score is how much of the query was found, weighted by word length, lowered a little for listed words the query lacks.
   - One matching word can't identify a party with several.
   - Names written as one word on one side are also compared whole, by edit distance.
4. **Adjusting.** A listed birth year more than two years away lowers the score; so does a country that doesn't fit any of the party's addresses or nationality. Matching ones raise it a little.

Each listed party appears once, with its best-matching name, primary or alias. The default threshold is 0.85.

Tested on the real list:
- the listed bank is found when misspelt ("Banko Nasional de Cuba"), reordered ("Nacional Banco Cuba") or with a different company form;
- these return nothing: John Smith, Sokha Chan, Chea Sophal, Nguyen Van An, Tan Wei Ming, Albert Einstein, Alexander Hamilton, Acme Trading LLC and Phnom Penh Commercial Bank.

Common names such as "Mohammed Ali" return many candidates, as they do in any screening; that is what the birth year and country are for.

## Command line

```
sanctionscreen update [--lists DIR]                   download the current list from OFAC
sanctionscreen check "NAME" [--type individual|entity|vessel|aircraft] [--born YEAR] [--country X] [--threshold 0.85] [--json]
sanctionscreen check-file names.csv [--out report.csv]   name[,type,born,country] per row, with a header row
sanctionscreen export --out list.json                 the list as compact JSON, for the website
```

`update` downloads into a temporary folder, checks that the files parse and hold thousands of entries, and only then replaces the old copy. It records the publication date from OFAC's download address. `check` warns when the list is more than two weeks old. Exit codes: 0 no hits, 1 possible matches, 2 usage, list or network error.

## Website

`site/SanctionScreen.Web` is a Blazor WebAssembly page using the same library. Its build:
1. downloads the list from OFAC;
2. exports it as JSON (3.7 MB, 0.6 MB compressed);
3. publishes the page.

The list is therefore as current as the last deploy, and the page shows its publication date. The page loads and indexes the list in about 4 seconds, then screens as you type. Nothing you type leaves the browser.

```
dotnet run --project src/SanctionScreen.Cli -- update
dotnet run --project src/SanctionScreen.Cli -- export --out site/SanctionScreen.Web/wwwroot/data/list.json
dotnet publish site/SanctionScreen.Web -c Release -o out
cd site/test && npm ci && npx playwright install chromium && node e2e.cjs ../../out/wwwroot
```

## Tests

`dotnet test` uses a made-up list in OFAC's exact format. It covers:
- reading;
- matching: word order, typos, initials, accents, aliases, fused words, company forms and former names;
- names that must not match;
- birth year, country and kind;
- hostile queries;
- broken and oversized lists;
- the JSON form, and CSV safety.

CI also downloads the real list and checks a known entity.

## Security

See [SECURITY.md](SECURITY.md).

## Licence

Apache-2.0 (see [LICENSE](LICENSE)). The SDN list is published by the US Treasury's Office of Foreign Assets Control; this project isn't affiliated with OFAC or the US government. Written with the help of Claude (Anthropic).

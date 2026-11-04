# Security

A screening tool fails in two ways that matter: it misses a listed party, or it can be made to. It also handles names of real people, and reads files and lists from outside. SanctionScreen is built with all of that in mind.

## Reporting a problem

Please report vulnerabilities privately through GitHub's **Security → Report a vulnerability** on this repository, not in a public issue. A name that should match but doesn't is welcome as a normal issue, with made-up data.

## What is protected, and how

| Threat | Protection | Tested by |
|---|---|---|
| **Evading the screen with invisible characters** | Characters that don't print (zero-width spaces, direction overrides, controls) are dropped when names are normalised, so "Jona‹zero-width space›than" is screened as "Jonathan". | `HostileQueriesAreHandledQuickly`; website "an invisible character doesn't hide a listed name" |
| **Evading it with spelling** | Typos, transliteration, word order, accents, punctuation, company forms, initials, and words joined or split are all matched. | `ListedPartiesAreFound` (13 variants), real-list checks in the README |
| **A silently stale or broken list** | `update` replaces the list only after the new one parses and has thousands of entries. `check` warns when the list is over two weeks old. The website shows the publication date and says when the list couldn't load, never "no matches". | `ABrokenListIsRefused`; the website's load check |
| **Hostile list files** | Files are at most 64 MB, with at most 500,000 rows, 50 columns and 20,000 characters a value. Each entry has at most 200 names. Column counts, entry numbers and types are checked. A list loaded as JSON is read with a byte limit even when its length is unknown, with depth limited to 8. | `ABrokenListIsRefused`, `AHostileListIsBounded`, `BadJsonIsRefused`, `AnOversizedListIsRefusedWhileReading` |
| **Slow queries** | Names are cut to 300 characters and 16 words. Every listed word is compared once per query, not once per listed name. Comparing names written as one word is limited to 120 characters. | `HostileQueriesAreHandledQuickly` (a million characters, 10,000 words, emoji: under 2 s together) |
| **Spreadsheet formulas in reports** | CSV cells starting with `=`, `+`, `-` or `@`, or a tab or carriage return, get a leading apostrophe; cells are quoted and controls escaped. Input files are limited to 50 MB and 100,000 names. | `CsvCellsCantBecomeFormulas`, `CsvLinesAreSplitWithQuotesAndBounded` |
| **Terminal escapes** | Everything printed has control and direction characters written as codes, and is cut to a length. | manual checks |
| **Names leaving the device** | The website screens in the browser; nothing typed is sent. The Content-Security-Policy allows connections only to the page's own origin, to load the list. The page can't be framed. | `site/test/e2e.cjs`: every request is same-origin |
| **Downloading the list** | Only over HTTPS from OFAC's own service, with a 3-minute timeout and a 64 MB limit per file. | manual checks; CI downloads it on every run |
| **Supply chain** | The library and command have no dependencies outside .NET. The browser tests use Playwright at an exact version with a lockfile. CI runs `npm audit` and `dotnet list package --vulnerable`, and pins GitHub Actions to commits. | CI |

## What it doesn't do

- It doesn't decide whether a party is sanctioned. That needs a person, the full record, and often legal advice.
- It covers only OFAC's SDN list. It doesn't cover OFAC's other lists, or the EU, UN and UK lists. It also doesn't apply ownership rules: an entity owned 50% or more by listed parties is blocked even if it isn't named.
- A threshold is a trade-off: lower it and more real matches are caught, along with more people who merely share a name.

using System.Text;
using Xunit;

namespace SanctionScreen.Tests;

public sealed class ScreeningTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static readonly WatchList List = Ofac.LoadFolder(Fixtures);
    private static readonly Screener S = new(List);

    private static Hit? Top(string name, PartyType? type = null, int? born = null, string? country = null) =>
        S.Screen(new Query(name, type, born, country)).FirstOrDefault();

    [Fact]
    public void TheListIsReadWithAliasesAddressesAndDetails()
    {
        Assert.Equal(("OFAC SDN", new DateOnly(2026, 11, 2), 7), (List.Source, List.Published, List.Entries.Count));
        var doe = List.Entries.Single(e => e.Id == "102");
        Assert.Equal(PartyType.Individual, doe.Type);
        Assert.Equal(["DEMO-2", "DEMO-3"], doe.Programs);
        Assert.Equal([1970], doe.BirthYears);
        Assert.Equal(["Freedonia"], doe.Countries);
        Assert.Equal([1965, 1966], List.Entries.Single(e => e.Id == "103").BirthYears);
        Assert.Equal([1980], List.Entries.Single(e => e.Id == "104").BirthYears);
        Assert.Equal(PartyType.Vessel, List.Entries.Single(e => e.Id == "105").Type);
        Assert.Equal(["United Arab Emirates"], List.Entries.Single(e => e.Id == "106").Countries);
        Assert.Equal([new ListedName("NORTHWIND BANK", "primary"), new ListedName("NORTHWIND JOINT STOCK BANK", "fka")], List.Entries.Single(e => e.Id == "107").Names);
    }

    [Theory]
    [InlineData("Jonathan Quincy Doe", "102")]               // word order
    [InlineData("DOE, Jonathan Quincy", "102")]
    [InlineData("Jonathon Doe", "102")]                      // a typo, and the middle name missing
    [InlineData("J. Quincy Doe", "102")]                     // an initial
    [InlineData("María José García López", "103")]           // accents
    [InlineData("maria-jose garcia lopez", "103")]           // case and punctuation
    [InlineData("Abd al-Rahman al Fictional", "104")]        // an alias
    [InlineData("Abdulrahman Alfictional", "104")]           // words written together
    [InlineData("Blue Horizon Trading", "106")]              // without the company form
    [InlineData("Blue Horizon Trading L.L.C.", "106")]       // with a different one
    [InlineData("Northwind Joint-Stock Bank", "107")]        // a former name
    [InlineData("Example Import & Export Company Limited", "101")]
    [InlineData("Sea Phantom", "105")]
    public void ListedPartiesAreFound(string name, string id)
    {
        var hit = Top(name);
        Assert.NotNull(hit);
        Assert.Equal(id, hit.Entry.Id);
        Assert.NotEmpty(hit.Reasons);
    }

    [Theory]
    [InlineData("John Smith")]
    [InlineData("Jane Doe")]                      // one word in common isn't a match
    [InlineData("Doe")]
    [InlineData("Bank")]
    [InlineData("Northwind Trading")]
    [InlineData("Sokha Chan")]
    [InlineData("Red Horizon Shipping")]
    [InlineData("Trading")]
    [InlineData("Albert Horizon")]               // ALBERT doesn't lose its AL
    public void UnrelatedNamesAreNotHits(string name) => Assert.Null(Top(name));

    [Fact]
    public void APartialNameIsAHit_ButScoresBelowTheFullName()
    {
        // people often drop a second surname or a middle name, so screening must catch them
        var partial = Top("Maria Garcia")!;
        var full = Top("Maria Jose Garcia Lopez")!;
        Assert.Equal(("103", "103"), (partial.Entry.Id, full.Entry.Id));
        Assert.True(partial.Score < full.Score);
        Assert.Contains(partial.Reasons, r => r.Contains("also has", StringComparison.Ordinal));
    }

    [Fact]
    public void TypeBirthYearAndCountryRefineHits()
    {
        Assert.Null(Top("Sea Phantom", PartyType.Entity));
        Assert.NotNull(Top("Sea Phantom", PartyType.Vessel));

        var same = Top("Jonathan Quincy Doe", born: 1971)!;
        var other = S.Screen(new Query("Jonathan Quincy Doe", BirthYear: 1990), threshold: 0.5).Single(h => h.Entry.Id == "102");
        Assert.True(same.Score > other.Score);
        Assert.Contains(same.Reasons, r => r.Contains("matches the listed 1970", StringComparison.Ordinal));
        Assert.Contains(other.Reasons, r => r.Contains("born 1990", StringComparison.Ordinal));

        var uae = Top("Blue Horizon Trading", country: "AE")!;
        var elsewhere = S.Screen(new Query("Blue Horizon Trading", Country: "Cambodia"), threshold: 0.5).Single();
        Assert.True(uae.Score > elsewhere.Score);
        Assert.Contains(uae.Reasons, r => r.Contains("listed with AE", StringComparison.Ordinal));
    }

    [Fact]
    public void OneHitPerParty_BestFirst()
    {
        var hits = S.Screen(new Query("Northwind Bank"), threshold: 0.5);
        Assert.Equal(hits.Count, hits.Select(h => h.Entry.Id).Distinct().Count());
        Assert.Equal("NORTHWIND BANK", hits[0].MatchedName.Name);
        Assert.Equal(1.0, hits[0].Score);
        Assert.Equal(hits.OrderByDescending(h => h.Score).Select(h => h.Score), hits.Select(h => h.Score));
    }

    [Fact]
    public void NamesAreNormalised()
    {
        Assert.Equal(["BLUE", "HORIZON", "TRADING"], Names.Tokens("Blue Horizon Trading F.Z.E."));
        Assert.Equal(["OBRIEN", "SEAN"], Names.Tokens("O'Brien, Seán"));
        Assert.Equal(["STRASSE"], Names.Tokens("Straße"));
        Assert.Equal(["QAIDA"], Names.Tokens("al-Qaida"));
        Assert.Equal(["THE", "COMPANY"], Names.Tokens("The Company"));   // only such words: kept
        Assert.Equal(1.0, Names.JaroWinkler("MARTHA", "MARTHA"));
        Assert.InRange(Names.JaroWinkler("MARTHA", "MARHTA"), 0.96, 0.962);  // the textbook value, 0.961
        Assert.InRange(Names.EditSimilarity("BINLADEN", "BINLADIN"), 0.87, 0.88);
    }

    [Fact]
    public void HostileQueriesAreHandledQuickly()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(S.Screen(new Query("")));
        Assert.Empty(S.Screen(new Query("   ,.;  ")));
        _ = S.Screen(new Query(new string('A', 1_000_000)));
        _ = S.Screen(new Query(string.Join(" ", Enumerable.Repeat("DOE", 10_000))));
        _ = S.Screen(new Query("\u202EDoe\u0000\u001B[2J Jonathan"));
        _ = S.Screen(new Query(string.Concat(Enumerable.Repeat("\U0001F600", 5000))));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms");
        Assert.Throws<ArgumentOutOfRangeException>(() => S.Screen(new Query("Doe"), threshold: 0.1));
        Assert.Equal("102", Top("\u202EJonathan\u200B Quincy Doe")?.Entry.Id);  // invisible characters don't hide a name
        var hidden = Top("Jona\u200Bthan Quin\u2060cy D\u00ADoe");                // not even inside words
        Assert.Equal(("102", 1.0), (hidden?.Entry.Id, hidden?.Score));
    }

    private static WatchList Load(string sdn, string? alt = null) =>
        Ofac.Load(new MemoryStream(Encoding.Latin1.GetBytes(sdn)), alt is null ? null : new MemoryStream(Encoding.Latin1.GetBytes(alt)));

    [Theory]
    [InlineData("1,\"A\",-0- ,\"X\"\r\n", "has 4 columns")]
    [InlineData("x,\"A\",-0- ,\"X\",-0- ,-0- ,-0- ,-0- ,-0- ,-0- ,-0- ,-0- \r\n", "entry number")]
    [InlineData("1,\"A\",\"dragon\",\"X\",-0- ,-0- ,-0- ,-0- ,-0- ,-0- ,-0- ,-0- \r\n", "unknown type")]
    [InlineData("1,\"A\",-0- ,\"X\",-0- ,-0- ,-0- ,-0- ,-0- ,-0- ,-0- ,-0- \r\n1,\"B\",-0- ,\"X\",-0- ,-0- ,-0- ,-0- ,-0- ,-0- ,-0- ,-0- \r\n", "repeats")]
    [InlineData("1,\"A", "quoted")]
    [InlineData("1,-0- ,-0- ,\"X\",-0- ,-0- ,-0- ,-0- ,-0- ,-0- ,-0- ,-0- \r\n", "no name")]
    [InlineData("", "no entries")]
    public void ABrokenListIsRefused(string sdn, string reason) =>
        Assert.Contains(reason, Assert.Throws<ListFormatException>(() => Load(sdn)).Message);

    [Fact]
    public void AHostileListIsBounded()
    {
        Assert.Contains("longer than", Assert.Throws<ListFormatException>(() => Load("1,\"" + new string('A', 30_000) + "\"")).Message);
        Assert.Contains("50 columns", Assert.Throws<ListFormatException>(() => Load(string.Concat(Enumerable.Repeat("a,", 60)))).Message);
        Assert.Contains("the ALT format has 5", Assert.Throws<ListFormatException>(() => Load(File.ReadAllText(Path.Combine(Fixtures, "SDN.CSV"), Encoding.Latin1), "1,2,3\r\n")).Message);
        // an entry with a flood of aliases keeps at most 200 names
        var aliases = string.Concat(Enumerable.Range(0, 1000).Select(i => $"7,{i},\"aka\",\"NAME {i}\",-0- \r\n"));
        var list = Load("7,\"X\",-0- ,\"P\",-0- ,-0- ,-0- ,-0- ,-0- ,-0- ,-0- ,-0- \r\n", aliases);
        Assert.Equal(200, list.Entries[0].Names.Count);
    }
}

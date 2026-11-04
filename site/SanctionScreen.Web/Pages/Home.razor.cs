using System.Globalization;

namespace SanctionScreen.Web.Pages;

public partial class Home
{
    private Screener? screener;
    private string? loadError;
    private string name = "";
    private string type = "";
    private string born = "";
    private string country = "";
    private double threshold = Screener.DefaultThreshold;
    private IReadOnlyList<Hit> hits = [];
    private string status = "";

    protected override async Task OnInitializedAsync()
    {
        try
        {
            await using var stream = await Http.GetStreamAsync("data/list.json");
            var list = await ListJson.LoadAsync(stream);
            screener = new Screener(list);
            status = $"{list.Entries.Count:N0} listed parties, {list.Entries.Sum(e => e.Names.Count):N0} names.";
        }
        catch (Exception)
        {
            // whatever went wrong, say so: a screen that silently never loads looks like "no matches"
            loadError = "The list couldn't be loaded, so nothing can be screened. Try reloading the page.";
        }
    }

    private void Run()
    {
        hits = [];
        if (screener is null || string.IsNullOrWhiteSpace(name)) return;
        var kind = type switch
        {
            "individual" => PartyType.Individual, "entity" => PartyType.Entity, "vessel" => PartyType.Vessel, "aircraft" => PartyType.Aircraft, _ => (PartyType?)null,
        };
        int? year = int.TryParse(born, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 1850 and <= 2100 ? y : null;
        hits = screener.Screen(new Query(name, kind, year, string.IsNullOrWhiteSpace(country) ? null : country), Math.Clamp(threshold, 0.5, 1));
    }

    private static string Kind(PartyType t) => t switch
    {
        PartyType.Individual => "person", PartyType.Vessel => "ship", PartyType.Aircraft => "aircraft", _ => "company or organisation",
    };

    private static string AliasKind(string k) => k switch { "aka" => "also known as", "fka" => "formerly known as", "nka" => "now known as", _ => k };
}

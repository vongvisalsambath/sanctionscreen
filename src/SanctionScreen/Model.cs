namespace SanctionScreen;

/// <summary>What a listed party is.</summary>
public enum PartyType
{
    /// <summary>A company, organisation, government body or other entity.</summary>
    Entity,
    /// <summary>A person.</summary>
    Individual,
    /// <summary>A ship.</summary>
    Vessel,
    /// <summary>An aircraft.</summary>
    Aircraft,
}

/// <summary>A name a listed party is known by.</summary>
/// <param name="Name">The name as the list writes it.</param>
/// <param name="Kind">"primary", or the list's alias kind: "aka" (also known as), "fka" (formerly), "nka" (now known as).</param>
public sealed record ListedName(string Name, string Kind);

/// <summary>One party on a sanctions list.</summary>
public sealed record ListEntry(
    string Id,
    PartyType Type,
    IReadOnlyList<ListedName> Names,
    IReadOnlyList<string> Programs,
    IReadOnlyList<string> Countries,
    IReadOnlyList<int> BirthYears,
    string? Remarks)
{
    /// <summary>The main name.</summary>
    public string Name => Names[0].Name;
}

/// <summary>A sanctions list, with where it came from and when.</summary>
/// <param name="Source">Such as "OFAC SDN".</param>
/// <param name="Published">The publication date, when known.</param>
/// <param name="Entries">Every listed party.</param>
public sealed record WatchList(string Source, DateOnly? Published, IReadOnlyList<ListEntry> Entries);

/// <summary>What to screen: a name, and optional details that make a match more or less likely.</summary>
/// <param name="Name">The name to look for.</param>
/// <param name="Type">Only consider parties of this type.</param>
/// <param name="BirthYear">For a person: a listed birth year more than 2 years away lowers the score.</param>
/// <param name="Country">A country (name or ISO code): a listed party with addresses only elsewhere lowers the score.</param>
public sealed record Query(string Name, PartyType? Type = null, int? BirthYear = null, string? Country = null);

/// <summary>A possible match, with the reasons.</summary>
/// <param name="Entry">The listed party.</param>
/// <param name="MatchedName">Which of its names matched best.</param>
/// <param name="Score">0 to 1: how alike the names are, adjusted for birth year and country.</param>
/// <param name="Reasons">Plain-English reasons, such as "SMITH matches SMYTH (typo)".</param>
public sealed record Hit(ListEntry Entry, ListedName MatchedName, double Score, IReadOnlyList<string> Reasons);

/// <summary>Thrown when a list can't be read. The message says why.</summary>
public sealed class ListFormatException(string message) : Exception(message);

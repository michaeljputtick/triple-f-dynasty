namespace TripleF.Models;

public enum GameType { Regular, Playoff, Consolation }

/// <summary>A manager, keyed by Sleeper user id (stable across dynasty seasons).</summary>
public record Owner(string Id, string Name, string? TeamName, string? Avatar);

/// <summary>One game from one manager's point of view. Every matchup produces two of these.</summary>
public record TeamGame(int Season, int Week, GameType Type, string OwnerId, string OpponentId, double PointsFor, double PointsAgainst)
{
    public double Margin => PointsFor - PointsAgainst;
    public bool Won => Margin > 0;
    public bool Lost => Margin < 0;
    public bool Tied => Margin == 0;
    public string Label => Type == GameType.Playoff ? $"{Season} Wk {Week} (playoffs)" : $"{Season} Wk {Week}";
}

/// <summary>A manager's full roster and per-player points for one week (from the matchups endpoint).</summary>
public record WeekRoster(
    int Season, int Week, string OwnerId, double Points,
    IReadOnlyList<string> Players, IReadOnlySet<string> Starters, IReadOnlyDictionary<string, double> PlayerPoints);

public record PlayerInfo(string Id, string Name, IReadOnlyList<string> Positions, DateOnly? BirthDate = null)
{
    public string Position => Positions.Count > 0 ? Positions[0] : "";

    /// <summary>Age as of September 1 of the given season, or null if the birth date is unknown.</summary>
    public int? AgeIn(int season)
    {
        if (BirthDate is not { } b) return null;
        var kickoff = new DateOnly(season, 9, 1);
        return kickoff.Year - b.Year - (kickoff < b.AddYears(kickoff.Year - b.Year) ? 1 : 0);
    }
}

public record PlayerMove(string PlayerId, string OwnerId);

/// <summary>A draft pick changing hands in a trade. A pick is identified by season, round, and original roster.</summary>
public record TradedPick(int Season, int Round, int OriginalRosterId, string FromOwnerId, string ToOwnerId);

public record FaabMove(string FromOwnerId, string ToOwnerId, int Amount);

public record Transaction(
    int Season, int Leg, string Type, DateTimeOffset Created,
    IReadOnlyList<PlayerMove> Adds, IReadOnlyList<PlayerMove> Drops, IReadOnlyList<string> OwnerIds, int Bid)
{
    public IReadOnlyList<TradedPick> Picks { get; init; } = [];
    public IReadOnlyList<FaabMove> Faab { get; init; } = [];
}

public record DraftPick(int Season, bool IsStartup, int Round, int PickNo, int Rounds, string PlayerId, string OwnerId)
{
    /// <summary>Roster that originally owned this pick (from the draft's slot order).</summary>
    public int? OriginalRosterId { get; init; }
}

/// <summary>Transactions and drafts, loaded only when the Hall of Greatness page needs them.</summary>
public class HallData
{
    public List<Transaction> Transactions { get; } = [];
    public List<DraftPick> Picks { get; } = [];
}

public class SeasonInfo
{
    public int Season { get; init; }
    public string LeagueId { get; init; } = "";
    public string Status { get; init; } = "";
    public int PlayoffWeekStart { get; init; }
    public int WeeksLoaded { get; init; }
    /// <summary>Last transaction "leg" to read. Offseason moves land in leg 1.</summary>
    public int TransactionLegs { get; init; }
    public IReadOnlyList<string> LineupSlots { get; init; } = [];
    public IReadOnlyDictionary<int, string> RosterOwners { get; init; } = new Dictionary<int, string>();
    public bool IsComplete => Status == "complete";
    public string? ChampionId { get; init; }
    public string? RunnerUpId { get; init; }
    public HashSet<string> PlayoffOwnerIds { get; init; } = [];
    /// <summary>Final placement for teams in the winners bracket (1st through 6th).</summary>
    public Dictionary<string, int> FinalPlace { get; init; } = [];
}

public class LeagueData
{
    public string Name { get; init; } = "";
    public Dictionary<string, Owner> Owners { get; } = [];
    public List<SeasonInfo> Seasons { get; } = [];
    public List<TeamGame> TeamGames { get; } = [];
    public List<WeekRoster> Rosters { get; } = [];
    public Dictionary<string, PlayerInfo> Players { get; } = [];

    public Owner OwnerOf(string id) =>
        Owners.TryGetValue(id, out var o) ? o : new Owner(id, "Unknown", null, null);

    public PlayerInfo PlayerOf(string id) =>
        Players.TryGetValue(id, out var p) ? p : new PlayerInfo(id, $"Player {id}", []);

    public string OwnerOfRoster(int season, int rosterId) =>
        Seasons.FirstOrDefault(s => s.Season == season)?.RosterOwners.GetValueOrDefault(rosterId)
        ?? $"roster-{season}-{rosterId}";
}

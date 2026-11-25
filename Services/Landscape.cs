using TripleF.Models;

namespace TripleF.Services;

public record LandscapePlayer(PlayerInfo Player, int Value, double? Age, bool Starter);
public record LandscapePick(int Season, int Round, Owner OriginalOwner, int Value);

public enum LandscapeMode { FullTeamAndPicks, FullTeam, Starters }

/// <summary>One team's current dynasty assets, valued with FantasyCalc.</summary>
public record TeamLandscape(Owner Owner, List<LandscapePlayer> Players, List<LandscapePick> Picks)
{
    public static readonly string[] Positions = ["QB", "RB", "WR", "TE"];

    private IEnumerable<LandscapePlayer> Counted(LandscapeMode mode) =>
        mode == LandscapeMode.Starters ? Players.Where(p => p.Starter) : Players;

    public int PositionValue(string pos, LandscapeMode mode) =>
        Counted(mode).Where(p => p.Player.Position == pos).Sum(p => p.Value);

    public int PickValue => Picks.Sum(p => p.Value);

    public int TotalValue(LandscapeMode mode) =>
        Counted(mode).Sum(p => p.Value) + (mode == LandscapeMode.FullTeamAndPicks ? PickValue : 0);

    /// <summary>Value-weighted average age: young studs pull it down more than young backups.</summary>
    public double? AverageAge(LandscapeMode mode)
    {
        var aged = Counted(mode).Where(p => p.Age is not null && p.Value > 0).ToList();
        var weight = aged.Sum(p => p.Value);
        return weight == 0 ? null : aged.Sum(p => p.Age!.Value * p.Value) / weight;
    }
}

public static class LandscapeBuilder
{
    private static readonly Dictionary<string, string[]> SlotEligibility = new()
    {
        ["QB"] = ["QB"], ["RB"] = ["RB"], ["WR"] = ["WR"], ["TE"] = ["TE"], ["K"] = ["K"], ["DEF"] = ["DEF"],
        ["FLEX"] = ["RB", "WR", "TE"],
        ["WRRB_FLEX"] = ["RB", "WR"],
        ["REC_FLEX"] = ["WR", "TE"],
        ["SUPER_FLEX"] = ["QB", "RB", "WR", "TE"],
    };

    /// <summary>Current rosters plus each team's own and acquired picks for the next three drafts.</summary>
    public static async Task<List<TeamLandscape>> BuildAsync(SleeperClient sleeper, LeagueData d, TradeValueTable values)
    {
        var season = d.Seasons[^1];
        var rostersTask = sleeper.GetRostersAsync(season.LeagueId);
        var tradedTask = sleeper.GetTradedPicksAsync(season.LeagueId);
        var rosters = await rostersTask;
        var traded = await tradedTask;

        var rounds = values.Picks.Keys.Select(k => k.Round).DefaultIfEmpty(0).Max();
        var futureSeasons = Enumerable.Range(season.Season + 1, 3).ToList();

        // Every roster starts owning its own picks; traded_picks lists the ones that moved.
        var pickOwner = new Dictionary<(int Season, int Round, int Original), int>();
        foreach (var s in futureSeasons)
            for (var r = 1; r <= rounds; r++)
                foreach (var roster in rosters)
                    pickOwner[(s, r, roster.RosterId)] = roster.RosterId;
        foreach (var t in traded)
        {
            if (int.TryParse(t.Season, out var s) && pickOwner.ContainsKey((s, t.Round, t.RosterId)))
                pickOwner[(s, t.Round, t.RosterId)] = t.OwnerId;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        return rosters
            .Where(r => !string.IsNullOrEmpty(r.OwnerId))
            .Select(r =>
            {
                var players = (r.Players ?? [])
                    .Select(id => d.PlayerOf(id))
                    .Select(p => (Player: p, Value: values.Player(p.Id), Age: p.BirthDate is { } b ? (today.DayNumber - b.DayNumber) / 365.25 : (double?)null))
                    .ToList();
                var starters = BestLineup(players.Select(p => (p.Player, p.Value)).ToList(), season.LineupSlots);
                var picks = pickOwner
                    .Where(kv => kv.Value == r.RosterId)
                    .Select(kv => new LandscapePick(kv.Key.Season, kv.Key.Round,
                        d.OwnerOf(d.OwnerOfRoster(season.Season, kv.Key.Original)),
                        values.Pick(kv.Key.Season, kv.Key.Round) ?? 0))
                    .OrderBy(p => p.Season).ThenBy(p => p.Round)
                    .ToList();
                return new TeamLandscape(
                    d.OwnerOf(r.OwnerId!),
                    players.Select(p => new LandscapePlayer(p.Player, p.Value, p.Age, starters.Contains(p.Player.Id)))
                        .OrderByDescending(p => p.Value).ToList(),
                    picks);
            })
            .ToList();
    }

    /// <summary>Highest-value starting lineup for the league's slots, filling the most restrictive slots first.</summary>
    private static HashSet<string> BestLineup(List<(PlayerInfo Player, int Value)> pool, IReadOnlyList<string> slots)
    {
        var ordered = pool.OrderByDescending(p => p.Value).ToList();
        var used = new HashSet<string>();
        foreach (var slot in slots.Where(SlotEligibility.ContainsKey).OrderBy(s => SlotEligibility[s].Length))
        {
            var eligible = SlotEligibility[slot];
            var pick = ordered.FirstOrDefault(p => !used.Contains(p.Player.Id) && p.Player.Positions.Any(eligible.Contains));
            if (pick.Player is not null) used.Add(pick.Player.Id);
        }
        return used;
    }
}

using TripleF.Models;

namespace TripleF.Services;

/// <summary>A continuous run of weeks a player spent on one manager's roster.</summary>
public record Stint(string PlayerId, string OwnerId, int StartIdx, int EndIdx, int Starts, double StarterPoints)
{
    public int Weeks => EndIdx - StartIdx + 1;
}

public record Boomerang(PlayerInfo Player, Owner Owner, IReadOnlyList<Stint> Stints);
public record PlayerCount(PlayerInfo Player, int Count);
public record OwnerCount(Owner Owner, int Count);
public record PairCount(Owner A, Owner B, int Count);
public record Journeyman(PlayerInfo Player, IReadOnlyList<Owner> Owners);
public record Loyalty(PlayerInfo Player, Owner Owner, int Starts, double Points);

public record LineupRow(Owner Owner, int Games, double Actual, double Optimal, int Wins, int Losses, int OptimalWins, int OptimalLosses, int LineupLosses, int PerfectWeeks)
{
    public double Efficiency => Optimal == 0 ? 0 : Actual / Optimal;
    public double BenchPoints => Optimal - Actual;
}

public record BenchWeek(TeamGame Game, Owner Owner, double Optimal)
{
    public double Left => Optimal - Game.PointsFor;
}

public record FaabRow(Owner Owner, int Total, int Seasons, int Claims, int BiggestBid)
{
    public double PerSeason => Seasons == 0 ? 0 : (double)Total / Seasons;
}

public record Pickup(Transaction Tx, PlayerInfo Player, Owner Owner, int Starts, double StarterPoints, int StintStart);
public record DrafterRow(Owner Owner, int Picks, double StarterPoints, double LeaguePoints, PickValue? BestPick)
{
    public double PerPick => Picks == 0 ? 0 : StarterPoints / Picks;
}
public record PickValue(DraftPick Pick, Owner Owner, PlayerInfo Player, double StarterPoints, double LeaguePoints);
public record PlayerWeek(PlayerInfo Player, Owner Owner, int Season, int Week, double Points);

/// <summary>Everything on the Hall of Greatness page, computed once from league + transaction data.</summary>
public class HallStats
{
    private static readonly Dictionary<string, string[]> SlotEligibility = new()
    {
        ["QB"] = ["QB"], ["RB"] = ["RB"], ["WR"] = ["WR"], ["TE"] = ["TE"], ["K"] = ["K"], ["DEF"] = ["DEF"],
        ["FLEX"] = ["RB", "WR", "TE"],
        ["WRRB_FLEX"] = ["RB", "WR"],
        ["REC_FLEX"] = ["WR", "TE"],
        ["SUPER_FLEX"] = ["QB", "RB", "WR", "TE"],
    };

    private readonly LeagueData d;
    private readonly HallData h;
    private readonly List<(int Season, int Week)> weekOrder;
    private readonly Dictionary<(int Season, int Week), int> weekIdx;
    private readonly ILookup<(string PlayerId, string OwnerId), Stint> stintsByPair;
    private readonly Dictionary<string, double> rosteredPoints;

    public List<Stint> Stints { get; }
    public List<Boomerang> Boomerangs { get; }
    public List<Stint> LongestTenures { get; }
    public List<Journeyman> Journeymen { get; }
    public List<Loyalty> MostLoyal { get; }

    public List<LineupRow> Lineups { get; }
    public List<BenchWeek> WorstBenchWeeks { get; }
    public List<BenchWeek> CouldaWon { get; }

    public List<PlayerCount> MostTraded { get; }
    public List<PlayerCount> MostCut { get; }
    public List<PlayerCount> MostAdded { get; }
    public List<FaabRow> Faab { get; }
    public List<Pickup> BiggestBids { get; }
    public List<Pickup> BestPickups { get; }
    public List<Pickup> FaabRegrets { get; }
    public List<OwnerCount> MostMoves { get; }
    public List<OwnerCount> MostTrades { get; }
    public List<PairCount> TradePartners { get; }

    public List<PickValue> Picks { get; }

    public List<PlayerWeek> BestStarterWeeks { get; }
    public List<PlayerWeek> BenchBombs { get; }

    public HallStats(LeagueData data, HallData hall)
    {
        d = data;
        h = hall;

        weekOrder = d.Rosters.Select(r => (r.Season, r.Week)).Distinct().OrderBy(w => w.Season).ThenBy(w => w.Week).ToList();
        weekIdx = weekOrder.Select((w, i) => (w, i)).ToDictionary(x => x.w, x => x.i);

        // ---- Roster stints ----
        var membership = new Dictionary<(string, string), List<(int Idx, bool Started, double Pts)>>();
        rosteredPoints = [];
        foreach (var r in d.Rosters)
        {
            var idx = weekIdx[(r.Season, r.Week)];
            foreach (var p in r.Players)
            {
                var pts = r.PlayerPoints.GetValueOrDefault(p);
                var started = r.Starters.Contains(p);
                if (!membership.TryGetValue((p, r.OwnerId), out var list))
                    membership[(p, r.OwnerId)] = list = [];
                list.Add((idx, started, started ? pts : 0));
                rosteredPoints[p] = rosteredPoints.GetValueOrDefault(p) + pts;
            }
        }

        Stints = [];
        foreach (var ((player, owner), weeks) in membership)
        {
            weeks.Sort((a, b) => a.Idx.CompareTo(b.Idx));
            var start = 0;
            for (var i = 1; i <= weeks.Count; i++)
            {
                if (i < weeks.Count && weeks[i].Idx == weeks[i - 1].Idx + 1) continue;
                var run = weeks.Skip(start).Take(i - start).ToList();
                Stints.Add(new Stint(player, owner, run[0].Idx, run[^1].Idx, run.Count(w => w.Started), run.Sum(w => w.Pts)));
                start = i;
            }
        }
        stintsByPair = Stints.ToLookup(s => (s.PlayerId, s.OwnerId));

        Boomerangs = stintsByPair
            .Where(g => g.Count() >= 2)
            .Select(g => new Boomerang(d.PlayerOf(g.Key.PlayerId), d.OwnerOf(g.Key.OwnerId), g.OrderBy(s => s.StartIdx).ToList()))
            .OrderByDescending(b => b.Stints.Count)
            .ThenByDescending(b => b.Stints.Sum(s => s.Weeks))
            .Take(10).ToList();

        LongestTenures = Stints.OrderByDescending(s => s.Weeks).ThenByDescending(s => s.StarterPoints).Take(10).ToList();

        Journeymen = Stints
            .GroupBy(s => s.PlayerId)
            .Select(g => new Journeyman(d.PlayerOf(g.Key),
                g.OrderBy(s => s.StartIdx).Select(s => s.OwnerId).Distinct().Select(d.OwnerOf).ToList()))
            .OrderByDescending(j => j.Owners.Count)
            .Take(10).ToList();

        MostLoyal = stintsByPair
            .Select(g => new Loyalty(d.PlayerOf(g.Key.PlayerId), d.OwnerOf(g.Key.OwnerId), g.Sum(s => s.Starts), g.Sum(s => s.StarterPoints)))
            .OrderByDescending(l => l.Starts).ThenByDescending(l => l.Points)
            .Take(10).ToList();

        // ---- Lineup decisions ----
        var slotsBySeason = d.Seasons.ToDictionary(s => s.Season, s => s.LineupSlots);
        var optimal = d.Rosters.ToDictionary(
            r => (r.Season, r.Week, r.OwnerId),
            r => OptimalPoints(r, slotsBySeason.GetValueOrDefault(r.Season) ?? []));

        var scored = LeagueStats.Meaningful(d)
            .Where(g => optimal.ContainsKey((g.Season, g.Week, g.OwnerId)))
            .Select(g => (Game: g, Optimal: optimal[(g.Season, g.Week, g.OwnerId)]))
            .ToList();

        Lineups = scored
            .GroupBy(x => x.Game.OwnerId)
            .Select(g => new LineupRow(
                d.OwnerOf(g.Key),
                Games: g.Count(),
                Actual: g.Sum(x => x.Game.PointsFor),
                Optimal: g.Sum(x => x.Optimal),
                Wins: g.Count(x => x.Game.Won),
                Losses: g.Count(x => x.Game.Lost),
                OptimalWins: g.Count(x => x.Optimal > x.Game.PointsAgainst),
                OptimalLosses: g.Count(x => x.Optimal < x.Game.PointsAgainst),
                LineupLosses: g.Count(x => x.Game.Lost && x.Optimal > x.Game.PointsAgainst),
                PerfectWeeks: g.Count(x => x.Optimal - x.Game.PointsFor < 0.01)))
            .OrderByDescending(r => r.Efficiency)
            .ToList();

        var benchWeeks = scored.Select(x => new BenchWeek(x.Game, d.OwnerOf(x.Game.OwnerId), x.Optimal)).ToList();
        WorstBenchWeeks = benchWeeks.OrderByDescending(b => b.Left).Take(10).ToList();
        CouldaWon = benchWeeks
            .Where(b => b.Game.Lost && b.Optimal > b.Game.PointsAgainst)
            .OrderByDescending(b => b.Optimal - b.Game.PointsAgainst)
            .Take(10).ToList();

        // ---- Transactions ----
        var trades = h.Transactions.Where(t => t.Type == "trade").ToList();
        var pickups = h.Transactions.Where(t => t.Type is "waiver" or "free_agent").ToList();

        MostTraded = CountPlayers(trades.SelectMany(t => t.Adds.Select(a => a.PlayerId)));
        MostCut = CountPlayers(h.Transactions.Where(t => t.Type != "trade").SelectMany(t => t.Drops.Select(x => x.PlayerId)));
        MostAdded = CountPlayers(pickups.SelectMany(t => t.Adds.Select(a => a.PlayerId)));

        var activeOwners = d.TeamGames.Select(g => g.OwnerId).Distinct().ToList();
        Faab = activeOwners
            .Select(id =>
            {
                var claims = pickups.Where(t => t.Type == "waiver" && t.Adds.Any(a => a.OwnerId == id)).ToList();
                return new FaabRow(d.OwnerOf(id),
                    Total: claims.Sum(t => t.Bid),
                    Seasons: d.TeamGames.Where(g => g.OwnerId == id).Select(g => g.Season).Distinct().Count(),
                    Claims: claims.Count,
                    BiggestBid: claims.Count == 0 ? 0 : claims.Max(t => t.Bid));
            })
            .OrderByDescending(f => f.Total)
            .ToList();

        var pickupValues = pickups
            .SelectMany(t => t.Adds.Select(a => PickupValue(t, a)))
            .ToList();
        BiggestBids = pickupValues.Where(p => p.Tx.Bid > 0).OrderByDescending(p => p.Tx.Bid).Take(10).ToList();
        var distinctPickups = OnePerStint(pickupValues);
        BestPickups = distinctPickups.OrderByDescending(p => p.StarterPoints).Take(10).ToList();
        FaabRegrets = distinctPickups
            .Where(p => p.Tx.Bid >= 10)
            .OrderBy(p => p.StarterPoints / p.Tx.Bid)
            .ThenByDescending(p => p.Tx.Bid)
            .Take(10).ToList();

        MostMoves = CountOwners(pickups.SelectMany(t => t.Adds.Select(a => a.OwnerId)));
        MostTrades = CountOwners(trades.SelectMany(t => t.OwnerIds));
        TradePartners = trades
            .SelectMany(t => t.OwnerIds.SelectMany(a => t.OwnerIds.Where(b => string.CompareOrdinal(a, b) < 0).Select(b => (a, b))))
            .GroupBy(p => p)
            .Select(g => new PairCount(d.OwnerOf(g.Key.a), d.OwnerOf(g.Key.b), g.Count()))
            .OrderByDescending(p => p.Count)
            .Take(10).ToList();

        // ---- Drafts ----
        Picks = h.Picks
            .Select(p => new PickValue(p, d.OwnerOf(p.OwnerId), d.PlayerOf(p.PlayerId),
                StarterPoints: stintsByPair[(p.PlayerId, p.OwnerId)].Sum(s => s.StarterPoints),
                LeaguePoints: rosteredPoints.GetValueOrDefault(p.PlayerId)))
            .ToList();

        // ---- Single-player weeks ----
        var playerWeeks = d.Rosters
            .SelectMany(r => r.PlayerPoints.Select(kv => (Roster: r, Player: kv.Key, Points: kv.Value)))
            .ToList();
        BestStarterWeeks = playerWeeks.Where(x => x.Roster.Starters.Contains(x.Player))
            .OrderByDescending(x => x.Points).Take(10)
            .Select(x => new PlayerWeek(d.PlayerOf(x.Player), d.OwnerOf(x.Roster.OwnerId), x.Roster.Season, x.Roster.Week, x.Points))
            .ToList();
        BenchBombs = playerWeeks.Where(x => !x.Roster.Starters.Contains(x.Player))
            .OrderByDescending(x => x.Points).Take(10)
            .Select(x => new PlayerWeek(d.PlayerOf(x.Player), d.OwnerOf(x.Roster.OwnerId), x.Roster.Season, x.Roster.Week, x.Points))
            .ToList();
    }

    public List<DrafterRow> Drafters(Func<DraftPick, bool> filter) =>
        Picks.Where(p => filter(p.Pick))
            .GroupBy(p => p.Pick.OwnerId)
            .Select(g => new DrafterRow(d.OwnerOf(g.Key), g.Count(), g.Sum(p => p.StarterPoints), g.Sum(p => p.LeaguePoints),
                g.MaxBy(p => p.StarterPoints)))
            .OrderByDescending(r => r.StarterPoints)
            .ToList();

    /// <summary>Late-half picks that produced the most for the league.</summary>
    public List<PickValue> Steals(Func<DraftPick, bool> filter) =>
        Picks.Where(p => filter(p.Pick) && p.Pick.Round > p.Pick.Rounds / 2)
            .OrderByDescending(p => p.LeaguePoints).Take(10).ToList();

    /// <summary>First-round picks that produced the least (skips the newest draft, too early to judge).</summary>
    public List<PickValue> Busts(Func<DraftPick, bool> filter)
    {
        var newest = h.Picks.Count == 0 ? 0 : h.Picks.Max(p => p.Season);
        return Picks.Where(p => filter(p.Pick) && p.Pick.Round == 1 && p.Pick.Season < newest)
            .OrderBy(p => p.LeaguePoints).Take(10).ToList();
    }

    public string StintLabel(Stint s)
    {
        var (s1, w1) = weekOrder[s.StartIdx];
        var (s2, w2) = weekOrder[s.EndIdx];
        if (s1 != s2) return $"{s1} Wk {w1} to {s2} Wk {w2}";
        return w1 == w2 ? $"{s1} Wk {w1}" : $"{s1} Wk {w1}-{w2}";
    }

    private double OptimalPoints(WeekRoster r, IReadOnlyList<string> slots)
    {
        var pool = r.Players
            .Select(p => (Id: p, Pts: r.PlayerPoints.GetValueOrDefault(p), Pos: d.PlayerOf(p).Positions))
            .OrderByDescending(p => p.Pts)
            .ToList();
        var used = new HashSet<string>();
        double total = 0;

        // Fill the most restrictive slots first so flex spots get whoever is left.
        foreach (var slot in slots.Where(SlotEligibility.ContainsKey).OrderBy(s => SlotEligibility[s].Length))
        {
            var eligible = SlotEligibility[slot];
            var pick = pool.FirstOrDefault(p => !used.Contains(p.Id) && p.Pos.Any(eligible.Contains));
            if (pick.Id is null) continue;
            used.Add(pick.Id);
            total += pick.Pts;
        }
        // Unknown slot types or missing positions can undercount; the real lineup is a floor.
        return Math.Max(total, r.Points);
    }

    /// <summary>Starts and starter points from the roster stint that begins with a transaction.</summary>
    public (int Starts, double Points) AfterMove(int season, int leg, string playerId, string ownerId)
    {
        var stint = StintAfter(season, leg, playerId, ownerId);
        return (stint?.Starts ?? 0, stint?.StarterPoints ?? 0);
    }

    private Stint? StintAfter(int season, int leg, string playerId, string ownerId)
    {
        // First loaded week at or after the transaction; the player shows up that week or the next.
        var idx = weekOrder.FindIndex(w => w.Season > season || (w.Season == season && w.Week >= leg));
        return idx < 0 ? null : stintsByPair[(playerId, ownerId)]
            .FirstOrDefault(s => s.EndIdx >= idx && s.StartIdx <= idx + 1);
    }

    /// <summary>Every starter point a player ever scored for a manager, across all stints.</summary>
    public (int Starts, double Points) CareerWith(string playerId, string ownerId)
    {
        var stints = stintsByPair[(playerId, ownerId)].ToList();
        return (stints.Sum(s => s.Starts), stints.Sum(s => s.StarterPoints));
    }

    private Pickup PickupValue(Transaction t, PlayerMove add)
    {
        var stint = StintAfter(t.Season, t.Leg, add.PlayerId, add.OwnerId);
        return new Pickup(t, d.PlayerOf(add.PlayerId), d.OwnerOf(add.OwnerId), stint?.Starts ?? 0, stint?.StarterPoints ?? 0, stint?.StartIdx ?? -1);
    }

    /// <summary>
    /// Several adds can lead into the same roster stint (e.g. add, drop, re-add before week 1).
    /// Keep one per stint: the biggest bid, then the latest move.
    /// </summary>
    private static List<Pickup> OnePerStint(IEnumerable<Pickup> pickups) =>
        pickups
            .GroupBy(p => p.StintStart < 0 ? $"none:{p.Tx.Created.Ticks}:{p.Player.Id}" : $"{p.Player.Id}:{p.Owner.Id}:{p.StintStart}")
            .Select(g => g.OrderByDescending(p => p.Tx.Bid).ThenByDescending(p => p.Tx.Created).First())
            .ToList();

    private List<PlayerCount> CountPlayers(IEnumerable<string> ids) =>
        ids.GroupBy(id => id)
            .Select(g => new PlayerCount(d.PlayerOf(g.Key), g.Count()))
            .OrderByDescending(p => p.Count)
            .Take(10).ToList();

    private List<OwnerCount> CountOwners(IEnumerable<string> ids) =>
        ids.GroupBy(id => id)
            .Select(g => new OwnerCount(d.OwnerOf(g.Key), g.Count()))
            .OrderByDescending(o => o.Count)
            .ToList();
}

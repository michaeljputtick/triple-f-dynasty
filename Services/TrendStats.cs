using TripleF.Models;

namespace TripleF.Services;

public record AssetValue(PlayerInfo Player, int? Age, double Ppg, double Value);
public record TeamValueSnapshot(Owner Owner, int Season, double Raw, double Index, IReadOnlyList<AssetValue> Assets);
public record PositionLeader(PlayerInfo Player, double Points, int Starts);

/// <summary>
/// Year-over-year numbers per manager: points per game, finish, and a dynasty team value.
///
/// Team value, per season, from each team's roster in the season's last loaded week:
///   1. Production: each player's points per game that season (weeks with nonzero points).
///      Players with under 6 games blend in the prior season so an injury doesn't zero them out.
///   2. Replacement level: the PPG of the first player past the startable pool at his position
///      (teams x lineup slots, with flex spots split RB 45% / WR 45% / TE 10%).
///   3. Player value = (PPG over replacement + prospect bonus for age 24 and under)
///      x position age curve (RBs fade fast, QBs slow) x 0.15 for K/DEF.
///   4. Team value = players sorted by value, weighted 1.0 for a starting lineup's worth,
///      0.5 for the next 6, 0.15 for the rest. Shown as an index where the league average is 100.
/// </summary>
public class TrendStats
{
    private static readonly string[] PositionOrder = ["QB", "RB", "WR", "TE", "K", "DEF"];

    private readonly LeagueData d;

    public List<int> Seasons { get; }
    public List<Owner> Managers { get; }
    public HashSet<int> InProgress { get; }
    public Dictionary<(string Owner, int Season), double> Ppg { get; } = [];
    public Dictionary<(string Owner, int Season), int> Finish { get; } = [];
    public Dictionary<(string Owner, int Season), TeamValueSnapshot> Value { get; } = [];

    public TrendStats(LeagueData data)
    {
        d = data;
        Seasons = d.Seasons.Where(s => s.WeeksLoaded > 0).Select(s => s.Season).ToList();
        InProgress = d.Seasons.Where(s => !s.IsComplete).Select(s => s.Season).ToHashSet();
        Managers = LeagueStats.AllTime(d).Select(r => r.Owner).ToList();

        foreach (var g in d.TeamGames.Where(g => g.Type == GameType.Regular).GroupBy(g => (g.OwnerId, g.Season)))
            Ppg[g.Key] = g.Average(x => x.PointsFor);

        foreach (var s in d.Seasons)
            foreach (var row in LeagueStats.SeasonStandings(d, s))
                Finish[(row.Owner.Id, s.Season)] = row.Finish ?? row.Seed;

        foreach (var s in d.Seasons.Where(s => s.WeeksLoaded > 0))
            foreach (var snap in TeamValues(s))
                Value[(snap.Owner.Id, s.Season)] = snap;
    }

    public double? AverageFinish(string ownerId)
    {
        var finishes = Seasons.Where(s => !InProgress.Contains(s))
            .Select(s => Finish.TryGetValue((ownerId, s), out var f) ? f : (int?)null)
            .OfType<int>().ToList();
        return finishes.Count == 0 ? null : finishes.Average();
    }

    /// <summary>A manager's top 3 starters by points at each position, all-time or for one season.</summary>
    public List<(string Position, List<PositionLeader> Leaders)> PositionLeaders(string ownerId, int? season)
    {
        var starts = d.Rosters
            .Where(r => r.OwnerId == ownerId && (season is null || r.Season == season))
            .SelectMany(r => r.Starters.Select(p => (Player: p, Points: r.PlayerPoints.GetValueOrDefault(p))))
            .GroupBy(x => x.Player)
            .Select(g => new PositionLeader(d.PlayerOf(g.Key), g.Sum(x => x.Points), g.Count()))
            .ToList();

        return PositionOrder
            .Select(pos => (pos, starts.Where(s => s.Player.Position == pos).OrderByDescending(s => s.Points).Take(3).ToList()))
            .Where(x => x.Item2.Count > 0)
            .ToList();
    }

    private List<TeamValueSnapshot> TeamValues(SeasonInfo s)
    {
        var current = Production(s.Season);
        var prior = Production(s.Season - 1);

        double PpgOf(string p)
        {
            var (sum, games) = current.GetValueOrDefault(p);
            if (games < 6 && prior.TryGetValue(p, out var prev))
                (sum, games) = (sum + prev.Sum, games + prev.Games);
            return games == 0 ? 0 : sum / games;
        }

        var lastWeek = d.Rosters.Where(r => r.Season == s.Season).Max(r => r.Week);
        var finalRosters = d.Rosters.Where(r => r.Season == s.Season && r.Week == lastWeek).ToList();
        var teams = finalRosters.Count;

        // Replacement level by position: PPG of the first player outside the startable pool.
        var shares = StarterShares(s.LineupSlots);
        var replacement = new Dictionary<string, double>();
        foreach (var pos in PositionOrder)
        {
            var pool = current.Where(kv => kv.Value.Games >= 4 && d.PlayerOf(kv.Key).Position == pos)
                .Select(kv => kv.Value.Sum / kv.Value.Games)
                .OrderByDescending(x => x).ToList();
            var n = (int)Math.Round(teams * shares.GetValueOrDefault(pos));
            replacement[pos] = pool.Count == 0 ? 0 : pool[Math.Min(n, pool.Count - 1)];
        }

        var starterCount = s.LineupSlots.Count;
        var snaps = finalRosters.Select(r =>
        {
            var assets = r.Players.Select(p =>
                {
                    var info = d.PlayerOf(p);
                    var age = info.AgeIn(s.Season);
                    var ppg = PpgOf(p);
                    var over = Math.Max(0, ppg - replacement.GetValueOrDefault(info.Position));
                    var prospect = age switch { <= 22 => 3.0, 23 => 2.0, 24 => 1.0, _ => 0.0 };
                    var posMult = info.Position is "K" or "DEF" ? 0.15 : 1.0;
                    return new AssetValue(info, age, ppg, (over + prospect) * AgeCurve(info.Position, age) * posMult);
                })
                .OrderByDescending(a => a.Value)
                .ToList();

            var raw = assets.Select((a, i) => a.Value * (i < starterCount ? 1.0 : i < starterCount + 6 ? 0.5 : 0.15)).Sum();
            return (Owner: d.OwnerOf(r.OwnerId), Raw: raw, Assets: assets);
        }).ToList();

        var avg = snaps.Count == 0 ? 1 : Math.Max(0.001, snaps.Average(x => x.Raw));
        return snaps.Select(x => new TeamValueSnapshot(x.Owner, s.Season, x.Raw, x.Raw / avg * 100, x.Assets.Take(5).ToList())).ToList();
    }

    /// <summary>Points and games played (nonzero weeks) per player, across every roster in a season.</summary>
    private Dictionary<string, (double Sum, int Games)> Production(int season) =>
        d.Rosters.Where(r => r.Season == season)
            .SelectMany(r => r.Players.Select(p => (Player: p, Points: r.PlayerPoints.GetValueOrDefault(p))))
            .Where(x => x.Points != 0)
            .GroupBy(x => x.Player)
            .ToDictionary(g => g.Key, g => (g.Sum(x => x.Points), g.Count()));

    private static Dictionary<string, double> StarterShares(IReadOnlyList<string> slots)
    {
        var shares = new Dictionary<string, double>();
        void Add(string pos, double n) => shares[pos] = shares.GetValueOrDefault(pos) + n;
        foreach (var slot in slots)
        {
            switch (slot)
            {
                case "FLEX": Add("RB", .45); Add("WR", .45); Add("TE", .10); break;
                case "WRRB_FLEX": Add("RB", .5); Add("WR", .5); break;
                case "REC_FLEX": Add("WR", .8); Add("TE", .2); break;
                case "SUPER_FLEX": Add("QB", .8); Add("RB", .1); Add("WR", .1); break;
                default: Add(slot, 1); break;
            }
        }
        return shares;
    }

    private static double AgeCurve(string pos, int? age)
    {
        if (age is not { } a) return 1.0;
        return pos switch
        {
            "RB" => a switch { <= 23 => 1.25, 24 => 1.15, 25 => 1.05, 26 => 0.95, 27 => 0.8, 28 => 0.65, 29 => 0.5, _ => 0.35 },
            "WR" => a switch { <= 23 => 1.2, 24 => 1.15, 25 => 1.1, 26 => 1.05, 27 => 1.0, 28 => 0.9, 29 => 0.8, 30 => 0.65, _ => 0.5 },
            "TE" => a switch { <= 24 => 1.15, <= 27 => 1.1, 28 => 1.0, 29 => 0.9, 30 => 0.75, _ => 0.55 },
            "QB" => a switch { <= 25 => 1.15, <= 30 => 1.1, <= 33 => 1.0, 34 => 0.85, 35 => 0.75, _ => 0.55 },
            _ => 1.0,
        };
    }
}

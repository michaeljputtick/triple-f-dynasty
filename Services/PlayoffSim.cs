using TripleF.Models;

namespace TripleF.Services;

public record SimTeam(Owner Owner, int RosterId, int Wins, int Losses, int Ties, double PointsFor, double Ppg, double StdDev, int RemainingGames)
{
    /// <summary>Points-for tiebreaker once the season ends: current PF plus season average for each remaining game.</summary>
    public double ProjectedPointsFor => PointsFor + Ppg * RemainingGames;
}

/// <summary>A remaining regular-season game between two teams (indexes into the team list).</summary>
public record SimGame(int Week, int A, int B)
{
    public string Key => $"{Week}:{A}:{B}";
}

public record SimOutcome(double[] Playoff, double[] Bye, int[] BestSeed, int[] WorstSeed, double[] ExpectedWins, long Scenarios, bool Exact);

public class PlayoffSetup
{
    public SeasonInfo Season { get; init; } = default!;
    public List<SimTeam> Teams { get; init; } = [];
    public List<SimGame> Games { get; init; } = [];
    public int PlayoffTeams { get; init; }
    public int Byes { get; init; }
    public int FirstWeek { get; init; }
    public int LastWeek { get; init; }
}

public static class PlayoffSim
{
    private const int ExactLimit = 18;      // 2^18 = 262k scenarios; above this, sample instead
    private const int Samples = 40_000;

    /// <summary>Standings, remaining schedule, and playoff format for the current season, or null if the regular season is over.</summary>
    public static async Task<PlayoffSetup?> LoadAsync(SleeperClient sleeper, LeagueData d)
    {
        var season = d.Seasons[^1];
        if (season.Status != "in_season") return null;
        var league = await sleeper.GetLeagueAsync(season.LeagueId);
        if (league is null) return null;

        var s = league.Settings;
        var lastScored = s.LastScoredLeg ?? 0;
        var firstWeek = lastScored + 1;
        var lastWeek = s.PlayoffWeekStart - 1;
        if (firstWeek > lastWeek) return null;

        var owners = season.RosterOwners.OrderBy(kv => kv.Key).ToList();
        var index = owners.Select((kv, i) => (kv.Key, i)).ToDictionary(x => x.Key, x => x.i);

        var schedule = await Task.WhenAll(Enumerable.Range(firstWeek, lastWeek - firstWeek + 1)
            .Select(async w => (Week: w, Matchups: await sleeper.GetMatchupsAsync(season.LeagueId, w))));
        var games = schedule
            .SelectMany(x => x.Matchups.Where(m => m.MatchupId is not null).GroupBy(m => m.MatchupId)
                .Where(g => g.Count() == 2)
                .Select(g => new SimGame(x.Week, index[g.First().RosterId], index[g.Last().RosterId])))
            .ToList();

        var teams = owners.Select(kv =>
        {
            var played = d.TeamGames.Where(g => g.Season == season.Season && g.Type == GameType.Regular && g.OwnerId == kv.Value).ToList();
            var pts = played.Select(g => g.PointsFor).ToList();
            var ppg = pts.Count == 0 ? 0 : pts.Average();
            var sd = pts.Count < 2 ? 25 : Math.Sqrt(pts.Sum(p => (p - ppg) * (p - ppg)) / (pts.Count - 1));
            var i = index[kv.Key];
            return new SimTeam(d.OwnerOf(kv.Value), kv.Key, played.Count(g => g.Won), played.Count(g => g.Lost), played.Count(g => g.Tied),
                pts.Sum(), ppg, sd, games.Count(g => g.A == i || g.B == i));
        }).ToList();

        var playoffTeams = Math.Min(s.PlayoffTeams, teams.Count);
        var bracket = 1 << (int)Math.Ceiling(Math.Log2(Math.Max(2, playoffTeams)));
        return new PlayoffSetup
        {
            Season = season,
            Teams = teams,
            Games = games,
            PlayoffTeams = playoffTeams,
            Byes = bracket - playoffTeams,
            FirstWeek = firstWeek,
            LastWeek = lastWeek,
        };
    }

    /// <summary>Chance team A beats team B: normal model on the score difference using each team's season mean and spread.</summary>
    public static double WinProbability(SimTeam a, SimTeam b)
    {
        var sd = Math.Sqrt(a.StdDev * a.StdDev + b.StdDev * b.StdDev);
        return sd <= 0 ? 0.5 : NormalCdf((a.Ppg - b.Ppg) / sd);
    }

    /// <summary>
    /// Playoff and bye odds given picked results (game key -> winning team index). Unpicked games are
    /// enumerated exactly when there are few enough, otherwise sampled.
    /// </summary>
    public static SimOutcome Run(PlayoffSetup setup, IReadOnlyDictionary<string, int> picks, bool weighted)
    {
        var teams = setup.Teams;
        var n = teams.Count;
        var baseWins = teams.Select(t => t.Wins + 0.5 * t.Ties).ToArray();
        foreach (var g in setup.Games)
            if (picks.TryGetValue(g.Key, out var winner)) baseWins[winner] += 1;

        var open = setup.Games.Where(g => !picks.ContainsKey(g.Key)).ToList();
        var pA = open.Select(g => weighted ? WinProbability(teams[g.A], teams[g.B]) : 0.5).ToArray();

        // The tiebreak (projected points for) never changes, so fold it into one integer sort key:
        // key = half-wins * n + tiebreak rank. Runs interpreted in the browser, so the hot loop avoids
        // delegates and LINQ; this is called up to 2^18 times per click.
        var tieRank = new int[n];
        var byPf = Enumerable.Range(0, n).OrderBy(i => teams[i].ProjectedPointsFor).ToArray();
        for (var r = 0; r < n; r++) tieRank[byPf[r]] = r;

        var playoff = new double[n];
        var bye = new double[n];
        var expected = new double[n];
        var best = Enumerable.Repeat(int.MaxValue, n).ToArray();
        var worst = new int[n];
        var wins = new double[n];
        var key = new int[n];
        var order = new int[n];
        double totalWeight = 0;
        int playoffTeams = setup.PlayoffTeams, byes = setup.Byes;

        void Score(double weight)
        {
            for (var t = 0; t < n; t++)
            {
                key[t] = (int)(wins[t] * 2) * n + tieRank[t];
                // Insertion sort, best key first.
                var j = t;
                while (j > 0 && key[order[j - 1]] < key[t]) { order[j] = order[j - 1]; j--; }
                order[j] = t;
            }
            for (var seed = 0; seed < n; seed++)
            {
                var t = order[seed];
                if (seed < playoffTeams) playoff[t] += weight;
                if (seed < byes) bye[t] += weight;
                expected[t] += wins[t] * weight;
                if (weight > 0)
                {
                    if (seed + 1 < best[t]) best[t] = seed + 1;
                    if (seed + 1 > worst[t]) worst[t] = seed + 1;
                }
            }
            totalWeight += weight;
        }

        var exact = open.Count <= ExactLimit;
        long scenarios;
        if (exact)
        {
            scenarios = 1L << open.Count;
            for (long mask = 0; mask < scenarios; mask++)
            {
                Array.Copy(baseWins, wins, n);
                double weight = 1;
                for (var i = 0; i < open.Count; i++)
                {
                    var aWins = (mask >> i & 1) == 0;
                    wins[aWins ? open[i].A : open[i].B] += 1;
                    weight *= aWins ? pA[i] : 1 - pA[i];
                }
                Score(weight);
            }
        }
        else
        {
            scenarios = Samples;
            var rng = new Random(12345);
            for (var s = 0; s < Samples; s++)
            {
                Array.Copy(baseWins, wins, n);
                for (var i = 0; i < open.Count; i++)
                    wins[rng.NextDouble() < pA[i] ? open[i].A : open[i].B] += 1;
                Score(1);
            }
        }

        for (var t = 0; t < n; t++)
        {
            playoff[t] /= totalWeight;
            bye[t] /= totalWeight;
            expected[t] /= totalWeight;
        }
        return new SimOutcome(playoff, bye, best, worst, expected, scenarios, exact);
    }

    // Abramowitz-Stegun approximation of the standard normal CDF (error < 1e-7).
    private static double NormalCdf(double z)
    {
        var t = 1 / (1 + 0.2316419 * Math.Abs(z));
        var poly = t * (0.319381530 + t * (-0.356563782 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
        var p = 1 - Math.Exp(-z * z / 2) / Math.Sqrt(2 * Math.PI) * poly;
        return z >= 0 ? p : 1 - p;
    }
}

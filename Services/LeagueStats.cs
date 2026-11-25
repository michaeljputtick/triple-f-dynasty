using TripleF.Models;

namespace TripleF.Services;

public record StandingRow(
    Owner Owner, int Seasons, int Wins, int Losses, int Ties, double PointsFor, double PointsAgainst,
    int Titles, int RunnerUps, int PlayoffApps, int PlayoffWins, int PlayoffLosses, int TopScoreWeeks)
{
    public int Games => Wins + Losses + Ties;
    public double WinPct => Games == 0 ? 0 : (Wins + 0.5 * Ties) / Games;
    public double PointsPerGame => Games == 0 ? 0 : PointsFor / Games;
}

public record SeasonRow(Owner Owner, int Seed, int Wins, int Losses, int Ties, double PointsFor, double PointsAgainst, int? Finish, bool MadePlayoffs)
{
    public int Games => Wins + Losses + Ties;
}

public record SeasonTotal(int Season, Owner Owner, double PointsFor, int Wins, int Losses, int Ties, bool InProgress);

public record Streak(Owner Owner, int Length, TeamGame First, TeamGame Last);

public record H2HRecord(int Wins, int Losses, int Ties)
{
    public int Games => Wins + Losses + Ties;
    public double WinPct => Games == 0 ? 0 : (Wins + 0.5 * Ties) / Games;
}

public static class LeagueStats
{
    /// <summary>Regular season plus playoff games. Consolation games are left out of records and H2H.</summary>
    public static IEnumerable<TeamGame> Meaningful(LeagueData d) =>
        d.TeamGames.Where(g => g.Type != GameType.Consolation);

    public static List<StandingRow> AllTime(LeagueData d)
    {
        var topWeeks = d.TeamGames
            .Where(g => g.Type == GameType.Regular)
            .GroupBy(g => (g.Season, g.Week))
            .Select(w => w.MaxBy(g => g.PointsFor)!.OwnerId)
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        return d.TeamGames
            .GroupBy(g => g.OwnerId)
            .Select(grp =>
            {
                var id = grp.Key;
                var reg = grp.Where(g => g.Type == GameType.Regular).ToList();
                var po = grp.Where(g => g.Type == GameType.Playoff).ToList();
                return new StandingRow(
                    d.OwnerOf(id),
                    Seasons: grp.Select(g => g.Season).Distinct().Count(),
                    Wins: reg.Count(g => g.Won),
                    Losses: reg.Count(g => g.Lost),
                    Ties: reg.Count(g => g.Tied),
                    PointsFor: reg.Sum(g => g.PointsFor),
                    PointsAgainst: reg.Sum(g => g.PointsAgainst),
                    Titles: d.Seasons.Count(s => s.ChampionId == id),
                    RunnerUps: d.Seasons.Count(s => s.RunnerUpId == id),
                    PlayoffApps: d.Seasons.Count(s => s.PlayoffOwnerIds.Contains(id)),
                    PlayoffWins: po.Count(g => g.Won),
                    PlayoffLosses: po.Count(g => g.Lost),
                    TopScoreWeeks: topWeeks.GetValueOrDefault(id));
            })
            .OrderByDescending(r => r.Titles)
            .ThenByDescending(r => r.WinPct)
            .ThenByDescending(r => r.PointsFor)
            .ToList();
    }

    public static List<SeasonRow> SeasonStandings(LeagueData d, SeasonInfo s)
    {
        var rows = d.TeamGames
            .Where(g => g.Season == s.Season && g.Type == GameType.Regular)
            .GroupBy(g => g.OwnerId)
            .Select(grp => new
            {
                Id = grp.Key,
                W = grp.Count(g => g.Won),
                L = grp.Count(g => g.Lost),
                T = grp.Count(g => g.Tied),
                PF = grp.Sum(g => g.PointsFor),
                PA = grp.Sum(g => g.PointsAgainst),
            })
            .OrderByDescending(r => r.W + 0.5 * r.T)
            .ThenByDescending(r => r.PF)
            .ToList();

        return rows.Select((r, i) => new SeasonRow(
                d.OwnerOf(r.Id), i + 1, r.W, r.L, r.T, r.PF, r.PA,
                s.FinalPlace.TryGetValue(r.Id, out var p) ? p : null,
                s.PlayoffOwnerIds.Contains(r.Id)))
            .ToList();
    }

    public static List<SeasonTotal> SeasonTotals(LeagueData d) =>
        d.Seasons.SelectMany(s => SeasonStandings(d, s)
                .Select(r => new SeasonTotal(s.Season, r.Owner, r.PointsFor, r.Wins, r.Losses, r.Ties, !s.IsComplete)))
            .ToList();

    public static List<Streak> Streaks(LeagueData d, Func<TeamGame, bool> predicate)
    {
        var result = new List<Streak>();
        foreach (var grp in Meaningful(d).GroupBy(g => g.OwnerId))
        {
            var games = grp.OrderBy(g => g.Season).ThenBy(g => g.Week).ToList();
            for (var i = 0; i < games.Count;)
            {
                if (!predicate(games[i])) { i++; continue; }
                var j = i;
                while (j + 1 < games.Count && predicate(games[j + 1])) j++;
                result.Add(new Streak(d.OwnerOf(grp.Key), j - i + 1, games[i], games[j]));
                i = j + 1;
            }
        }
        return result.OrderByDescending(s => s.Length).ToList();
    }

    public static H2HRecord HeadToHead(LeagueData d, string ownerId, string opponentId)
    {
        var games = Meaningful(d).Where(g => g.OwnerId == ownerId && g.OpponentId == opponentId).ToList();
        return new H2HRecord(games.Count(g => g.Won), games.Count(g => g.Lost), games.Count(g => g.Tied));
    }

    /// <summary>Weekly high scorer for a season's regular-season weeks.</summary>
    public static List<TeamGame> WeeklyHighs(LeagueData d, int season) =>
        d.TeamGames
            .Where(g => g.Season == season && g.Type == GameType.Regular)
            .GroupBy(g => g.Week)
            .OrderBy(g => g.Key)
            .Select(w => w.MaxBy(g => g.PointsFor)!)
            .ToList();
}

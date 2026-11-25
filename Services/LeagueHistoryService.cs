using TripleF.Models;

namespace TripleF.Services;

/// <summary>
/// Walks the league's previous_league_id chain and loads every season's games.
/// Loaded once per page session and shared by all pages.
/// </summary>
public class LeagueHistoryService(SleeperClient sleeper, PlayerDirectory playerDirectory, IConfiguration config)
{
    private Task<LeagueData>? _load;
    private Task<HallData>? _hall;

    public Task<LeagueData> LoadAsync()
    {
        if (_load is null || _load.IsFaulted)
            _load = LoadCoreAsync();
        return _load;
    }

    /// <summary>Transactions and drafts for every season. About 20 extra calls per season, so only loaded on demand.</summary>
    public Task<HallData> LoadHallAsync()
    {
        if (_hall is null || _hall.IsFaulted)
            _hall = LoadHallCoreAsync();
        return _hall;
    }

    private HallStats? _hallStats;
    private TradeHistory? _trades;

    public async Task<HallStats> LoadHallStatsAsync()
    {
        var data = await LoadAsync();
        var hall = await LoadHallAsync();
        return _hallStats ??= new HallStats(data, hall);
    }

    public async Task<TradeHistory> LoadTradesAsync()
    {
        var stats = await LoadHallStatsAsync();
        return _trades ??= new TradeHistory(await LoadAsync(), await LoadHallAsync(), stats);
    }

    private async Task<LeagueData> LoadCoreAsync()
    {
        var leagueId = config["LeagueId"]
            ?? throw new InvalidOperationException("LeagueId is missing from wwwroot/appsettings.json.");

        var playersTask = playerDirectory.LoadAsync();

        var chain = new List<SleeperLeague>();
        for (var id = leagueId; !string.IsNullOrEmpty(id) && id != "0";)
        {
            var league = await sleeper.GetLeagueAsync(id)
                ?? throw new InvalidOperationException($"League {id} was not found on Sleeper.");
            chain.Add(league);
            id = league.PreviousLeagueId;
        }
        chain.Reverse();

        var seasons = await Task.WhenAll(chain.Select(LoadSeasonAsync));

        var data = new LeagueData { Name = chain[^1].Name };
        foreach (var (pid, p) in await playersTask)
            data.Players[pid] = new PlayerInfo(pid, p.N, p.P, DateOnly.TryParseExact(p.B, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var born) ? born : null);

        // Oldest to newest, so each manager ends up with their most recent name and avatar.
        foreach (var s in seasons)
        {
            foreach (var u in s.Users)
                data.Owners[u.UserId] = new Owner(u.UserId, u.DisplayName ?? "Unknown", u.Metadata?.TeamName, u.Avatar);
            data.Seasons.Add(s.Info);
            data.TeamGames.AddRange(s.Games);
            data.Rosters.AddRange(s.Rosters);
        }
        foreach (var id in data.TeamGames.Select(g => g.OwnerId).Distinct().Where(id => !data.Owners.ContainsKey(id)))
            data.Owners[id] = new Owner(id, "Orphan team", null, null);

        var current = chain[^1];
        foreach (var id in seasons[^1].Info.RosterOwners.Values.Where(id => !id.StartsWith("roster-")))
            data.ActiveOwnerIds.Add(id);
        var qbSlots = current.RosterPositions.Count(p => p is "QB" or "SUPER_FLEX");
        data.ValueFormat = new ValueFormat(
            Teams: current.TotalRosters > 0 ? current.TotalRosters : seasons[^1].Info.RosterOwners.Count,
            Qbs: qbSlots >= 2 ? 2 : 1,
            Ppr: current.ScoringSettings?.GetValueOrDefault("rec") ?? 1.0);

        return data;
    }

    private async Task<(SeasonInfo Info, List<SleeperUser> Users, List<TeamGame> Games, List<WeekRoster> Rosters)> LoadSeasonAsync(SleeperLeague league)
    {
        var id = league.LeagueId;
        var season = int.Parse(league.Season);
        var settings = league.Settings;

        var usersTask = sleeper.GetUsersAsync(id);
        var rostersTask = sleeper.GetRostersAsync(id);
        var bracketTask = sleeper.GetWinnersBracketAsync(id);

        // Assumes one week per playoff round (playoff_round_type 0), which is how this league is set up.
        var rounds = (int)Math.Ceiling(Math.Log2(Math.Max(2, settings.PlayoffTeams)));
        var lastWeek = settings.PlayoffWeekStart + rounds - 1;
        if (league.Status is "pre_draft" or "drafting")
            lastWeek = 0;
        else if (league.Status != "complete")
            lastWeek = Math.Min(lastWeek, settings.LastScoredLeg ?? 0); // skip the week in progress

        var weekTasks = Enumerable.Range(1, Math.Max(0, lastWeek))
            .Select(async w => (Week: w, Matchups: await sleeper.GetMatchupsAsync(id, w)))
            .ToList();

        var users = await usersTask;
        var rosters = await rostersTask;
        var bracket = await bracketTask;
        var weeks = await Task.WhenAll(weekTasks);

        var rosterOwners = rosters.ToDictionary(r => r.RosterId, r => r.OwnerId ?? $"roster-{season}-{r.RosterId}");
        string OwnerOf(int rosterId) => rosterOwners.GetValueOrDefault(rosterId) ?? $"roster-{season}-{rosterId}";

        // Title-path games only. Placement games (3rd, 5th place) are in the winners bracket
        // but count as consolation.
        var bracketGames = bracket
            .Where(b => b.T1 is not null && b.T2 is not null && (b.P is null || b.P == 1))
            .Select(b => (b.R, Math.Min(b.T1!.Value, b.T2!.Value), Math.Max(b.T1!.Value, b.T2!.Value)))
            .ToHashSet();

        var games = new List<TeamGame>();
        var weekRosters = new List<WeekRoster>();
        foreach (var (week, matchups) in weeks)
        {
            foreach (var m in matchups.Where(m => m.Players is { Count: > 0 }))
            {
                weekRosters.Add(new WeekRoster(
                    season, week, OwnerOf(m.RosterId), m.Points ?? 0,
                    m.Players!, (m.Starters ?? []).ToHashSet(), m.PlayersPoints ?? []));
            }

            foreach (var pair in matchups.Where(m => m.MatchupId is not null).GroupBy(m => m.MatchupId))
            {
                if (pair.Count() != 2) continue;
                var a = pair.First();
                var b = pair.Last();
                double pa = a.Points ?? 0, pb = b.Points ?? 0;
                if (pa == 0 && pb == 0) continue;

                var type = week < settings.PlayoffWeekStart ? GameType.Regular
                    : bracketGames.Contains((week - settings.PlayoffWeekStart + 1, Math.Min(a.RosterId, b.RosterId), Math.Max(a.RosterId, b.RosterId)))
                        ? GameType.Playoff
                        : GameType.Consolation;

                var oa = OwnerOf(a.RosterId);
                var ob = OwnerOf(b.RosterId);
                games.Add(new TeamGame(season, week, type, oa, ob, pa, pb));
                games.Add(new TeamGame(season, week, type, ob, oa, pb, pa));
            }
        }

        var finalPlace = new Dictionary<string, int>();
        foreach (var m in bracket.Where(m => m.P is not null && m.W is not null && m.L is not null))
        {
            finalPlace[OwnerOf(m.W!.Value)] = m.P!.Value;
            finalPlace[OwnerOf(m.L!.Value)] = m.P!.Value + 1;
        }
        var titleGame = league.Status == "complete" ? bracket.FirstOrDefault(m => m.P == 1 && m.W is not null) : null;

        var info = new SeasonInfo
        {
            Season = season,
            LeagueId = id,
            Status = league.Status,
            PlayoffWeekStart = settings.PlayoffWeekStart,
            WeeksLoaded = Math.Max(0, lastWeek),
            TransactionLegs = league.Status == "complete" ? 18 : Math.Max(1, settings.Leg ?? lastWeek),
            LineupSlots = league.RosterPositions.Where(p => p is not ("BN" or "IR" or "TAXI")).ToList(),
            RosterOwners = rosterOwners,
            ChampionId = titleGame is null ? null : OwnerOf(titleGame.W!.Value),
            RunnerUpId = titleGame?.L is null ? null : OwnerOf(titleGame.L.Value),
            PlayoffOwnerIds = bracket.SelectMany(m => new[] { m.T1, m.T2 }).OfType<int>().Select(OwnerOf).ToHashSet(),
            FinalPlace = finalPlace,
        };
        return (info, users, games, weekRosters);
    }

    private async Task<HallData> LoadHallCoreAsync()
    {
        var data = await LoadAsync();
        var hall = new HallData();

        var txTasks = data.Seasons
            .SelectMany(s => Enumerable.Range(1, s.TransactionLegs)
                .Select(async leg => (s.Season, Items: await sleeper.GetTransactionsAsync(s.LeagueId, leg))))
            .ToList();

        var draftTasks = data.Seasons
            .Select(async s => (s.Season, Drafts: await sleeper.GetDraftsAsync(s.LeagueId)))
            .ToList();

        foreach (var (season, drafts) in await Task.WhenAll(draftTasks))
        {
            foreach (var draft in drafts.Where(dr => dr.Status == "complete"))
            {
                var rounds = draft.Settings?.Rounds ?? 0;
                var detailTask = sleeper.GetDraftAsync(draft.DraftId);
                var picks = await sleeper.GetDraftPicksAsync(draft.DraftId);
                var slots = (await detailTask)?.SlotToRosterId ?? [];
                foreach (var p in picks)
                {
                    if (string.IsNullOrEmpty(p.PlayerId)) continue;
                    var owner = string.IsNullOrEmpty(p.PickedBy) ? data.OwnerOfRoster(season, p.RosterId) : p.PickedBy;
                    // A big draft is the dynasty startup; the short ones are rookie drafts.
                    hall.Picks.Add(new DraftPick(season, rounds > 8, p.Round, p.PickNo, rounds, p.PlayerId, owner)
                    {
                        OriginalRosterId = slots.GetValueOrDefault(p.DraftSlot.ToString()) ?? p.RosterId,
                    });

                    if (!data.Players.ContainsKey(p.PlayerId) && p.Metadata is not null)
                        data.Players[p.PlayerId] = new PlayerInfo(p.PlayerId,
                            $"{p.Metadata.FirstName} {p.Metadata.LastName}".Trim(),
                            p.Metadata.Position is null ? [] : [p.Metadata.Position]);
                }
            }
        }

        foreach (var (season, items) in await Task.WhenAll(txTasks))
        {
            foreach (var t in items.Where(t => t.Status == "complete"))
            {
                List<PlayerMove> Moves(Dictionary<string, int>? map) =>
                    (map ?? []).Select(kv => new PlayerMove(kv.Key, data.OwnerOfRoster(season, kv.Value))).ToList();

                hall.Transactions.Add(new Transaction(
                    season, t.Leg, t.Type, DateTimeOffset.FromUnixTimeMilliseconds(t.Created),
                    Moves(t.Adds), Moves(t.Drops),
                    (t.RosterIds ?? []).Select(r => data.OwnerOfRoster(season, r)).Distinct().ToList(),
                    t.Type == "waiver" ? t.Settings?.WaiverBid ?? 0 : 0)
                {
                    Picks = (t.DraftPicks ?? []).Select(p => new TradedPick(
                        int.TryParse(p.Season, out var ps) ? ps : season, p.Round, p.RosterId,
                        data.OwnerOfRoster(season, p.PreviousOwnerId), data.OwnerOfRoster(season, p.OwnerId))).ToList(),
                    Faab = (t.WaiverBudget ?? []).Select(f => new FaabMove(
                        data.OwnerOfRoster(season, f.Sender), data.OwnerOfRoster(season, f.Receiver), f.Amount)).ToList(),
                });
            }
        }

        return hall;
    }
}

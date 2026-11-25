using TripleF.Models;

namespace TripleF.Services;

public record PlayerReceived(PlayerInfo Player, int Starts, double Points);

/// <summary>What became of a traded pick: drafted (by whom, into which player) or flipped in a later trade.</summary>
public record PickOutcome(DraftPick? Drafted, PlayerInfo? Player, Owner? DraftedBy, Transaction? FlippedIn, Owner? FlippedTo, int Starts, double Points);

public record PickReceived(TradedPick Pick, Owner OriginalOwner, PickOutcome Outcome);

public record TradeSide(Owner Owner, List<PlayerReceived> Players, List<PickReceived> Picks, int FaabIn)
{
    /// <summary>Starter points the received players and drafted picks produced for this manager.</summary>
    public double Value => Players.Sum(p => p.Points) + Picks.Sum(p => p.Outcome.Points);
}

public record TradeView(Transaction Tx, List<TradeSide> Sides)
{
    public TradeSide? Winner
    {
        get
        {
            var ranked = Sides.OrderByDescending(s => s.Value).ToList();
            return ranked.Count >= 2 && ranked[0].Value > ranked[1].Value ? ranked[0] : null;
        }
    }
    public double Edge => Sides.Count < 2 ? 0 : Sides.Max(s => s.Value) - Sides.OrderByDescending(s => s.Value).Skip(1).First().Value;
}

/// <summary>A pick that changed hands at least once, from original owner to wherever it ended up.</summary>
public record PickJourney(int Season, int Round, Owner OriginalOwner, List<(Transaction Tx, Owner From, Owner To)> Hops, DraftPick? Drafted, PlayerInfo? Player, Owner? DraftedBy, double PointsForDrafter);

public class TradeHistory
{
    public List<TradeView> Trades { get; }
    public List<PickJourney> PickJourneys { get; }

    public TradeHistory(LeagueData d, HallData h, HallStats stats)
    {
        var trades = h.Transactions.Where(t => t.Type == "trade").OrderBy(t => t.Created).ToList();

        DraftPick? FindDraftPick(int season, int round, int originalRoster) =>
            h.Picks.Where(p => p.Season == season && p.Round == round && p.OriginalRosterId == originalRoster)
                .OrderBy(p => p.IsStartup) // traded future picks are rookie picks; prefer those
                .FirstOrDefault();

        bool SamePick(TradedPick a, TradedPick b) =>
            a.Season == b.Season && a.Round == b.Round && a.OriginalRosterId == b.OriginalRosterId;

        Trades = trades.Select(t =>
        {
            var sides = t.OwnerIds.Select(ownerId =>
            {
                var players = t.Adds.Where(a => a.OwnerId == ownerId)
                    .Select(a =>
                    {
                        var (starts, points) = stats.AfterMove(t.Season, t.Leg, a.PlayerId, ownerId);
                        return new PlayerReceived(d.PlayerOf(a.PlayerId), starts, points);
                    })
                    .ToList();

                var picks = t.Picks.Where(p => p.ToOwnerId == ownerId)
                    .Select(p =>
                    {
                        var flip = trades.FirstOrDefault(later => later.Created > t.Created
                            && later.Picks.Any(lp => SamePick(lp, p) && lp.FromOwnerId == ownerId));
                        var flippedTo = flip?.Picks.First(lp => SamePick(lp, p) && lp.FromOwnerId == ownerId).ToOwnerId;
                        var drafted = FindDraftPick(p.Season, p.Round, p.OriginalRosterId);
                        var (starts, points) = flip is null && drafted is not null && drafted.OwnerId == ownerId
                            ? stats.CareerWith(drafted.PlayerId, ownerId)
                            : (0, 0.0);
                        return new PickReceived(p, d.OwnerOf(d.OwnerOfRoster(t.Season, p.OriginalRosterId)),
                            new PickOutcome(drafted, drafted is null ? null : d.PlayerOf(drafted.PlayerId),
                                drafted is null ? null : d.OwnerOf(drafted.OwnerId),
                                flip, flippedTo is null ? null : d.OwnerOf(flippedTo), starts, points));
                    })
                    .ToList();

                var faab = t.Faab.Where(f => f.ToOwnerId == ownerId).Sum(f => f.Amount);
                return new TradeSide(d.OwnerOf(ownerId), players, picks, faab);
            }).ToList();
            return new TradeView(t, sides);
        })
        .OrderByDescending(v => v.Tx.Created)
        .ToList();

        PickJourneys = trades
            .SelectMany(t => t.Picks.Select(p => (Tx: t, Pick: p)))
            .GroupBy(x => (x.Pick.Season, x.Pick.Round, x.Pick.OriginalRosterId))
            .Select(g =>
            {
                var first = g.First();
                var drafted = FindDraftPick(g.Key.Season, g.Key.Round, g.Key.OriginalRosterId);
                return new PickJourney(g.Key.Season, g.Key.Round,
                    d.OwnerOf(d.OwnerOfRoster(first.Tx.Season, g.Key.OriginalRosterId)),
                    g.OrderBy(x => x.Tx.Created).Select(x => (x.Tx, d.OwnerOf(x.Pick.FromOwnerId), d.OwnerOf(x.Pick.ToOwnerId))).ToList(),
                    drafted,
                    drafted is null ? null : d.PlayerOf(drafted.PlayerId),
                    drafted is null ? null : d.OwnerOf(drafted.OwnerId),
                    drafted is null ? 0 : stats.CareerWith(drafted.PlayerId, drafted.OwnerId).Points);
            })
            .OrderByDescending(j => j.Season).ThenBy(j => j.Round).ThenBy(j => j.Drafted?.PickNo ?? int.MaxValue)
            .ToList();
    }
}

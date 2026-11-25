using System.Net.Http.Json;
using System.Text.RegularExpressions;
using TripleF.Models;

namespace TripleF.Services;

/// <summary>Current dynasty values from FantasyCalc (public API, keyed by Sleeper player id).</summary>
public class TradeValueTable
{
    public Dictionary<string, int> Players { get; } = [];
    public Dictionary<(int Season, int Round), int> Picks { get; } = [];

    public int Player(string sleeperId) => Players.GetValueOrDefault(sleeperId);

    /// <summary>
    /// Value of an undrafted pick. Years past FantasyCalc's horizon use its furthest year for that round;
    /// years before it (drafts already held) have no pick value.
    /// </summary>
    public int? Pick(int season, int round)
    {
        if (Picks.TryGetValue((season, round), out var v)) return v;
        var years = Picks.Keys.Where(k => k.Round == round).Select(k => k.Season).ToList();
        if (years.Count == 0 || season < years.Min()) return null;
        return Picks[(years.Max(), round)];
    }
}

public record TradeAssetValue(int Value, string Note);

public class TradeValueService(HttpClient http)
{
    private static readonly Regex GenericPick = new(@"^(\d{4}) (\d)(st|nd|rd|th)$");

    private Task<TradeValueTable>? _load;

    public Task<TradeValueTable> LoadAsync(ValueFormat format)
    {
        if (_load is null || _load.IsFaulted)
            _load = FetchAsync(format);
        return _load;
    }

    private async Task<TradeValueTable> FetchAsync(ValueFormat f)
    {
        var ppr = f.Ppr >= 0.75 ? "1" : f.Ppr >= 0.25 ? "0.5" : "0";
        var url = $"values/current?isDynasty=true&numQbs={f.Qbs}&numTeams={f.Teams}&ppr={ppr}";
        var rows = await http.GetFromJsonAsync<List<FcRow>>(url) ?? [];

        var table = new TradeValueTable();
        foreach (var row in rows)
        {
            if (row.Player is null) continue;
            if (row.Player.Position == "PICK")
            {
                // Only the generic "2027 1st" entries; the Early/Mid/Late variants need a draft slot we don't know yet.
                var m = GenericPick.Match(row.Player.Name ?? "");
                if (m.Success)
                    table.Picks[(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))] = row.Value;
            }
            else if (!string.IsNullOrEmpty(row.Player.SleeperId))
            {
                table.Players[row.Player.SleeperId] = row.Value;
            }
        }
        return table;
    }

    /// <summary>A traded pick is worth the player it became, or its current pick value if it hasn't been used.</summary>
    public static TradeAssetValue PickValue(TradeValueTable t, PickReceived pick)
    {
        if (pick.Outcome.Player is { } p)
            return new TradeAssetValue(t.Player(p.Id), $"now {p.Name}");
        return t.Pick(pick.Pick.Season, pick.Pick.Round) is int v
            ? new TradeAssetValue(v, "pick value")
            : new TradeAssetValue(0, "no value");
    }

    public static int SideValue(TradeValueTable t, TradeSide side) =>
        side.Players.Sum(p => t.Player(p.Player.Id)) + side.Picks.Sum(p => PickValue(t, p).Value);

    private class FcRow
    {
        public FcPlayer? Player { get; set; }
        public int Value { get; set; }
    }

    private class FcPlayer
    {
        public string? Name { get; set; }
        public string? Position { get; set; }
        public string? SleeperId { get; set; }
    }
}

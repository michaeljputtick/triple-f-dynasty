using System.Net.Http.Json;
using System.Text.Json;
using TripleF.Models;

namespace TripleF.Services;

/// <summary>Thin wrapper over the public Sleeper read API (https://docs.sleeper.com).</summary>
public class SleeperClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public Task<SleeperLeague?> GetLeagueAsync(string leagueId) =>
        http.GetFromJsonAsync<SleeperLeague>($"league/{leagueId}", Json);

    public async Task<List<SleeperUser>> GetUsersAsync(string leagueId) =>
        await http.GetFromJsonAsync<List<SleeperUser>>($"league/{leagueId}/users", Json) ?? [];

    public async Task<List<SleeperRoster>> GetRostersAsync(string leagueId) =>
        await http.GetFromJsonAsync<List<SleeperRoster>>($"league/{leagueId}/rosters", Json) ?? [];

    public async Task<List<SleeperMatchup>> GetMatchupsAsync(string leagueId, int week) =>
        await http.GetFromJsonAsync<List<SleeperMatchup>>($"league/{leagueId}/matchups/{week}", Json) ?? [];

    public async Task<List<SleeperBracketMatch>> GetWinnersBracketAsync(string leagueId) =>
        await http.GetFromJsonAsync<List<SleeperBracketMatch>>($"league/{leagueId}/winners_bracket", Json) ?? [];

    /// <summary>Future picks that have changed hands (original roster -> current owner roster).</summary>
    public async Task<List<SleeperTradedPick>> GetTradedPicksAsync(string leagueId) =>
        await http.GetFromJsonAsync<List<SleeperTradedPick>>($"league/{leagueId}/traded_picks", Json) ?? [];

    public async Task<List<SleeperTransaction>> GetTransactionsAsync(string leagueId, int leg) =>
        await http.GetFromJsonAsync<List<SleeperTransaction>>($"league/{leagueId}/transactions/{leg}", Json) ?? [];

    public async Task<List<SleeperDraft>> GetDraftsAsync(string leagueId) =>
        await http.GetFromJsonAsync<List<SleeperDraft>>($"league/{leagueId}/drafts", Json) ?? [];

    public Task<SleeperDraft?> GetDraftAsync(string draftId) =>
        http.GetFromJsonAsync<SleeperDraft>($"draft/{draftId}", Json);

    public async Task<List<SleeperDraftPick>> GetDraftPicksAsync(string draftId) =>
        await http.GetFromJsonAsync<List<SleeperDraftPick>>($"draft/{draftId}/picks", Json) ?? [];
}

/// <summary>Reads the trimmed player list bundled with the site (wwwroot/data/players.json).</summary>
public class PlayerDirectory(HttpClient siteHttp)
{
    public async Task<Dictionary<string, PlayerFileEntry>> LoadAsync()
    {
        try
        {
            return await siteHttp.GetFromJsonAsync<Dictionary<string, PlayerFileEntry>>("data/players.json") ?? [];
        }
        catch (HttpRequestException)
        {
            return []; // names fall back to draft metadata or "Player {id}"
        }
    }
}

namespace TripleF.Models;

// Raw Sleeper API shapes. Deserialized with a snake_case naming policy, so
// PreviousLeagueId <- previous_league_id, etc. Unused fields are omitted.

public class SleeperLeague
{
    public string LeagueId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Season { get; set; } = "";
    public string Status { get; set; } = "";
    public string? PreviousLeagueId { get; set; }
    public List<string> RosterPositions { get; set; } = [];
    public SleeperLeagueSettings Settings { get; set; } = new();
    public Dictionary<string, double>? ScoringSettings { get; set; }
    public int TotalRosters { get; set; }
}

public class SleeperLeagueSettings
{
    public int PlayoffWeekStart { get; set; }
    public int PlayoffTeams { get; set; }
    public int PlayoffRoundType { get; set; }
    public int? LastScoredLeg { get; set; }
    public int? Leg { get; set; }
}

public class SleeperUser
{
    public string UserId { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Avatar { get; set; }
    public SleeperUserMetadata? Metadata { get; set; }
}

public class SleeperUserMetadata
{
    public string? TeamName { get; set; }
}

public class SleeperRoster
{
    public int RosterId { get; set; }
    public string? OwnerId { get; set; }
    public List<string>? Players { get; set; }
}

public class SleeperMatchup
{
    public int RosterId { get; set; }
    public int? MatchupId { get; set; }
    public double? Points { get; set; }
    public List<string>? Players { get; set; }
    public List<string>? Starters { get; set; }
    public Dictionary<string, double>? PlayersPoints { get; set; }
}

public class SleeperTransaction
{
    public string Type { get; set; } = "";   // trade, waiver, free_agent, commissioner
    public string Status { get; set; } = ""; // complete, failed
    public int Leg { get; set; }
    public long Created { get; set; }
    public Dictionary<string, int>? Adds { get; set; }  // player id -> roster id
    public Dictionary<string, int>? Drops { get; set; }
    public List<int>? RosterIds { get; set; }
    public SleeperTransactionSettings? Settings { get; set; }
    public List<SleeperTradedPick>? DraftPicks { get; set; }
    public List<SleeperFaabMove>? WaiverBudget { get; set; }
}

public class SleeperTradedPick
{
    public string Season { get; set; } = "";
    public int Round { get; set; }
    public int RosterId { get; set; }        // original owner of the pick
    public int OwnerId { get; set; }         // roster receiving it in this trade
    public int PreviousOwnerId { get; set; } // roster giving it up
}

public class SleeperFaabMove
{
    public int Sender { get; set; }
    public int Receiver { get; set; }
    public int Amount { get; set; }
}

public class SleeperTransactionSettings
{
    public int? WaiverBid { get; set; }
}

public class SleeperDraft
{
    public string DraftId { get; set; } = "";
    public string Status { get; set; } = "";
    public long? StartTime { get; set; } // epoch ms
    public SleeperDraftSettings? Settings { get; set; }
    public Dictionary<string, int?>? SlotToRosterId { get; set; } // only on draft/{id}, not the league's draft list
}

public class SleeperDraftSettings
{
    public int Rounds { get; set; }
}

public class SleeperDraftPick
{
    public string PlayerId { get; set; } = "";
    public string? PickedBy { get; set; }
    public int RosterId { get; set; }
    public int Round { get; set; }
    public int PickNo { get; set; }
    public int DraftSlot { get; set; }
    public SleeperDraftPickMetadata? Metadata { get; set; }
}

public class SleeperDraftPickMetadata
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Position { get; set; }
}

/// <summary>One entry in wwwroot/data/players.json (see tools/refresh-players.ps1).</summary>
public class PlayerFileEntry
{
    public string N { get; set; } = "";
    public List<string> P { get; set; } = [];
    public string? B { get; set; } // birth date, yyyy-MM-dd
}

public class SleeperBracketMatch
{
    public int R { get; set; }   // round
    public int M { get; set; }   // match id
    public int? T1 { get; set; } // roster ids
    public int? T2 { get; set; }
    public int? W { get; set; }
    public int? L { get; set; }
    public int? P { get; set; }  // placement decided by this match (1 = title game, 3 = third place, ...)
}

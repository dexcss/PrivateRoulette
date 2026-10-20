using System;
using System.Collections.Generic;

using Newtonsoft.Json;

namespace PrivateRoulette.Protocol;

// =====================================================================
// Wire models for roulette.gamba.pro — see PROTOCOL.md for the reference.
// Property names MUST match the server's JSON exactly; they are validated
// against a live dealer capture (wire-capture-2026-10-20.txt).
// =====================================================================

public sealed class AuthResult
{
    [JsonProperty("ok")] public bool Ok { get; set; }
    [JsonProperty("themes")] public List<ThemeInfo> Themes { get; set; } = new();
    [JsonProperty("api_key")] public string ApiKey { get; set; } = string.Empty;
    [JsonProperty("message")] public string Message { get; set; } = string.Empty;
}

public sealed class ThemeInfo
{
    [JsonProperty("key")] public string Key { get; set; } = string.Empty;
    [JsonProperty("display_name")] public string DisplayName { get; set; } = string.Empty;
}

public sealed class GameSummary
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("theme")] public string? Theme { get; set; }
    [JsonProperty("theme_display_name")] public string? ThemeDisplayName { get; set; }
    [JsonProperty("players")] public int Players { get; set; }
    [JsonProperty("status")] public string Status { get; set; } = "active";
    [JsonProperty("spinning")] public bool Spinning { get; set; }
    [JsonProperty("created_ts")] public DateTime? CreatedTs { get; set; }
    [JsonProperty("creator_name")] public string CreatorName { get; set; } = string.Empty;
    [JsonProperty("creator_homeworld")] public string CreatorHomeworld { get; set; } = string.Empty;
    [JsonProperty("spin_mode")] public string SpinMode { get; set; } = "control";
    [JsonProperty("control_number")] public string? ControlNumber { get; set; }
}

public sealed class GamesList
{
    [JsonProperty("games")] public List<GameSummary> Games { get; set; } = new();
}

public sealed class PlayerState
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;          // "Char@World"
    [JsonProperty("display_name")] public string DisplayName { get; set; } = string.Empty;
    [JsonProperty("balance")] public long Balance { get; set; }
    [JsonProperty("ready")] public bool Ready { get; set; }
    [JsonProperty("away")] public bool Away { get; set; }
    [JsonProperty("bets")] public Dictionary<string, long> Bets { get; set; } = new();
    [JsonProperty("color")] public string Color { get; set; } = "#4A90E2";
    [JsonProperty("chip_style")] public string ChipStyle { get; set; } = "classic";
    [JsonProperty("last_round_net")] public long LastRoundNet { get; set; }
    [JsonProperty("is_vip")] public bool IsVip { get; set; }
    [JsonProperty("is_freeplay")] public bool IsFreeplay { get; set; }
    [JsonProperty("stashed_balance")] public long StashedBalance { get; set; }
    [JsonProperty("player_key")] public string? PlayerKey { get; set; }
}

public sealed class ChatRateLimit
{
    [JsonProperty("max_messages")] public int MaxMessages { get; set; } = 1;
    [JsonProperty("per_seconds")] public int PerSeconds { get; set; } = 2;
}

public sealed class GameState
{
    [JsonProperty("spinning")] public bool Spinning { get; set; }
    [JsonProperty("table_locked")] public bool TableLocked { get; set; }
    [JsonProperty("theme")] public string? Theme { get; set; }
    [JsonProperty("theme_display_name")] public string? ThemeDisplayName { get; set; }
    [JsonProperty("creator_name")] public string CreatorName { get; set; } = string.Empty;
    [JsonProperty("creator_homeworld")] public string CreatorHomeworld { get; set; } = string.Empty;
    [JsonProperty("spin_mode")] public string SpinMode { get; set; } = "control";
    [JsonProperty("control_number")] public string? ControlNumber { get; set; }
    [JsonProperty("max_bet_inner")] public long MaxBetInner { get; set; }
    [JsonProperty("max_bet_outer")] public long MaxBetOuter { get; set; }
    [JsonProperty("max_bet_inner_vip")] public long MaxBetInnerVip { get; set; }
    [JsonProperty("max_bet_outer_vip")] public long MaxBetOuterVip { get; set; }
    [JsonProperty("max_bet_total")] public long MaxBetTotal { get; set; }
    [JsonProperty("max_bet_total_vip")] public long MaxBetTotalVip { get; set; }
    [JsonProperty("chip_denominations")] public List<long>? ChipDenominations { get; set; }
    [JsonProperty("game_status")] public string GameStatus { get; set; } = "active";
    [JsonProperty("chat_rate_limit")] public ChatRateLimit ChatRateLimit { get; set; } = new();
    [JsonProperty("players")] public List<PlayerState> Players { get; set; } = new();
}

public sealed class GameCreated
{
    [JsonProperty("game_id")] public string GameId { get; set; } = string.Empty;
    [JsonProperty("theme")] public string? Theme { get; set; }
    [JsonProperty("spin_mode")] public string SpinMode { get; set; } = "control";
    [JsonProperty("control_number")] public string? ControlNumber { get; set; }
    [JsonProperty("max_bet_inner")] public long MaxBetInner { get; set; }
    [JsonProperty("max_bet_outer")] public long MaxBetOuter { get; set; }
    [JsonProperty("max_bet_inner_vip")] public long MaxBetInnerVip { get; set; }
    [JsonProperty("max_bet_outer_vip")] public long MaxBetOuterVip { get; set; }
    [JsonProperty("max_bet_total")] public long MaxBetTotal { get; set; }
    [JsonProperty("max_bet_total_vip")] public long MaxBetTotalVip { get; set; }
}

public sealed class PlayerCreated
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;
    [JsonProperty("balance")] public long Balance { get; set; }
    [JsonProperty("game_id")] public string GameId { get; set; } = string.Empty;
    [JsonProperty("color")] public string Color { get; set; } = "#4A90E2";
    [JsonProperty("player_key")] public string? PlayerKey { get; set; }
}

public sealed class SpinReady
{
    [JsonProperty("game_id")] public string GameId { get; set; } = string.Empty;
    [JsonProperty("message")] public string Message { get; set; } = string.Empty;
    [JsonProperty("spin_mode")] public string SpinMode { get; set; } = "control";
    [JsonProperty("control_number")] public string? ControlNumber { get; set; }
}

public sealed class SpinProof
{
    [JsonProperty("dice_roll")] public long? DiceRoll { get; set; }
    [JsonProperty("control_number")] public string ControlNumber { get; set; } = string.Empty;
    [JsonProperty("hash")] public string Hash { get; set; } = string.Empty;
    [JsonProperty("seed")] public long Seed { get; set; }
    [JsonProperty("result")] public int Result { get; set; }
}

public sealed class SpinResult
{
    [JsonProperty("result")] public int Result { get; set; }
    [JsonProperty("color")] public string Color { get; set; } = string.Empty;
    [JsonProperty("proof")] public SpinProof? Proof { get; set; }
    [JsonProperty("control_number")] public string? ControlNumber { get; set; }
}

public sealed class RoundResult
{
    [JsonProperty("result")] public int Result { get; set; }
    [JsonProperty("color")] public string Color { get; set; } = string.Empty;
    [JsonProperty("balances")] public Dictionary<string, long> Balances { get; set; } = new();
    [JsonProperty("dice_roll")] public long? DiceRoll { get; set; }
    [JsonProperty("results")] public List<RoundBetResult> Results { get; set; } = new();
}

public sealed class RoundBetResult
{
    [JsonProperty("player_id")] public string PlayerId { get; set; } = string.Empty;
    [JsonProperty("player_name")] public string PlayerName { get; set; } = string.Empty;
    [JsonProperty("bet_type")] public string BetType { get; set; } = string.Empty;
    [JsonProperty("bet_label")] public string BetLabel { get; set; } = string.Empty;
    [JsonProperty("bet_amount")] public long BetAmount { get; set; }
    [JsonProperty("payout")] public long Payout { get; set; }
    [JsonProperty("net")] public long Net { get; set; }
}

public sealed class TableHistoryEntry
{
    [JsonProperty("ts")] public DateTime Ts { get; set; }
    [JsonProperty("result")] public int Result { get; set; }
    [JsonProperty("color")] public string Color { get; set; } = string.Empty;
}

public sealed class TableHistory
{
    [JsonProperty("history")] public List<TableHistoryEntry> History { get; set; } = new();
}

public sealed class ChatMessage
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("from_id")] public string FromId { get; set; } = string.Empty;
    [JsonProperty("from_name")] public string FromName { get; set; } = string.Empty;
    [JsonProperty("text")] public string Text { get; set; } = string.Empty;
    [JsonProperty("ts")] public DateTime Ts { get; set; }
    [JsonProperty("role")] public string Role { get; set; } = "player";
    [JsonProperty("color")] public string Color { get; set; } = "#ffffff";
}

public sealed class ChatHistory
{
    [JsonProperty("messages")] public List<ChatMessage> Messages { get; set; } = new();
}

public sealed class PublicBets
{
    [JsonProperty("players")] public List<PublicPlayerBets> Players { get; set; } = new();
}

public sealed class PublicPlayerBets
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;
    [JsonProperty("display_name")] public string DisplayName { get; set; } = string.Empty;
    [JsonProperty("color")] public string Color { get; set; } = "#4A90E2";
    [JsonProperty("chip_style")] public string ChipStyle { get; set; } = "classic";
    [JsonProperty("bets")] public Dictionary<string, long> Bets { get; set; } = new();
    [JsonProperty("ready")] public bool Ready { get; set; }
    [JsonProperty("away")] public bool Away { get; set; }
    [JsonProperty("is_vip")] public bool IsVip { get; set; }
    [JsonProperty("is_freeplay")] public bool IsFreeplay { get; set; }
}

public sealed class ReadyUpdate
{
    [JsonProperty("player_id")] public string PlayerId { get; set; } = string.Empty;
    [JsonProperty("ready")] public bool Ready { get; set; }
}

public sealed class AwayUpdate
{
    [JsonProperty("player_id")] public string PlayerId { get; set; } = string.Empty;
    [JsonProperty("away")] public bool Away { get; set; }
}

public sealed class GenericAck
{
    [JsonProperty("success")] public bool Success { get; set; }
    [JsonProperty("message")] public string? Message { get; set; }
    // server is loose here; keep the raw json for diagnostics
    [JsonProperty("game_id")] public string? GameId { get; set; }
}

public sealed class LedgerPlayer
{
    [JsonProperty("player_id")] public string PlayerId { get; set; } = string.Empty;
    [JsonProperty("player_name")] public string PlayerName { get; set; } = string.Empty;
    [JsonProperty("color")] public string Color { get; set; } = string.Empty;
    [JsonProperty("total_buy_in")] public long TotalBuyIn { get; set; }
    [JsonProperty("cash_out_amount")] public long? CashOutAmount { get; set; }
    [JsonProperty("current_balance")] public long CurrentBalance { get; set; }
    [JsonProperty("effective_balance")] public long EffectiveBalance { get; set; }
    [JsonProperty("left_at")] public DateTime? LeftAt { get; set; }
}

public sealed class GameLedgerResponse
{
    [JsonProperty("players")] public List<LedgerPlayer> Players { get; set; } = new();
}

public sealed class DealerStats
{
    [JsonProperty("games")] public int Games { get; set; }
    [JsonProperty("active_games")] public int ActiveGames { get; set; }
    [JsonProperty("total_rounds")] public long TotalRounds { get; set; }
    [JsonProperty("total_bets")] public long TotalBets { get; set; }
    [JsonProperty("total_payouts")] public long TotalPayouts { get; set; }
    [JsonProperty("total_tips")] public long TotalTips { get; set; }
    [JsonProperty("players_served")] public long PlayersServed { get; set; }
    [JsonProperty("house_edge")] public long HouseEdge { get; set; }
}

public sealed class HistoricalLedger
{
    [JsonProperty("game_info")] public Dictionary<string, object?> GameInfo { get; set; } = new();
    [JsonProperty("summary")] public Dictionary<string, object?> Summary { get; set; } = new();
    [JsonProperty("players")] public List<Dictionary<string, object?>> Players { get; set; } = new();
    [JsonProperty("rounds")] public List<Dictionary<string, object?>> Rounds { get; set; } = new();
    [JsonProperty("all_bets")] public List<Dictionary<string, object?>> AllBets { get; set; } = new();
    [JsonProperty("adjustments")] public List<Dictionary<string, object?>> Adjustments { get; set; } = new();
}

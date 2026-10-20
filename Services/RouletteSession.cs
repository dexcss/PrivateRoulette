using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

using PrivateRoulette.Configuration;
using PrivateRoulette.Protocol;

namespace PrivateRoulette.Services;

/// <summary>
/// High-level dealer session state: connection/auth, the current game, its
/// players and bets, and the spin workflow. Mutations happen on the framework
/// thread (marshalled via <see cref="RunOnMainThread"/> by <see cref="SocketService"/>).
/// </summary>
public sealed class RouletteSession : IDisposable
{
    private readonly PluginConfig config;
    private readonly SocketService socket;
    private readonly IPluginLog log;
    private readonly IChatGui? chat;

    public Action<Action>? RunOnMainThread { get; set; }

    /// <summary>Executes a game chat command — set by the plugin.</summary>
    public Action<string>? RunCommand { get; set; }

    /// <summary>Sends a chat line through the game's chat entry — set by the plugin.</summary>
    public Action<string>? SendChatText { get; set; }

    // ---- connection state ----
    public bool Authenticated { get; private set; }
    public string AuthMessage { get; private set; } = string.Empty;
        // ---- access control ----
    /// <summary>False until the current character passes the whitelist. Socket stays disconnected while false.</summary>
    public bool IsAuthorized { get; private set; }
    public string AuthorizationNote { get; private set; } = "checking character…";

    /// <summary>Re-evaluated each framework tick; returns true once the character is whitelisted.</summary>
    public bool EvaluateAuthorization()
    {
        var (charId, name, world) = CharacterContext.Current;
        if (charId == 0 || string.IsNullOrEmpty(name))
        {
            this.IsAuthorized = false;
            this.AuthorizationNote = "log in to verify authorization";
            return false;
        }

        var ok = this.config.IsDealerAuthorized(name, world);
        if (ok != this.IsAuthorized)
        {
            this.IsAuthorized = ok;
            this.AuthorizationNote = ok
                ? $"{name}@{world} authorized"
                : $"{name}@{world} is not on the authorized dealer list";
            this.log.Info($"authorization: {this.AuthorizationNote}");
            this.Changed?.Invoke();
        }

        return ok;
    }

    // ---- current game ----
    public string? GameId { get; private set; }
    public GameState? State { get; private set; }
    public SpinReady? PendingSpin { get; private set; }
    public SpinResult? LastSpin { get; private set; }
    public List<TableHistoryEntry> History { get; } = new();
    public List<ChatMessage> Chat { get; } = new();
    public List<GameSummary> KnownGames { get; private set; } = new();
    public HistoricalLedger? LastLedger { get; private set; }
    public DealerStats? Stats { get; private set; }

    /// <summary>Waiting for the dealer to roll the dice (control mode).</summary>
    public bool AwaitingDice { get; private set; }

    public event Action? Changed;
    public event Action<SpinResult>? SpinReceived;

    public RouletteSession(PluginConfig config, SocketService socket, IPluginLog log, IChatGui? chat = null)
    {
        this.config = config;
        this.socket = socket;
        this.log = log;
        this.chat = chat;

        socket.Connected += this.OnConnected;
        socket.AuthOk += this.OnAuthOk;
        socket.Disconnected += _ => this.Reset();
        socket.GamesList += g => { this.KnownGames = g.Games; this.Changed?.Invoke(); };
        socket.GameCreated += g => { this.GameId = g.GameId; this.State = null; this.Changed?.Invoke(); };
        socket.GameState += this.OnGameState;
        socket.PlayerCreated += this.OnPlayerCreated;
        socket.SpinReady += r => { this.PendingSpin = r; this.AwaitingDice = r.SpinMode == "control"; this.Changed?.Invoke(); };
        socket.Spin += r => { this.LastSpin = r; this.PendingSpin = null; this.AwaitingDice = false; this.SpinReceived?.Invoke(r); this.Changed?.Invoke(); };
        socket.RoundResult += r => { this.AwaitingDice = false; this.AnnounceRoundToParty(r); this.Changed?.Invoke(); };
        socket.TableHistory += r => { this.History.Clear(); this.History.AddRange(r.History); this.Changed?.Invoke(); };
        socket.ChatHistory += r => { this.Chat.Clear(); this.Chat.AddRange(r.Messages); this.Changed?.Invoke(); };
        socket.ChatMessage += m => { this.Chat.Add(m); this.Changed?.Invoke(); };
        socket.Ledger += l => { this.LastLedger = l; this.Changed?.Invoke(); };
        socket.DealerStats += s => { this.Stats = s; this.Changed?.Invoke(); };
        socket.DealerError += (msg, raw) => { this.AuthMessage = msg; this.log.Warning($"server error: {msg} {raw}"); this.Changed?.Invoke(); };
        socket.DealerOk += (msg, _) => { this.AuthMessage = msg; this.Changed?.Invoke(); };
    }

    private void OnConnected()
    {
        this.log.Debug("connected — hello handled by socket service");
    }

    private void OnAuthOk()
    {
        this.Authenticated = true;
        this.AuthMessage = "";
        this.log.Info("authenticated with backend");
        // re-sync the current table if we had one, otherwise refresh the lobby
        if (this.GameId != null)
            _ = this.socket.JoinGameAsync(this.GameId);
        else
            _ = this.socket.ListGamesAsync();
        this.Changed?.Invoke();
    }

    private void OnGameState(GameState state)
    {
        // keep player_key mapping fresh for links
        this.State = state;
        if (this.GameId == null && state.Players.Count > 0)
        {
            // joined state pushed after join; GameId arrives from game_created/join
        }
        this.Changed?.Invoke();
    }

    private void OnPlayerCreated(PlayerCreated p)
    {
        // server echoes the created player; state refresh will follow
        this.log.Info($"player created: {p.Name} key={p.PlayerKey?[..8]}…");
        this.Changed?.Invoke();
    }

    private void Reset()
    {
        // Keep the current table selected across reconnects: the UI stays put and
        // we re-sync with join_game once the socket is back.
        this.Authenticated = false;
        this.AuthMessage = "disconnected — reconnecting…";
        this.Changed?.Invoke();
    }

    /// <summary>Hello payload for the backend: role, token, dealer identity. Null when unusable.</summary>
    public object? CurrentHello()
    {
        var (charId, name, world) = CharacterContext.Current;
        if (charId == 0 || string.IsNullOrEmpty(name))
            return null;

        if (string.IsNullOrEmpty(this.config.DealerToken))
            return null;

        return new
        {
            role = "dealer",
            token = this.config.DealerToken,
            dealer_name = name,
            dealer_world = world,
        };
    }

    public void SetDealerToken(string token)
    {
        this.config.DealerToken = token.Trim();
        this.config.Save();
    }

    public string DealerToken => this.config.DealerToken;

    // ---- actions ----

    public void CreateGame(SpinMode mode)
        => _ = this.socket.CreateGameAsync(null, mode, this.config.MaxBetInner, this.config.MaxBetOuter, this.config.MaxBetInnerVip, this.config.MaxBetOuterVip,
            this.config.ChipPresets.FirstOrDefault()?.Values?.Select(v => (long)v).ToArray(),
            CharacterContext.Name, CharacterContext.World);

    public void JoinGame(string gameId)
    {
        this.GameId = gameId;
        _ = this.socket.JoinGameAsync(gameId);
    }

    public void AddPlayer(string name, string displayName, long bankroll, bool isVip = false, bool isFreeplay = false)
    {
        if (this.GameId == null) return;
        _ = this.socket.AddPlayerAsync(name, displayName, bankroll, this.GameId, isVip, isFreeplay);
    }

    public void AdjustBalance(string playerId, long delta)
    {
        if (this.GameId == null) return;
        _ = this.socket.AdjustBalanceAsync(playerId, delta, this.GameId);
    }

    public void CashoutPlayer(string playerId)
    {
        if (this.GameId == null) return;
        _ = this.socket.CashoutPlayerAsync(playerId, this.GameId);
    }

    public void GetStats() => _ = this.socket.GetStatsAsync();

    public void EndGame()
    {
        if (this.GameId == null) return;
        _ = this.socket.EndGameAsync(this.GameId);
    }

    public void DeleteGame()
    {
        if (this.GameId == null) return;
        _ = this.socket.DeleteGameAsync(this.GameId);
        this.LeaveTable();
    }

    public void Echo(string text) => this.chat?.Print(new XivChatEntry { Message = text, Type = XivChatType.Echo });

    public void RequestSpin()
    {
        if (this.GameId == null) return;
        _ = this.socket.RequestSpinAsync(this.GameId);
    }

    /// <summary>Spin with the dealer's dice roll (control mode) or forced number (manual mode).</summary>
    public void Spin(long? diceRoll, int? forcedNumber)
    {
        if (this.GameId == null) return;
        _ = this.socket.SpinAsync(this.GameId, forcedNumber, diceRoll);
        this.AwaitingDice = false;
    }

    public void ClearBets()
    {
        if (this.GameId == null) return;
        _ = this.socket.ClearBetsAsync(this.GameId);
    }

    public void Rebet()
    {
        if (this.GameId == null) return;
        _ = this.socket.RebetAsync(this.GameId);
    }

    public void RefreshLedger()
    {
        if (this.GameId == null) return;
        _ = this.socket.GetLedgerAsync(this.GameId);
            }

    public void LeaveTable()
    {
        this.GameId = null;
        this.State = null;
        this.PendingSpin = null;
        this.LastSpin = null;
        this.AwaitingDice = false;
        _ = this.socket.ListGamesAsync();
        this.Changed?.Invoke();
    }

    public void SendChat(string text)
    {
        if (this.GameId == null) return;
        _ = this.socket.SendChatAsync(this.GameId, text);
        // local echo so the dealer sees what was announced without opening the web client
        this.chat?.Print(new XivChatEntry { Message = $"[table] {text}", Type = XivChatType.Echo });
    }

    /// <summary>Match a seated player by full name or display name.</summary>
    public (string Id, string Name)? FindPlayerByName(string name)
    {
        var players = this.State?.Players;
        if (players == null) return null;
        foreach (var p in players)
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Name.Split('@')[0], name, StringComparison.OrdinalIgnoreCase))
                return (p.Id, p.DisplayName);
        }

        return null;
    }

    /// <summary>Applies a completed trade's gil delta to a player's balance.</summary>
    public void ApplyTradeDelta(string playerId, long delta)
    {
        if (this.GameId == null || delta == 0) return;
        _ = this.socket.AdjustBalanceAsync(playerId, delta, this.GameId, "Trade");
    }

    /// <summary>Adds the dealer's current target as a player.</summary>
    public bool AddTargetedPlayer(long bankroll, bool isVip, bool isFreeplay)
    {
        var full = this.GetTargetPlayerName?.Invoke();
        if (string.IsNullOrEmpty(full)) return false;
        var display = full.Split('@')[0];
        this.AddPlayer(full, display, bankroll, isVip, isFreeplay);
        return true;
    }

    /// <summary>Set by the plugin: name@world of the current target (null if not a player).</summary>
    public Func<string?>? GetTargetPlayerName { get; set; }

    /// <summary>Set by the plugin: true when the dealer is in a party.</summary>
    public Func<bool>? IsInParty { get; set; }

    private void AnnounceRoundToParty(RoundResult result)
    {
        if (!this.config.AnnouncePartyResults) return;
        if (this.IsInParty?.Invoke() != true) return;

        _ = System.Threading.Tasks.Task.Delay(450).ContinueWith(_ =>
        {
            this.RunOnMainThread?.Invoke(() =>
            {
                try
                {
                    var number = Wheel.Display(result.Result);
                    this.RunCommand?.Invoke($"/p Number {number} ({result.Color})!");
                    var players = this.State?.Players;
                    if (players == null || players.Count == 0) return;

                    var parts = new System.Collections.Generic.List<string>();
                    foreach (var p in players)
                    {
                        var net = p.LastRoundNet >= 0 ? $"+{p.LastRoundNet:N0}" : $"{p.LastRoundNet:N0}";
                        parts.Add($"{p.DisplayName}: {p.Balance:N0} ({net})");
                    }

                    var line = "Balances — " + string.Join(" | ", parts);
                    while (line.Length > 240)
                    {
                        var cut = line.LastIndexOf(" | ", 240, StringComparison.Ordinal);
                        if (cut < 0) cut = Math.Min(240, line.Length);
                        this.RunCommand?.Invoke($"/p {line[..cut]}");
                        line = cut + 3 <= line.Length ? "Balances — " + line[(cut + 3)..] : string.Empty;
                        if (line.Length <= "Balances — ".Length) return;
                    }

                    if (line.Length > 0) this.RunCommand?.Invoke($"/p {line}");
                }
                catch
                {
                    // party chat is best-effort
                }
            });
        });
    }
    /// <summary>Whispers the player their invite link via /tell.</summary>
    public void SendLink(PlayerState player)
    {
        var link = this.PlayerLink(player);
        if (link == null) return;
        if (this.SendChatText != null)
            this.SendChatText($"/tell {player.Name} Here is your link to play: {link}");
        else
            this.RunCommand?.Invoke($"/tell {player.Name} Here is your link to play: {link}");
    }

    /// <summary>Player betting link for the given player.</summary>
    public string? PlayerLink(PlayerState player)
        => player.PlayerKey == null ? null : $"https://dexcss.github.io/PrivateRoulette/#/{this.GameId}/{player.PlayerKey}";

    public void Dispose()
    {
        // socket lifetime owned by plugin
    }
}

/// <summary>Snapshot of the logged-in character, set by DiceService/Plugin on login/tick.</summary>
public static class CharacterContext
{
    public static ulong ContentId { get; private set; }
    public static string Name { get; private set; } = string.Empty;
    public static string World { get; private set; } = string.Empty;
    public static (ulong charId, string name, string world) Current => (ContentId, Name, World);

    public static void Update(ulong contentId, string name, string world)
    {
        ContentId = contentId;
        Name = name;
        World = world;
    }
}

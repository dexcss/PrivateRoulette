using System;
using System.Collections.Generic;
using System.Linq;

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

    public Action<Action>? RunOnMainThread { get; set; }

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

    /// <summary>Waiting for the dealer to roll the dice (control mode).</summary>
    public bool AwaitingDice { get; private set; }

    public event Action? Changed;

    public RouletteSession(PluginConfig config, SocketService socket, IPluginLog log)
    {
        this.config = config;
        this.socket = socket;
        this.log = log;

        socket.Connected += this.OnConnected;
        socket.AuthOk += this.OnAuthOk;
        socket.Disconnected += _ => this.Reset();
        socket.GamesList += g => { this.KnownGames = g.Games; this.Changed?.Invoke(); };
        socket.GameCreated += g => { this.GameId = g.GameId; this.State = null; this.Changed?.Invoke(); };
        socket.GameState += this.OnGameState;
        socket.PlayerCreated += this.OnPlayerCreated;
        socket.SpinReady += r => { this.PendingSpin = r; this.AwaitingDice = r.SpinMode == "control"; this.Changed?.Invoke(); };
        socket.Spin += r => { this.LastSpin = r; this.PendingSpin = null; this.AwaitingDice = false; this.Changed?.Invoke(); };
        socket.RoundResult += r => { this.AwaitingDice = false; this.Changed?.Invoke(); };
        socket.TableHistory += r => { this.History.Clear(); this.History.AddRange(r.History); this.Changed?.Invoke(); };
        socket.ChatHistory += r => { this.Chat.Clear(); this.Chat.AddRange(r.Messages); this.Changed?.Invoke(); };
        socket.ChatMessage += m => { this.Chat.Add(m); this.Changed?.Invoke(); };
        socket.DealerError += (msg, raw) => { this.AuthMessage = msg; this.log.Warning($"server error: {msg} {raw}"); this.Changed?.Invoke(); };
        socket.DealerOk += (msg, _) => { this.AuthMessage = msg; this.Changed?.Invoke(); };
    }

    private void OnConnected()
    {
        var hello = this.CurrentHello();
        if (hello != null)
            _ = this.socket.SendAsync("hello", hello);
        else
            this.log.Warning("no dealer token configured — set it in /pr config");
    }

    private void OnAuthOk()
    {
        this.Authenticated = true;
        this.AuthMessage = "";
        this.log.Info("authenticated with backend");
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
        this.Authenticated = false;
        this.AuthMessage = "disconnected";
        this.GameId = null;
        this.State = null;
        this.PendingSpin = null;
        this.AwaitingDice = false;
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

    public void AddPlayer(string name, string displayName, long bankroll)
    {
        if (this.GameId == null) return;
        _ = this.socket.AddPlayerAsync(name, displayName, bankroll, this.GameId);
    }

    public void AdjustBalance(string playerId, long delta)
    {
        if (this.GameId == null) return;
        _ = this.socket.AdjustBalanceAsync(playerId, delta, this.GameId);
    }

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

    public void SendChat(string text)
    {
        if (this.GameId == null) return;
        _ = this.socket.SendChatAsync(this.GameId, text);
    }

    /// <summary>Player betting link for the given player.</summary>
    public string? PlayerLink(PlayerState player)
        => player.PlayerKey == null ? null : $"dexcss.github.io/PrivateRoulette/#/{this.GameId}/{player.PlayerKey}";

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
